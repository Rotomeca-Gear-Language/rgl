using System.Text.RegularExpressions;
using Rgl.AST.Expressions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Molds;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>La sorte d'une proposition, avec la valeur que lui donne le protocole des éditeurs.</summary>
internal enum CompletionKind
{
    Function = 3,
    Field = 5,
    Variable = 6,
    Class = 7,
    Method = 2,
    Module = 9,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    TypeParameter = 25,
}

/// <summary>Un texte à insérer à une position du document (ligne et colonne en unités UTF-16, à partir de 0).</summary>
internal sealed record TextInsertion(uint Line, uint Character, string Text);

/// <summary>Une proposition d'autocomplétion.</summary>
/// <param name="Label">Le texte proposé.</param>
/// <param name="Kind">Sa sorte, qui choisit l'icône de l'éditeur.</param>
/// <param name="Detail">Ce qu'il est : signature d'un gear, type d'un champ… ; <see langword="null"/> sans détail.</param>
/// <param name="Documentation">Le Markdown montré à côté de la liste : la déclaration et son commentaire.</param>
/// <param name="SortText">L'ordre de la liste : ce qui est le plus proche vient en premier.</param>
/// <param name="Source">Le fichier d'où vient un nom importé ou à importer, écrit à droite du libellé.</param>
/// <param name="Edits">Ce que choisir la proposition ajoute ailleurs dans le document : l'import d'un nom d'un autre fichier.</param>
/// <param name="Command">Une commande de l'éditeur lancée après l'insertion (<c>editor.action.triggerSuggest</c> rouvre la liste) ; <see langword="null"/> sans.</param>
/// <param name="Deprecated">Vrai pour un gear obsolète (<c>@deprecated</c>) : l'éditeur barre la proposition.</param>
/// <param name="InsertText">Ce que choisir la proposition écrit, un snippet de l'éditeur (<c>sum()</c>, <c>indexOf($0)</c>) ; <see langword="null"/> pour le libellé seul.</param>
internal sealed record CompletionItem(
    string Label,
    CompletionKind Kind,
    string? Detail = null,
    string? Documentation = null,
    string? SortText = null,
    string? Source = null,
    IReadOnlyList<TextInsertion>? Edits = null,
    string? InsertText = null,
    bool Deprecated = false,
    string? Command = null);

/// <summary>
/// L'autocomplétion, comme dans un éditeur de C# ou de JavaScript : elle connaît ce qui est visible à l'endroit du curseur
/// (variables du bloc, paramètres, déclarations du fichier, noms importés), le type de ce qui précède un point (champs d'un mold,
/// variantes d'un enum, membres d'un tableau, noms d'un fichier importé), et propose aussi ce que déclarent les autres fichiers du
/// projet : choisir un tel nom ajoute l'<c>import</c> qui manque. Elle lit le fichier même s'il contient des erreurs : le
/// parseur se rattrape toujours.
/// </summary>
internal static partial class CompletionProvider
{
    /// <summary>Les mots contextuels : des identificateurs pour le lexer, des mots-clés pour qui écrit.</summary>
    private static readonly string[] _contextualWords = ["done", "panic", "ok", "err", "eval", "all", "accept", "error", "console", "raw", "cancel", "task", "by", "with", "only", "extend", "namespace", "export", "this", "dataset", "annotation"];

    /// <summary>Les genres de fichier <c>.forge</c>.</summary>
    private static readonly string[] _forgeKinds = ["train", "mold", "gears", "extend", "index", "native", "precast", "descriptor"];

    private const string Placeholder = "__rgl__";

    /// <summary>Les propositions à la position donnée, pour un fichier hors de tout projet.</summary>
    /// <param name="content">Le texte du document.</param>
    /// <param name="line">La ligne, à partir de 0.</param>
    /// <param name="character">La colonne en unités UTF-16, à partir de 0.</param>
    public static IReadOnlyList<CompletionItem> Complete(string content, uint line, uint character) =>
        Complete("document.rgl", content, line, character, new ProjectContext("", "", []));

    /// <summary>Les propositions à la position donnée.</summary>
    /// <param name="path">Le chemin du document.</param>
    /// <param name="content">Son texte, tel que l'éditeur le montre.</param>
    /// <param name="line">La ligne, à partir de 0.</param>
    /// <param name="character">La colonne en unités UTF-16, à partir de 0.</param>
    /// <param name="project">Les fichiers du projet que le document peut importer.</param>
    public static IReadOnlyList<CompletionItem> Complete(string path, string content, uint line, uint character, ProjectContext project)
    {
        var document = SemanticDocument.Create(path, content, project);
        var offset = document.OffsetOf(line, character);
        if (document.Self.Tokens.Any(t => t.Kind == TokenKind.CppBlock && t.Span.Start.Value < offset && offset < t.Span.End.Value)) return [];
        var wordStart = offset;
        while (wordStart > 0 && SyntaxFacts.IsIdentifierPart(content[wordStart - 1])) --wordStart;

        var lineStart = (int)document.Source.LineStart[(int)Math.Min(line, (uint)document.Source.LineStart.Count - 1)].Value;
        var prefix = content[lineStart..offset];

        if (prefix.EndsWith('/')) return _DocumentationSkeleton(document, prefix, offset);
        if (wordStart > 0 && content[wordStart - 1] == '@') return _Annotations(document);
        if (_Directive(document, prefix) is { } directive) return directive;
        if (wordStart > 0 && content[wordStart - 1] == '.' && !(wordStart > 1 && content[wordStart - 2] == '.')) return _Members(path, content, project, document, offset, wordStart);
        if (_HeaderWords(path, document, prefix, wordStart) is { } header) return header;
        if (_MoldFields(path, content, project, offset, wordStart) is { } fields) return fields;
        return _Everything(document, offset, wordStart);
    }

