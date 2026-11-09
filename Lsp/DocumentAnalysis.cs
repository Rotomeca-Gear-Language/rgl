using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Ce que l'analyse d'un document donne à l'éditeur.</summary>
/// <param name="Source">Le texte du document.</param>
/// <param name="Diagnostics">Ses diagnostics.</param>
/// <param name="MutArguments">
/// Les arguments passés à un paramètre <c>mut</c> : l'éditeur écrit <c>mut</c> devant eux, grisé, puisque
/// rien ne l'écrit à l'appel. Vide tant que le document a une erreur.
/// </param>
/// <param name="Hints">
/// Les autres indications écrites dans le texte, grisées : le type déduit d'un <c>var</c> (après son nom) et les points où le gear peut se suspendre
/// ou se bloquer (<c>await</c>, attente d'un gear <c>@sync</c>). Vides tant que le document a une erreur.
/// </param>
internal sealed record DocumentReport(SourceText Source, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<SourceSpan> MutArguments, IReadOnlyList<DocumentHint>? Hints = null)
{
    /// <summary>Les indications ; vide s'il n'y en a pas.</summary>
    public IReadOnlyList<DocumentHint> Hints { get; init; } = Hints ?? [];
}

/// <summary>Une indication grisée de l'éditeur.</summary>
/// <param name="Offset">L'endroit où elle s'écrit.</param>
/// <param name="Label">Son texte.</param>
/// <param name="IsType">Vrai pour un type (genre « type »), faux pour une indication de suspension.</param>
internal sealed record DocumentHint(SourceSpan Offset, string Label, bool IsType);

/// <summary>
/// Les diagnostics d'un document ouvert dans l'éditeur, avec les mêmes étapes que la compilation :
/// lexer, parseur, puis binder. Comme en compilation, une étape ne s'exécute que si la précédente
/// n'a signalé aucune erreur, pour ne pas montrer d'erreurs en cascade. Un fichier <c>.forge</c>
/// n'est que lu et analysé : il n'a pas de train à lier.
/// </summary>
internal static class DocumentAnalysis
{
    /// <summary>Le texte du document, ses diagnostics, et ce que l'éditeur en montre en plus.</summary>
    /// <param name="path">Le chemin du document, pour son extension et le <c>rgl.json</c> voisin.</param>
    /// <param name="content">Son contenu, tel que l'éditeur le montre (pas forcément enregistré).</param>
    /// <param name="openTexts">Le texte des fichiers ouverts dans l'éditeur, par chemin complet : les fichiers importés sont lus comme l'éditeur les montre.</param>
    public static DocumentReport Analyze(string path, string content, IReadOnlyDictionary<string, string>? openTexts = null)
    {
        var source = new SourceText(path, content);
        var diagnostics = new DiagnosticBag();

        var isEntry = path.EndsWith(RglProject.EntryExtension, StringComparison.OrdinalIgnoreCase);
        if (!isEntry)
        {
            // Un fichier .forge n'a pas de train d'entrée à lier : son en-tête et son contenu sont vérifiés.
            var tokens = new Lexer(source, diagnostics).Tokenize();
            if (diagnostics.HasErrors) return new DocumentReport(source, diagnostics.Items, []);

            var unit = new Parser(tokens, diagnostics).Parse();
            if (!diagnostics.HasErrors) FileChecks.Check(unit, isEntry: false, diagnostics);
            if (!diagnostics.HasErrors) _BindWithProject(path, content, unit, diagnostics, openTexts);
            return new DocumentReport(source, diagnostics.Items, []);
        }

        // Le fichier d'entrée, tel que l'éditeur le montre, avec les .forge qu'il importe, lus sur le disque.
        // Un document dont l'adresse n'est pas celle d'un fichier local (non enregistré, distant) n'a pas de dossier de projet : on lui en donne un qui
        // n'existe pas, donc sans fichiers à importer, mais la std reste visible.
        var manifest = _Manifest(path);
        var modules = SourceGraph.Load(source, _ProjectRoot(path), diagnostics, manifest?.Exclude, openTexts, NativeEditorModel.Toolchain, NativeEditorModel.CacheDirectory, new NativePolicy(manifest, _ProjectRoot(path)));
        if (modules is null) return new DocumentReport(source, diagnostics.Items, []);

        var program = new Binder(diagnostics, manifest?.Overflow ?? OverflowMode.Checked,
            manifest is null ? ProjectManifest.DefaultRecursionLimit : manifest.RecursionLimit, manifest?.Safe ?? false, manifest?.BoundedLoops ?? false, new NativePolicy(manifest, _ProjectRoot(path))).Bind(modules);
        return program is null
            ? new DocumentReport(source, diagnostics.Items, [])
            : new DocumentReport(source, diagnostics.Items, _MutArguments(program, source), _Hints(program, source));
    }

