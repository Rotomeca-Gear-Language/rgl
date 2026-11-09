using Rgl.AST.Files;
using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Native;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Cli;

/// <summary>
/// <c>rgl check</c> : le bilan de l'intégration C++ du projet. Pour chaque fichier <c>forge: native</c>, ce que les liaisons ont converti et ce
/// qu'elles ont laissé de côté, avec la raison. Un symbole non converti que le code RGL utilise est une erreur (E019) ; un symbole non converti et
/// inutilisé n'est signalé qu'avec <c>--all</c>.
/// </summary>
/// <remarks>
/// Par défaut le rapport n'échoue pas : il informe. Avec <c>--strict</c>, un symbole utilisé et non converti fait échouer la commande, pour
/// l'intégration continue.
/// </remarks>
internal static class CheckCommand
{
    /// <summary>Un symbole d'un fichier <c>forge: native</c> que les liaisons n'ont pas converti.</summary>
    private sealed record Symbol(string Module, string File, string Name, string Reason);

    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var project = RglProject.Load(commandLine.File, commandLine.File is null ? commandLine.Directory ?? "." : null, context.WorkingDirectory, diagnostics);
        if (project is null) return CliRunner.Failure;

        var entry = SourceText.FromPath(project.EntryFile, diagnostics);
        if (entry is null) return CliRunner.Failure;

        // Les notices de la lecture des en-têtes (« pas encore pris en charge ») sont dites par le bilan : seules les erreurs et les avertissements passent.
        var loading = new DiagnosticBag();
        var modules = SourceGraph.Load(entry, project.RootDirectory, loading, project.Manifest?.Exclude, toolchain: context.Toolchain, nativeCache: Path.Combine(context.CacheRoot, "native"), nativePolicy: new NativePolicy(project.Manifest, project.RootDirectory));
        foreach (var item in loading.Items.Where(d => d.Level != DiagnosticLevel.Info)) diagnostics.Add(item);
        if (modules is null) return CliRunner.Failure;

        var natives = modules.Where(m => m.Native is not null).ToList();
        if (natives.Count == 0)
        {
            context.Out.WriteLine("Aucune bibliothèque C dans le projet : rien à vérifier.");
            return CliRunner.Success;
        }

        List<Symbol> symbols = [];
        foreach (var module in natives)
        {
            context.Out.WriteLine(module.Native!.Summary);
            foreach (var item in module.Native.Unconverted ?? [])
                symbols.Add(new Symbol(module.Name, Path.GetFileName(module.Unit.Span.Source.Path), item.Name, item.Reason));
        }

        var used = Uses(modules, natives, symbols, diagnostics);
        var unused = symbols.Count - symbols.Select(s => (s.Module, s.Name)).Distinct().Count(k => used.Contains(k));

        context.Out.WriteLine(used.Count == 0
            ? "Aucun symbole utilisé n'est resté sans liaison."
            : $"{used.Count} symbole(s) utilisé(s) sans liaison (E019).");

        if (commandLine.All)
            foreach (var symbol in symbols.Where(s => !used.Contains((s.Module, s.Name))))
                diagnostics.Info($"« {symbol.Name} » ({symbol.File}) n'est pas converti, mais rien ne l'utilise : {symbol.Reason}", symbol.File);
        else if (unused > 0)
            context.Out.WriteLine($"{unused} symbole(s) non converti(s) et inutilisé(s) : « rgl check --all » les liste.");

        return commandLine.Strict && used.Count > 0 ? CliRunner.Failure : CliRunner.Success;
    }

    /// <summary>
    /// Les symboles non convertis que le code du projet nomme : <c>Module.nom</c>, ou <c>nom</c> seul quand l'import le donne (<c>only { nom }</c>).
    /// Chaque emploi est une erreur E019 à sa place dans le source.
    /// </summary>
    private static HashSet<(string Module, string Name)> Uses(IReadOnlyList<SourceModule> modules, List<SourceModule> natives, List<Symbol> symbols, DiagnosticBag diagnostics)
    {
        HashSet<(string, string)> used = [];
        foreach (var module in modules)
        {
            if (module.Native is not null || module.Name.StartsWith("std.", StringComparison.Ordinal) || module.Imports is null) continue;

            var tokens = new Lexer(module.Unit.Span.Source, new DiagnosticBag()).Tokenize().Tokens;
            foreach (var import in module.Unit.Imports)
            {
                if (!module.Imports.TryGetValue(import, out var targets)) continue;
                foreach (var native in natives.Where(n => targets.Contains(n.Name)))
                {
                    var own = symbols.Where(s => s.Module == native.Name).ToList();
                    var visible = import.Only?.ToDictionary(n => n.Alias?.Name ?? n.Name.Name, n => n.Name.Name) ?? [];

                    for (var i = 0; i < tokens.Count; ++i)
                    {
                        if (tokens[i].Kind != TokenKind.Identifier || (tokens[i].Span.Start >= import.Span.Start && tokens[i].Span.End <= import.Span.End)) continue;

                        // Module.nom, avec le nom local du fichier importé ; ou nom seul, quand « only » le rend visible.
                        var qualified = i >= 2 && tokens[i - 1].Kind == TokenKind.Dot && tokens[i - 2].Kind == TokenKind.Identifier && tokens[i - 2].Text == import.LocalModuleName;
                        var name = qualified ? tokens[i].Text : visible.GetValueOrDefault(tokens[i].Text);
                        if (name is null || own.FirstOrDefault(s => s.Name == name) is not { } symbol) continue;

                        used.Add((symbol.Module, symbol.Name));
                        diagnostics.Error(tokens[i].Span, DiagnosticCodes.NativeSymbolUnconverted, $"« {symbol.Name} » n'est pas converti : {symbol.Reason}", "pas de liaison RGL",
                            [$"la déclaration est dans {symbol.File}", "la fonction n'a pas de gear RGL : aucune liaison à la main n'existe encore pour la remplacer"]);
                    }
                }
            }
        }
        return used;
    }
}