    #region En-tête d'un fichier forge

    /// <summary>
    /// En tête d'un fichier <c>.forge</c>, avant tout import ou déclaration : <c>forge</c> (qui écrit <c>forge: </c> et rouvre la liste des genres de fichier) tant que le
    /// genre n'est pas écrit, puis <c>description</c> (qui écrit <c>description: ''</c>, le curseur entre les guillemets) tant qu'elle manque. <see langword="null"/> ailleurs.
    /// </summary>
    private static IReadOnlyList<CompletionItem>? _HeaderWords(string path, SemanticDocument document, string prefix, int wordStart)
    {
        // Le mot est seul sur sa ligne : seulement du blanc avant lui, et lui-même un début d'identificateur.
        if (!path.EndsWith(".forge", StringComparison.OrdinalIgnoreCase) || !prefix.Trim().All(SyntaxFacts.IsIdentifierPart)) return null;

        var header = document.Unit.Header;
        var headerEnd = header?.Span.End.Value ?? 0;
        // Rien d'autre que l'en-tête avant le mot : ni import, ni déclaration, ni commentaire de code.
        if (document.Self.Tokens.Any(t => t.Kind != TokenKind.EndOfFile && t.Span.Start.Value < (uint)wordStart && t.Span.Start.Value >= headerEnd)) return null;
        if (document.Unit.Statements.Any(s => s.Span.Start.Value < (uint)wordStart)) return null;

        var items = new List<CompletionItem>();
        if (header?.Kind is not { IsMissing: false })
        {
            items.Add(new CompletionItem("forge", CompletionKind.Keyword, "en-tête : le genre du fichier", KeywordDocumentation.For("forge"), "0_forge", InsertText: "forge: ", Command: "editor.action.triggerSuggest"));
        }
        else if (header.Description is null)
        {
            items.Add(new CompletionItem("description", CompletionKind.Keyword, "en-tête : la description du fichier", KeywordDocumentation.For("description"), "0_description", InsertText: "description: '$0'"));
        }
        return items.Count == 0 ? null : items;
    }

    #endregion

    #region Annotations

    /// <summary>Les annotations du langage, puis celles que le programme déclare.</summary>
    private static IReadOnlyList<CompletionItem> _Annotations(SemanticDocument document)
    {
        (string Name, string Insert, string Detail)[] builtIn =
        [
            ("before", "before($0)", "contrôle exécuté avant le gear"),
            ("tap", "tap($0)", "effet de bord avant ou après le gear"),
            ("deprecated", "deprecated(\"$0\")", "gear obsolète"),
            ("async", "async", "coroutine"),
            ("sync", "sync", "attend les opérations asynchrones"),
            ("fire", "fire", "coroutine détachée"),
            ("task", "task", "exécuté sur le pool de threads"),
            ("trusted", "trusted", "frontière native de confiance"),
        ];
        var items = builtIn.Select(a => new CompletionItem(a.Name, CompletionKind.Keyword, a.Detail, null, "1_" + a.Name, null, null, a.Insert)).ToList();
        foreach (var annotation in document.AnnotationSymbols())
        {
            var declaration = (Rgl.AST.Notes.AnnotationDeclaration)annotation.Node!;
            var insert = declaration.Parameters.Count == 0 ? annotation.Name : annotation.Name + "($0)";
            items.Add(new CompletionItem(annotation.Name, CompletionKind.Function, Signatures.Of(annotation), annotation.Documentation, "0_" + annotation.Name,
                annotation.Module is { } module && module != document.Self ? Path.GetFileName(module.Path) : null, null, insert));
        }
        return items;
    }

    #endregion

    #region Documentation