    /// <summary>
    /// Un fichier <c>.forge</c> n'a pas de train à lier, mais ses erreurs de sens (un nom inconnu, un type qui ne convient pas, une extension
    /// invalide) se voient comme dans un langage compilé : il est lié dans son projet, par un fichier d'entrée que l'éditeur invente et qui
    /// l'importe. Les diagnostics des autres fichiers sont rendus avec ce document ; ceux de l'entrée inventée ne le sont jamais. Un fichier hors de
    /// tout projet, ou à l'intérieur d'une box (dont l'index seul est importé de l'extérieur), garde son analyse de syntaxe.
    /// </summary>
    private static void _BindWithProject(string path, string content, AST.CompilationUnit unit, DiagnosticBag diagnostics, IReadOnlyDictionary<string, string>? openTexts)
    {
        if (!Path.IsPathRooted(path) || Path.GetDirectoryName(path) is not { } directory || !Directory.Exists(directory)) return;

        string? root = null;
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, ProjectManifest.FileName)))
            {
                root = current;
                break;
            }
        }
        if (root is null) return;

        var kind = unit.Header?.Kind?.Name;
        var inBox = Path.GetRelativePath(root, directory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.EndsWith(".box", StringComparison.OrdinalIgnoreCase));
        if (kind is null || inBox && kind != "extend") return;

        // Un fichier forge: extend s'applique sans import ; un autre est importé par son nom.
        var entryPath = Path.Combine(root, "__editeur_rgl__.rgl");
        var entryText = kind == "extend" ? "!train |> done;" : $"import {Path.GetFileNameWithoutExtension(path)};\n!train |> done;";
        var manifest = _Manifest(Path.Combine(root, "x"));
        var texts = new Dictionary<string, string>(openTexts ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(path)] = content };

        var project = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(entryPath, entryText), root, project, manifest?.Exclude, texts, NativeEditorModel.Toolchain, NativeEditorModel.CacheDirectory, new NativePolicy(manifest, root));
        if (modules is not null)
            new Binder(project, manifest?.Overflow ?? OverflowMode.Checked, manifest is null ? ProjectManifest.DefaultRecursionLimit : manifest.RecursionLimit, manifest?.Safe ?? false, manifest?.BoundedLoops ?? false, new NativePolicy(manifest, root)).Bind(modules);

        foreach (var diagnostic in project.Items)
        {
            if (diagnostic.Span is { } span && string.Equals(span.Source.Path, entryPath, StringComparison.OrdinalIgnoreCase)) continue;
            // Le fichier est lu deux fois (seul, puis dans son projet) : ce que la première lecture a déjà signalé ne l'est pas deux fois.
            if (diagnostics.Items.Any(d => d.Code == diagnostic.Code && d.Message == diagnostic.Message && d.Span?.Start == diagnostic.Span?.Start
                && string.Equals(d.Span?.Source.Path, diagnostic.Span?.Source.Path, StringComparison.OrdinalIgnoreCase))) continue;
            diagnostics.Add(diagnostic);
        }
    }

    /// <summary>Les arguments passés à un paramètre <c>mut</c>, dans les gears et leurs lambdas, dans l'ordre du source.</summary>
    private static IReadOnlyList<SourceSpan> _MutArguments(IrProgram program, SourceText source)
    {
        var spans = new List<SourceSpan>();
        var bodies = new Queue<IrBlock>(program.Gears.Select(g => g.Body));
        while (bodies.Count > 0)
        {
            foreach (var expression in IrWalk.AllExpressions(bodies.Dequeue()))
            {
                if (expression is IrLambda lambda) bodies.Enqueue(lambda.Body);
                // L'élément d'un parcours qui modifie sur place n'est écrit nulle part.
                if (expression is IrCall call) spans.AddRange(IrMutation.MutableArguments(call).Where(a => a is not IrElementValue).Select(a => a.Span));
            }
        }
        // Seuls ceux du document ouvert : les fichiers importés ont les leurs.
        return [.. spans.Where(s => s.Source == source).OrderBy(s => s.Start.Value)];
    }

    /// <summary>
    /// Le type déduit de chaque <c>var</c> (indiqué à la fin du nom) et chaque point de suspension ou de blocage d'un gear asynchrone, dans l'ordre du source.
    /// </summary>
    private static IReadOnlyList<DocumentHint> _Hints(IrProgram program, SourceText source)
    {
        var hints = new List<DocumentHint>();
        var bodies = new Queue<IrBlock>(program.Gears.Select(g => g.Body));
        while (bodies.Count > 0)
        {
            var body = bodies.Dequeue();
            foreach (var statement in IrWalk.Statements(body))
                if (statement is IrVariableDeclaration { Variable: { IsConstant: false } variable } && variable.Declaration.Source == source && _IsInferred(source, variable.Declaration))
                    hints.Add(new DocumentHint(variable.Declaration, ": " + variable.Type, true));
            foreach (var expression in IrWalk.AllExpressions(body))
            {
                if (expression is IrLambda lambda) bodies.Enqueue(lambda.Body);
                if (expression.Span.Source != source) continue;
                if (expression is IrAwait) hints.Add(new DocumentHint(expression.Span, "⏸ peut se suspendre", false));
                else if (expression is IrBlockOn) hints.Add(new DocumentHint(expression.Span, "⏳ bloque jusqu'à la fin", false));
            }
        }
        return [.. hints.OrderBy(h => h.Offset.Start.Value)];
    }

    /// <summary>Vrai quand le mot qui précède le nom est <c>var</c> : le type n'est pas écrit.</summary>
    private static bool _IsInferred(SourceText source, SourceSpan name)
    {
        var text = source.Content[..(int)name.Start.Value].TrimEnd();
        return text.EndsWith("var", StringComparison.Ordinal) && (text.Length == 3 || !char.IsLetterOrDigit(text[^4]) && text[^4] != '_');
    }

    /// <summary>Le dossier d'un document qui est un fichier local (chemin complet) ; <see langword="null"/> pour une adresse qui n'en est pas un.</summary>
    private static string? _LocalDirectory(string path) =>
        Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) is { Length: > 0 } directory ? directory : null;

    /// <summary>Le dossier où chercher les fichiers importés : celui du document, sinon un dossier qui n'existe pas.</summary>
    private static string _ProjectRoot(string path)
    {
        var directory = _LocalDirectory(path);
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(Path.Combine(current, ProjectManifest.FileName))) return current;
        return directory ?? Path.Combine(Path.GetTempPath(), "rgl-document-sans-dossier");
    }

    /// <summary>
    /// Le <c>rgl.json</c> du dossier du document, s'il existe et se lit : ses erreurs à lui ne
    /// concernent pas le document, la compilation les signale.
    /// </summary>
    private static ProjectManifest? _Manifest(string path)
    {
        var directory = _ProjectRoot(path);

        var manifest = Path.Combine(directory, ProjectManifest.FileName);
        return File.Exists(manifest) ? ProjectManifest.Load(manifest, new DiagnosticBag()) : null;
    }
}