    /// <summary>
    /// Après <c>///</c> au-dessus d'une déclaration encore sans documentation : le squelette du commentaire, avec une balise pour chaque paramètre,
    /// paramètre générique, résultat et échec possible. Après une autre barre, rien : la proposition ne s'ouvre pas sur une division.
    /// </summary>
    private static IReadOnlyList<CompletionItem> _DocumentationSkeleton(SemanticDocument document, string prefix, int offset)
    {
        if (prefix.Trim() != "///") return [];

        // La déclaration est ce qui suit le commentaire ; son commentaire ne doit pas déjà avoir d'autre ligne.
        var next = document.Self.Tokens.FirstOrDefault(t => t.Span.Start.Value >= offset);
        if (next is null || Docs.DocExtractor.Before(next) is not { Lines.Count: 1 }) return [];

        var entry = Docs.DocCollector.Collect(document.Self.Name, document.Self.Unit, document.Self.Tokens).FirstOrDefault(e => e.Declaration.Start.Value == next.Span.Start.Value);
        if (entry is null) return [];

        var snippet = Docs.DocSkeleton.For(entry);
        return [new CompletionItem("/// documentation", CompletionKind.Snippet, "squelette XML", "Écrit le résumé et une balise pour chaque paramètre, paramètre générique, résultat et échec.", "0", InsertText: snippet)];
    }

    #endregion

    #region Imports et en-tête

    /// <summary>Les lignes <c>forge:</c> et <c>import</c> : les genres de fichier, les fichiers à importer, les noms d'un fichier.</summary>
    private static IReadOnlyList<CompletionItem>? _Directive(SemanticDocument document, string prefix)
    {
        if (Regex.Match(prefix, @"^\s*forge\s*:\s*\w*$") is { Success: true })
            return [.. _forgeKinds.Select(k => new CompletionItem(k, CompletionKind.EnumMember, "genre de fichier"))];

        // dataset Moves | : of, as ou from ; dataset Moves from "x.json" | : of ou as.
        if (Regex.Match(prefix, @"^\s*(?:export\s+)?dataset\s+\w+\s+(?<from>from\s+(?:""[^""]*""|'[^']*'))?\s*\w*$") is { Success: true } header)
        {
            var words = header.Groups["from"].Success
                ? new[] { ("of", "lit les entrées comme un mold déclaré (erreur s'il est inconnu)"), ("as", "crée le mold depuis les entrées") }
                : [("of", "lit les entrées comme un mold déclaré (erreur s'il est inconnu)"), ("as", "crée le mold depuis les entrées"), ("from", "lit les entrées d'un fichier JSON")];
            return [.. words.Select(w => new CompletionItem(w.Item1, CompletionKind.Keyword, "dataset", w.Item2, "0_" + w.Item1))];
        }

        // dataset Moves of | , dataset Moves as | : le mold des entrées.
        if (Regex.Match(prefix, @"^\s*(?:export\s+)?dataset\s+\w+(?:\s+from\s+(?:""[^""]*""|'[^']*'))?\s+(?:of|as)\s+\w*$") is { Success: true })
            return [.. document.Self.Symbols.Values.Concat(document.ImportedNames.Values).Where(s => s.Kind == RglSymbolKind.Mold).DistinctBy(s => s.Name).Select(s => _Item(document, s, "0_"))];

        // use <Math> only {…} : les noms du fichier ou de la box de la std ; use <… : les noms de la std.
        if (Regex.Match(prefix, @"^\s*use\s*<(?<module>\w+)>(?:\s+as\s+\w+)?\s+only\s*\{(?<done>[^}]*)$") is { Success: true } stdOnly)
        {
            var stdModule = document.Project.FindStd(stdOnly.Groups["module"].Value);
            if (stdModule is null) return [];
            var chosen = stdOnly.Groups["done"].Value.Split(',').Select(n => Regex.Replace(n.Trim(), @"\s+as\s+\w+$", "")).ToHashSet(StringComparer.Ordinal);
            return [.. stdModule.Exported.Values.Where(s => s.IsTopLevel && !chosen.Contains(s.Name)).Select(s => _Item(document, s, "0_"))];
        }

        if (Regex.Match(prefix, @"^\s*use\s*<\w*$") is { Success: true })
            return [.. (document.Project.Std ?? []).Select(m => new CompletionItem(m.Name, CompletionKind.Module, "la std", _ModuleDocumentation(m), "0_" + m.Name, Path.GetFileName(m.Path)))];

        if (Regex.Match(prefix, @"^\s*import\s+(?:\[\.\.\]\s+)?(?:(?:box|tools)\s+)?(?<module>[\w.]+)(?:\s+as\s+\w+)?\s+only\s*\{(?<done>[^}]*)$") is { Success: true } only)
        {
            var module = document.Project.Find(only.Groups["module"].Value);
            if (module is null) return [];
            var listed = only.Groups["done"].Value.Split(',').Select(n => Regex.Replace(n.Trim(), @"\s+as\s+\w+$", "")).ToHashSet(StringComparer.Ordinal);
            return [.. module.Exported.Values.Where(s => s.IsTopLevel && !listed.Contains(s.Name)).Select(s => _Item(document, s, "0_"))];
        }

        if (Regex.Match(prefix, @"^\s*import\s+(?<kind>\[\.\.\]\s+|box\s+|tools\s+)?(?<word>[\w.]*)$") is { Success: true } import)
        {
            var kind = import.Groups["kind"].Value.Trim();
            var word = import.Groups["word"].Value;
            if (kind == "tools") return _ToolsNames(document, word);

            var items = document.Project.Modules.Where(m => kind != "box")
                .Select(m => new CompletionItem(m.Name, CompletionKind.Module, m.Kind is null ? "fichier" : $"forge: {m.Kind}", _ModuleDocumentation(m), "0_" + m.Name, Path.GetFileName(m.Path)))
                .ToList();
            if (kind.Length == 0 && word.Length > 0 || kind.Length == 0) items.AddRange(new[] { "box", "tools" }.Select(k => new CompletionItem(k, CompletionKind.Keyword, "genre d'import", SortText: "5_" + k)));
            return items;
        }

        return null;
    }

    /// <summary>Les dossiers <c>.tools</c> du projet, puis, après <c>Dossier.</c>, les fichiers qu'ils contiennent.</summary>
    private static IReadOnlyList<CompletionItem> _ToolsNames(SemanticDocument document, string word)
    {
        var folders = document.Project.Modules
            .Select(m => (Module: m, Folder: Path.GetDirectoryName(m.Path)!.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).LastOrDefault(s => s.EndsWith(".tools", StringComparison.OrdinalIgnoreCase))))
            .Where(f => f.Folder is not null)
            .ToArray();

        var dot = word.IndexOf('.');
        if (dot < 0) return [.. folders.Select(f => f.Folder![..^".tools".Length]).Distinct().Select(n => new CompletionItem(n, CompletionKind.Module, "dossier tools"))];

        var wanted = word[..dot] + ".tools";
        return [.. folders.Where(f => f.Folder!.Equals(wanted, StringComparison.OrdinalIgnoreCase)).Select(f => new CompletionItem(f.Module.Name, CompletionKind.Module, f.Module.Kind is null ? "fichier" : $"forge: {f.Module.Kind}"))];
    }

    private static string _ModuleDocumentation(ModuleInfo module) =>
        $"Fichier `{Path.GetFileName(module.Path)}`" + (module.Kind is null ? "" : $" — `forge: {module.Kind}`") + (module.Description is null ? "" : $"\n\n{module.Description}");

    #endregion

    #region Après un point

    /// <summary>
    /// Après <c>x.</c> : les variantes si <c>x</c> est un enum, les noms d'un fichier importé, sinon les champs et membres du type
    /// de <c>x</c>. Le type de <c>x</c> vient de l'arbre du document, où le mot en cours est remplacé par un nom provisoire quand
    /// il est vide, pour que l'accès existe.
    /// </summary>
    private static IReadOnlyList<CompletionItem> _Members(string path, string content, ProjectContext project, SemanticDocument original, int offset, int wordStart)
    {
        var typed = content[wordStart..offset];
        var text = typed.Length == 0 ? content.Insert(offset, Placeholder) : content;
        var document = typed.Length == 0 ? SemanticDocument.Create(path, text, project) : original;

        var access = document.PathAt(wordStart, inclusiveEnd: true).OfType<MemberAccessExpression>().LastOrDefault(m => m.Member.Span.Start.Value == (uint)wordStart);
        if (access is null) return _QualifiedMembers(document, content, wordStart) ?? _TypeMembers(document, wordStart);

        // std. : tous les noms de la std, à plat, sans rien importer.
        if (access.Target is NameExpression { Name.Name: "std" } && document.LocalsAt(wordStart).All(l => l.Name != "std") && !document.ImportedModules.ContainsKey("std"))
            return [.. project.StdNames.Values.Select(s => _WithCall(_Item(document, s, "0_"), s))];

        // Moves. : les entrées d'un dataset, le type de ses identifiants, sa longueur et find.
        if (document.DatasetOf(access.Target, wordStart) is { } dataset)
            return [.. document.DatasetMembers(dataset).Select(m => _WithCall(_Item(document, m, m.Kind == RglSymbolKind.Variant ? "0_" : "1_"), m))];

        // int. : les bornes d'un type numérique.
        if (access.Target is NameExpression { Name.Name: var numeric } && SemanticDocument.NumericTypes.Contains(numeric) && document.LocalsAt(wordStart).All(l => l.Name != numeric))
            return [.. SemanticDocument.NumericLimits(numeric).Select(s => _Item(document, s, "0_"))];

        // Direction. , line. : un type nomme ses variantes (enum) et ce que ses extensions lui ajoutent (constantes, gears sans this).
        if (document.AsTypeSymbol(access.Target, wordStart) is { IsType: true } named && named.Kind != RglSymbolKind.TypeParameter)
        {
            var statics = DescriptorEditorModel.Members(named, true, SemanticDocument.SplitGeneric(access.Target.Span.Text).Arguments).Select(s => _WithCall(_Item(document, s, "0_"), s)).ToList();
            if (named.Kind == RglSymbolKind.Enum) statics.AddRange(document.VariantsOf(named).Select(v => _Item(document, v, "0_")));
            statics.AddRange(ExtensionMethods.StaticMembers(project, named.Name, document).Select(s => _WithCall(_Item(document, s, "1_") with { Source = "extension · " + Path.GetFileName(s.Module?.Path ?? "") }, s)));
            return statics;
        }

        // Geometry. : un fichier importé.
        if (document.ResolveExpression(access.Target, wordStart) is { Kind: RglSymbolKind.Enum } enumeration) return [.. document.VariantsOf(enumeration).Select(v => _Item(document, v, "0_"))];
        if (access.Target is NameExpression qualifier && document.QualifiedNames(qualifier.Name.Name) is { } names && document.LocalsAt(wordStart).All(l => l.Name != qualifier.Name.Name))
            return [.. names.Values.Select(s => _Item(document, s, "0_"))];

        if (document.TypeOf(access.Target, wordStart) is not { } type) return _AllMembers(document);
        var members = document.MembersOf(SemanticDocument.StripNullable(type), wordStart);
        var items = members.Select(m => _WithCall(_Item(document, m, "0_"), m)).ToList();

        // Les extensions s'appliquent à toute valeur de leur type, après ses propres membres.
        var own = items.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
        items.AddRange(ExtensionMethods.For(project, type, document).Where(m => !own.Contains(m.Name)).DistinctBy(m => m.Name).Select(_ExtensionItem));
        return items.Count == 0 && !SemanticDocument.IsArray(SemanticDocument.StripNullable(type), out _, out _) ? [] : items;
    }

    /// <summary>
    /// Après le point d'un nom qualifié que le parseur lit d'un bloc (<c>|> Home.mount</c>, une étape de train ; <c>Geometry.Vec2</c>, un type) : les noms du fichier
    /// importé que désigne ce qui précède le point. <see langword="null"/> si ce n'est pas un fichier importé.
    /// </summary>
    private static IReadOnlyList<CompletionItem>? _QualifiedMembers(SemanticDocument document, string content, int wordStart)
    {
        var start = wordStart - 1;
        while (start > 0 && (SyntaxFacts.IsIdentifierPart(content[start - 1]) || content[start - 1] == '.')) --start;
        var qualifier = content[start..(wordStart - 1)];
        if (qualifier.Length == 0 || document.LocalsAt(wordStart).Any(l => l.Name == qualifier) || document.QualifiedNames(qualifier) is not { } names) return null;

        var inType = document.PathAt(wordStart, inclusiveEnd: true).OfType<Rgl.AST.Types.NamedType>().Any();
        return [.. names.Values.Where(s => !inType || s.IsType).Select(s => _Item(document, s, "0_"))];
    }

    /// <summary>
    /// Quand le type de ce qui précède le point est inconnu : rien. Proposer les champs de tous les molds et les membres de toutes les collections
    /// ne serait que du bruit : un éditeur de C# ou de TypeScript ne propose que ce qui existe.
    /// </summary>
    private static IReadOnlyList<CompletionItem> _AllMembers(SemanticDocument document) => [];

    /// <summary>
    /// Après le point d'un nom de type qualifié (<c>Moves.</c> dans <c>mold P { Moves. }</c>) : pour un dataset, <c>id</c>, le type de ses identifiants ; rien d'autre.
    /// </summary>
    private static IReadOnlyList<CompletionItem> _TypeMembers(SemanticDocument document, int wordStart)
    {
        var named = document.PathAt(wordStart, inclusiveEnd: true).OfType<Rgl.AST.Types.NamedType>().LastOrDefault(t => t.Name.Name.Contains('.'));
        if (named is null) return _AllMembers(document);

        var qualifier = named.Name.Name[..named.Name.Name.LastIndexOf('.')];
        if (document.DatasetOfIdType(qualifier + ".id", wordStart) is not { } dataset) return _AllMembers(document);
        return [.. document.DatasetMembers(dataset).Where(m => m.Name == "id").Select(m => _Item(document, m, "0_"))];
    }

    #endregion

    #region Partout ailleurs

    /// <summary>Tout ce qui peut s'écrire hors d'un accès à un membre, le plus proche d'abord ; les noms des autres fichiers s'importent en le choisissant.</summary>
    private static IReadOnlyList<CompletionItem> _Everything(SemanticDocument document, int offset, int wordStart)
    {
        var typeOnly = _IsTypeContext(document, offset, wordStart);
        var items = new List<CompletionItem>();

        if (!typeOnly && document.ResolveName("pack", offset) is null)
            items.Add(new CompletionItem("pack", CompletionKind.Function, "pack(tableau)", "Copie un tableau SoA en tableau de structures.", "1_pack", InsertText: "pack($0)"));
        var locals = document.LocalsAt(offset);
        for (var i = locals.Count - 1; i >= 0; --i)
        {
            if (!typeOnly || locals[i].IsType) items.Add(_Item(document, locals[i], "0_"));
        }

        foreach (var symbol in document.Self.Symbols.Values.Concat(document.ImportedNames.Values))
        {
            if (!typeOnly || symbol.IsType || symbol.Kind == RglSymbolKind.Dataset) items.Add(_WithCall(_Item(document, symbol, "1_"), symbol));
        }

        foreach (var (name, module) in document.ImportedModules)
        {
            if (typeOnly) continue;
            items.Add(new CompletionItem(name, module.DefaultTrain is null ? CompletionKind.Module : CompletionKind.Function, module.DefaultTrain is null ? "fichier importé" : "train par défaut",
                _ModuleDocumentation(module) + (module.DefaultTrain?.Documentation is { } trainDocumentation ? "\n\n" + trainDocumentation : ""), "2_" + name, Path.GetFileName(module.Path)));
        }

        if (!typeOnly && document.Project.Std is not null)
            items.Add(new CompletionItem("std", CompletionKind.Module, "la bibliothèque standard", "`std.nom` désigne un nom de la bibliothèque standard, sans rien importer.", "2_std"));
        items.AddRange(new[] { "Exception", "CastError", "LoopError" }.Select(e => new CompletionItem(e, CompletionKind.Struct, "erreur standard", SortText: "3_" + e)));

        var keywords = SyntaxFacts.Keywords.Where(k => !typeOnly || k.Category == KeywordCategory.PrimitiveType)
            .Select(k => new CompletionItem(k.Text, CompletionKind.Keyword, k.Category == KeywordCategory.PrimitiveType ? "type primitif" : null, KeywordDocumentation.For(k.Text), (k.Category == KeywordCategory.PrimitiveType ? "4_" : "6_") + k.Text));
        items.AddRange(keywords);
        if (!typeOnly) items.AddRange(_contextualWords.Select(w => new CompletionItem(w, CompletionKind.Keyword, Documentation: KeywordDocumentation.For(w), SortText: "6_" + w)));

        var known = items.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
        items.AddRange(_AutoImports(document, typeOnly, known));
        items.AddRange(_StdImports(document, typeOnly, known));
        return [.. items.Where(i => i.Label.Length > 0).DistinctBy(i => i.Label)];
    }

    /// <summary>Les noms déclarés par les autres fichiers du projet, que le document n'a pas importés : choisir l'un d'eux écrit son <c>import</c>.</summary>
    private static IEnumerable<CompletionItem> _AutoImports(SemanticDocument document, bool typeOnly, HashSet<string> known)
    {
        foreach (var module in document.Project.Modules)
        {
            if (!typeOnly && !known.Contains(module.Name) && !document.ImportedModules.ContainsKey(module.Name))
            {
                yield return new CompletionItem(module.Name, module.DefaultTrain is null ? CompletionKind.Module : CompletionKind.Function, $"import {module.Name}",
                    _ModuleDocumentation(module), "5_" + module.Name, Path.GetFileName(module.Path), [ImportEdit(document, module.Name, null)]);
            }

            foreach (var symbol in module.Exported.Values)
            {
                if (!symbol.IsTopLevel || known.Contains(symbol.Name) || typeOnly && !symbol.IsType && symbol.Kind != RglSymbolKind.Dataset) continue;
                var item = _WithCall(_Item(document, symbol, "5_"), symbol);
                yield return item with
                {
                    Source = Path.GetFileName(module.Path),
                    Edits = [ImportEdit(document, module.Name, symbol.Name)],
                    Documentation = item.Documentation + $"\n\n*Ajoute* `import {module.Name} only {{{symbol.Name}}};`",
                };
            }
        }
    }

    /// <summary>Les noms de la bibliothèque standard, que le document n'a pas importés : choisir l'un d'eux écrit son <c>use … only {…}</c>.</summary>
    private static IEnumerable<CompletionItem> _StdImports(SemanticDocument document, bool typeOnly, HashSet<string> known)
    {
        foreach (var module in document.Project.Std ?? [])
        {
            if (!typeOnly && !known.Contains(module.Name) && !document.ImportedModules.ContainsKey(module.Name))
            {
                yield return new CompletionItem(module.Name, CompletionKind.Module, $"use <{module.Name}>", _ModuleDocumentation(module), "7_" + module.Name, "std", [ImportEdit(document, module.Name, null, std: true)]);
            }

            foreach (var symbol in module.Exported.Values)
            {
                // Un nom qui commence par _ est privé à la std : il ne se propose pas.
                if (!symbol.IsTopLevel || symbol.Name.StartsWith('_') || known.Contains(symbol.Name) || typeOnly && !symbol.IsType && symbol.Kind != RglSymbolKind.Dataset) continue;
                var item = _WithCall(_Item(document, symbol, "7_"), symbol);
                yield return item with
                {
                    Source = "std · " + module.Name,
                    Edits = [ImportEdit(document, module.Name, symbol.Name, std: true)],
                    Documentation = item.Documentation + $"\n\n*Ajoute* `use <{module.Name}> only {{{symbol.Name}}};`",
                };
            }
        }

    }

    /// <summary>
    /// Indique si un type est attendu à cet endroit : après <c>new</c> ou <c>as</c>, entre les chevrons d'un générique, dans les
    /// paramètres et le type de retour d'un gear. Seuls les types se proposent alors.
    /// </summary>
    private static bool _IsTypeContext(SemanticDocument document, int offset, int wordStart)
    {
        var tokens = document.Self.Tokens;
        var last = -1;
        for (var i = tokens.Count - 1; i >= 0; --i)
        {
            if (tokens[i].Span.End.Value <= wordStart && tokens[i].Kind != TokenKind.EndOfFile)
            {
                last = i;
                break;
            }
        }
        if (last < 0) return false;

        var previous = tokens[last];
        if (previous.Kind is TokenKind.NewKeyword or TokenKind.AsKeyword) return true;
        if (previous.Kind is TokenKind.Question or TokenKind.Bang && last > 0 && tokens[last - 1].Kind == TokenKind.AsKeyword) return true;
        if (previous.Kind == TokenKind.Less && last > 0 && tokens[last - 1].Kind == TokenKind.Identifier
            && document.ResolveName(tokens[last - 1].Text, offset)?.Node is GearDeclaration { TypeParameters: not null } or MoldDeclaration { TypeParameters: not null }) return true;

        // Dans l'en-tête d'un gear : « gear nom(… » après « ( » ou « , », ou le type après « ): ».
        if (previous.Kind is TokenKind.OpenParen or TokenKind.Comma or TokenKind.MutKeyword) return _InGearHeader(tokens, last);
        if (previous.Kind == TokenKind.Colon && last > 1 && tokens[last - 1].Kind == TokenKind.CloseParen) return _InGearHeader(tokens, last - 2);
        return false;
    }

    /// <summary>
    /// Vrai si la parenthèse ouverte trouvée en remontant depuis <paramref name="from"/> est celle des paramètres d'un gear :
    /// <c>gear nom(</c>, <c>gear pure nom(</c>, <c>gear nom&lt;T&gt;(</c>.
    /// </summary>
    private static bool _InGearHeader(IReadOnlyList<Token> tokens, int from)
    {
        var depth = 0;
        var open = -1;
        for (var i = from; i >= 0 && open < 0; --i)
        {
            switch (tokens[i].Kind)
            {
                case TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace:
                    ++depth;
                    break;

                case TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace:
                    if (depth > 0) --depth;
                    else if (tokens[i].Kind == TokenKind.OpenParen) open = i;
                    else return false;
                    break;
            }
        }
        if (open < 1) return false;

        var before = open - 1;
        if (tokens[before].Kind == TokenKind.Greater)
        {
            while (before >= 0 && tokens[before].Kind != TokenKind.Less) --before;
            --before;
        }
        return before >= 1 && tokens[before].Kind == TokenKind.Identifier
            && tokens[before - 1].Kind is TokenKind.GearKeyword or TokenKind.GuardKeyword or TokenKind.EmitKeyword or TokenKind.PureKeyword;
    }

    #endregion

    #region Propositions

    /// <summary>La proposition d'une déclaration : son libellé, sa signature, son commentaire.</summary>
    private static CompletionItem _Item(SemanticDocument document, RglSymbol symbol, string sort)
    {
        var shown = symbol.Kind is RglSymbolKind.Variable or RglSymbolKind.Constant && symbol.TypeText is null ? symbol with { TypeText = document.TypeOfSymbol(symbol) } : symbol;
        var signature = Signatures.Of(shown);
        var documentation = "```rgl\n" + signature + "\n```" + (symbol.Documentation is { } text ? "\n\n" + text : "");
        var detail = symbol.Kind switch
        {
            RglSymbolKind.Variable or RglSymbolKind.Parameter or RglSymbolKind.Constant or RglSymbolKind.Field => shown.TypeText,
            RglSymbolKind.Method => symbol.TypeText,
            RglSymbolKind.Variant => symbol.Container is { } container ? $"{container}.{symbol.Name}" : null,
            RglSymbolKind.Mold => "mold",
            RglSymbolKind.Error => "error",
            RglSymbolKind.Enum => "enum",
            RglSymbolKind.Ingot => signature,
            RglSymbolKind.Module => "fichier",
            RglSymbolKind.TypeParameter => "paramètre générique",
            _ => signature,
        };
        var source = symbol.Module is { } module && module != document.Self ? Path.GetFileName(module.Path) : null;
        return new CompletionItem(symbol.Name, _KindOf(symbol), detail, documentation, sort + symbol.Name, source, Deprecated: symbol.IsDeprecated);
    }

    /// <summary>
    /// Un gear s'écrit avec ses parenthèses, comme dans un éditeur de C# : le curseur est entre elles s'il attend des arguments. Un guard, un emit
    /// et un train se placent dans un train, sans parenthèses.
    /// </summary>
    private static CompletionItem _WithCall(CompletionItem item, RglSymbol symbol) => symbol switch
    {
        { Node: Rgl.AST.Descriptors.DescriptorMember member } => item with { InsertText = ExtensionMethods.CallSnippet(symbol.Name, DescriptorEditorModel.Parameters(symbol, member).Count) },
        { Kind: RglSymbolKind.Gear, Node: Rgl.AST.Gears.GearDeclaration gear } => item with { InsertText = ExtensionMethods.CallSnippet(symbol.Name, gear.Parameters.Count) },
        { Kind: RglSymbolKind.Method, TypeText: { } text } when text.Contains('(') => item with { InsertText = ExtensionMethods.CallSnippet(symbol.Name, text.Contains("()") ? 0 : 1) },
        _ => item,
    };

    /// <summary>La proposition d'un gear d'extension : sa signature sans <c>this</c>, et le fichier qui l'écrit.</summary>
    private static CompletionItem _ExtensionItem(ExtensionMethod method)
    {
        var documentation = "```rgl\n" + $"{method.Name}{method.Parameters}: {method.Return}" + "\n```" + (method.Module.DocumentationFor(method.Gear.Span) is { } text ? "\n\n" + text : "")
            + $"\n\n*Extension de* `{Signatures.Collapse(method.Extend.Target.Span.Text)}`";
        var source = method.Module.Unit.Statements.OfType<Rgl.AST.Files.NamespaceDeclaration>().FirstOrDefault()?.Name is { Length: > 0 } space ? "extension · " + space : "extension";
        return new CompletionItem(method.Name, CompletionKind.Method, $"{method.Parameters}: {method.Return}", documentation, "1_" + method.Name, source,
            InsertText: ExtensionMethods.CallSnippet(method.Name, method.Gear.Parameters.Count - 1),
            Deprecated: method.Gear.Notes?.Annotations.OfType<Rgl.AST.Notes.DeprecatedAnnotation>().Any() == true);
    }

    private static CompletionKind _KindOf(RglSymbol symbol) => symbol.Kind switch
    {
        RglSymbolKind.Gear or RglSymbolKind.Guard or RglSymbolKind.Emit or RglSymbolKind.Train => CompletionKind.Function,
        RglSymbolKind.Mold or RglSymbolKind.Error => CompletionKind.Struct,
        RglSymbolKind.Enum or RglSymbolKind.Dataset => CompletionKind.Enum,
        RglSymbolKind.Ingot => CompletionKind.Class,
        RglSymbolKind.Constant => CompletionKind.Constant,
        RglSymbolKind.Variable or RglSymbolKind.Parameter => CompletionKind.Variable,
        RglSymbolKind.TypeParameter => CompletionKind.TypeParameter,
        RglSymbolKind.Field => CompletionKind.Field,
        RglSymbolKind.Variant => CompletionKind.EnumMember,
        RglSymbolKind.Method => CompletionKind.Method,
        RglSymbolKind.Module => CompletionKind.Module,
        _ => CompletionKind.Variable,
    };

    #endregion

    #region Import automatique

    /// <summary>
    /// Ce qu'il faut écrire pour importer <paramref name="name"/> de <paramref name="module"/> (le fichier entier si <paramref name="name"/> est
    /// <see langword="null"/>) : le nom s'ajoute à l'<c>import … only {…}</c> du fichier s'il y en a un, sinon une ligne
    /// <c>import</c> s'écrit après les autres imports, ou l'en-tête, ou en tête du fichier.
    /// </summary>
    /// <param name="std">Vrai pour un nom de la std : l'import s'écrit <c>use &lt;Fichier&gt; only {…};</c>.</param>
    public static TextInsertion ImportEdit(SemanticDocument document, string module, string? name, bool std = false)
    {
        var source = document.Source;
        var kind = std ? ImportKind.Std : ImportKind.Plain;
        if (name is not null && document.Unit.Imports.FirstOrDefault(i => i.Kind == kind && i.Module.Name == module && i.Alias is null && i.Only is not null) is { } existing)
        {
            if (existing.Only!.Count > 0) return _At(source, (int)existing.Only[^1].Span.End.Value, ", " + name);
            var brace = source.Content.IndexOf('{', (int)existing.Module.Span.End.Value);
            if (brace >= 0) return _At(source, brace + 1, name);
        }

        var line = std ? (name is null ? $"use <{module}>;" : $"use <{module}> only {{{name}}};") : name is null ? $"import {module};" : $"import {module} only {{{name}}};";
        var last = document.Unit.Imports.LastOrDefault();
        var after = last?.Span.End ?? document.Unit.Header?.Span.End;
        if (after is null) return new TextInsertion(0, 0, line + "\n\n");

        var anchor = _At(source, (int)after.Value.Value, "");
        var next = anchor.Line + 1;
        var separator = last is null ? "\n" : "";
        return next < source.LineStart.Count
            ? new TextInsertion(next, 0, separator + line + "\n")
            : new TextInsertion(anchor.Line, (uint)(source.Content.Length - (int)source.LineStart[(int)anchor.Line].Value), "\n" + separator + line);
    }

    private static TextInsertion _At(SourceText source, int offset, string text)
    {
        var position = source[Rotomeca.Utils.Types.RUint.CreateChecked((uint)offset)];
        var line = position.Line.Value - 1;
        return new TextInsertion(line, (uint)offset - source.LineStart[(int)line].Value, text);
    }

    #endregion
}
