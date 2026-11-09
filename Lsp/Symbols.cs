using Rgl.AST.Descriptors;
using Rgl.AST.Files;
using System.Text;
using System.Text.RegularExpressions;
using Rgl.AST;
using Rgl.AST.Datasets;
using Rgl.AST.Enums;
using Rgl.AST.Gears;
using Rgl.AST.Generics;
using Rgl.AST.Ingots;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Precasts;
using Rgl.AST.Train;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Ce qu'un nom désigne pour l'éditeur : sert à l'icône, au survol et à l'aide à la saisie.</summary>
internal enum RglSymbolKind
{
    Gear,
    Guard,
    Emit,
    Train,
    Mold,
    Error,
    Enum,
    Ingot,
    Constant,
    Variable,
    Parameter,
    TypeParameter,
    Field,
    Variant,
    Method,
    Module,
    Dataset,
    Annotation,
}

/// <summary>
/// Un nom déclaré : dans un fichier (gear, mold, constante…), dans un corps (variable, paramètre) ou dans un type (champ, variante).
/// </summary>
/// <param name="Name">Le nom.</param>
/// <param name="Kind">Ce qu'il désigne.</param>
/// <param name="NameSpan">Le nom dans sa déclaration.</param>
/// <param name="Node">La déclaration, quand il y en a une dans l'arbre.</param>
/// <param name="Module">Le fichier qui le déclare.</param>
/// <param name="TypeText">Le type écrit d'une variable, d'un champ ou d'un paramètre, le type de retour d'un gear ; <see langword="null"/> s'il est à déduire.</param>
/// <param name="Initializer">La valeur d'une variable déclarée sans type (<c>var x: 3;</c>), dont se déduit son type.</param>
/// <param name="Container">Le type qui contient un champ ou une variante.</param>
/// <param name="Documentation">Le commentaire écrit juste avant la déclaration.</param>
/// <param name="TypeArguments">Substitution des paramètres de type d’un membre de descriptor instancié.</param>
internal sealed record RglSymbol(
    string Name,
    RglSymbolKind Kind,
    SourceSpan NameSpan,
    IAstNode? Node,
    ModuleInfo? Module,
    string? TypeText = null,
    IExpression? Initializer = null,
    string? Container = null,
    string? Documentation = null,
    IReadOnlyDictionary<string, string>? TypeArguments = null)
{
    /// <summary>Le message de <c>@deprecated</c> (vide s'il n'en a pas) quand la déclaration est obsolète ; <see langword="null"/> sinon.</summary>
    public string? Deprecation => Node is GearDeclaration { Notes: { } notes } && notes.Annotations.OfType<AST.Notes.DeprecatedAnnotation>().FirstOrDefault() is { } annotation
        ? (annotation.Message?.Span.Text is { Length: >= 2 } text ? text[1..^1] : "")
        : null;

    /// <summary>Vrai si la déclaration porte <c>@deprecated</c> : chaque emploi est barré dans l'éditeur.</summary>
    public bool IsDeprecated => Deprecation is not null;

    /// <summary>Vrai pour ce qui s'appelle : gear, guard, emit, train.</summary>
    public bool IsCallable => Kind is RglSymbolKind.Gear or RglSymbolKind.Guard or RglSymbolKind.Emit or RglSymbolKind.Train;

    /// <summary>Vrai pour ce qui s'écrit à la place d'un type : mold, error, enum, ingot, paramètre générique.</summary>
    public bool IsType => Kind is RglSymbolKind.Mold or RglSymbolKind.Error or RglSymbolKind.Enum or RglSymbolKind.Ingot or RglSymbolKind.TypeParameter;

    /// <summary>Vrai pour ce qui se déclare au premier niveau d'un fichier, et qu'un autre fichier peut importer.</summary>
    public bool IsTopLevel => Kind is RglSymbolKind.Gear or RglSymbolKind.Guard or RglSymbolKind.Emit or RglSymbolKind.Train or RglSymbolKind.Mold
        or RglSymbolKind.Error or RglSymbolKind.Enum or RglSymbolKind.Ingot or RglSymbolKind.Constant or RglSymbolKind.Dataset;
}

/// <summary>Un fichier lu pour l'éditeur : son arbre, ses tokens (pour les commentaires) et ses déclarations de premier niveau.</summary>
internal sealed class ModuleInfo
{
    private readonly Dictionary<uint, Token> _tokenAt = [];

    private ModuleInfo(string path, SourceText source, CompilationUnit unit, IReadOnlyList<Token> tokens, string? name)
    {
        Path = path;
        Source = source;
        Unit = unit;
        Tokens = tokens;
        Name = name ?? System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (var token in tokens) _tokenAt.TryAdd(token.Span.Start.Value, token);

        Kind = unit.Header?.Kind?.Name;
        Description = unit.Header?.Description is { } description ? _Unquote(description.Text) : null;
        var declared = SymbolsOf(unit.Statements, this).ToArray();
        Overloads = declared.Where(s => s.IsCallable).GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RglSymbol>)[.. g], StringComparer.Ordinal);
        Symbols = new(StringComparer.Ordinal);
        foreach (var symbol in declared) Symbols.TryAdd(symbol.Name, symbol);
        // Un fichier qui écrit un export n'expose que ces noms à ceux qui l'importent ; sans export, tout.
        var exports = unit.Statements.OfType<ExportDeclaration>().SelectMany(e => e.Names).Where(n => n.Kind == ExportKind.Name).Select(n => n.Name.Name).ToHashSet();
        Exported = unit.Statements.OfType<ExportDeclaration>().Any() && Kind != "index"
            ? Symbols.Where(s => exports.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal)
            : Symbols;
        DefaultTrain = _FindDefaultTrain();
    }

    /// <summary>Le chemin du fichier.</summary>
    public string Path { get; }

    /// <summary>Son nom sans extension : celui qu'un import écrit.</summary>
    public string Name { get; }

    public SourceText Source { get; }

    public CompilationUnit Unit { get; }

    public IReadOnlyList<Token> Tokens { get; }

    /// <summary>Le genre de son en-tête <c>forge:</c> ; <see langword="null"/> pour un fichier sans en-tête.</summary>
    public string? Kind { get; }

    /// <summary>Sa description d'en-tête, sans guillemets.</summary>
    public string? Description { get; }

    /// <summary>Ses déclarations de premier niveau, par nom.</summary>
    public Dictionary<string, RglSymbol> Symbols { get; }

    /// <summary>Toutes les signatures appelables d'un nom ; <see cref="Symbols"/> en garde une pour les usages non contextuels.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RglSymbol>> Overloads { get; }

    /// <summary>Ceux de ses <see cref="Symbols"/> que les fichiers qui l'importent voient : les noms exportés s'il écrit un export, tous sinon.</summary>
    public Dictionary<string, RglSymbol> Exported { get; private set; }

    /// <summary>
    /// Pour une box : ce que son index exporte, c'est-à-dire les noms exportés des fichiers qu'il étale (<c>export {...Math, ...Constants};</c>),
    /// que <paramref name="members"/> (les fichiers de la box) déclarent.
    /// </summary>
    public void MergeExports(IReadOnlyList<ModuleInfo> members, IEnumerable<string> spread)
    {
        var merged = new Dictionary<string, RglSymbol>(StringComparer.Ordinal);
        foreach (var name in spread)
        {
            if (members.FirstOrDefault(m => m.Name == name) is not { } member) continue;
            foreach (var (symbolName, symbol) in member.Exported) merged.TryAdd(symbolName, symbol);
        }
        if (merged.Count > 0) Exported = merged;
    }

    /// <summary>Les extensions écrites dans ce fichier (<c>extend [T] { … }</c>).</summary>
    public IEnumerable<AST.Extensions.ExtendDeclaration> Extends => Unit.Statements.OfType<AST.Extensions.ExtendDeclaration>();

    /// <summary>Son train par défaut, que <c>import Fichier;</c> rend appelable sous le nom du fichier ; <see langword="null"/> s'il n'en a pas.</summary>
    public RglSymbol? DefaultTrain { get; }

    /// <summary>Vrai pour un fichier de projet que d'autres peuvent importer (pas le fichier d'entrée).</summary>
    public bool IsImportable => Path.EndsWith(".forge", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lit un fichier ; il peut contenir des erreurs, le parseur se rattrape toujours.</summary>
    /// <param name="name">Le nom sous lequel un import l'écrit, s'il n'est pas celui du fichier (une box de la std).</param>
    public static ModuleInfo Build(string path, string content, string? name = null)
    {
        var source = new SourceText(path, content);
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(source, diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        return new ModuleInfo(path, source, unit, tokens.Tokens, name);
    }

    /// <summary>Expose les liaisons C engendrées, mais navigue vers l’en-tête forge réellement éditable.</summary>
    public void AddNativeBindings(AST.CompilationUnit bindings)
    {
        foreach (var symbol in SymbolsOf(bindings.Statements, this))
        {
            var visible = symbol with { NameSpan = Unit.Header!.Span };
            Symbols.TryAdd(visible.Name, visible);
            Exported.TryAdd(visible.Name, visible);
        }
    }

    /// <summary>Le commentaire écrit juste avant la déclaration qui commence à <paramref name="span"/> ; <see langword="null"/> s'il n'y en a pas.</summary>
    public string? DocumentationFor(SourceSpan span) =>
        _tokenAt.TryGetValue(span.Start.Value, out var token) ? _DocumentationOf(token) : null;

    /// <summary>La documentation <c>///</c> lue de la déclaration qui commence à <paramref name="span"/> ; <see langword="null"/> s'il n'y en a pas.</summary>
    public Docs.DocComment? DocCommentFor(SourceSpan span) =>
        _tokenAt.TryGetValue(span.Start.Value, out var token) && Docs.DocExtractor.Before(token) is { } block ? Docs.DocParser.Parse(block) : null;

    /// <summary>
    /// Ce que l'éditeur montre d'une déclaration : sa documentation <c>///</c> (résumé, paramètres, résultat…) si elle en a, sinon
    /// les commentaires ordinaires qui la précèdent.
    /// </summary>
    private static string? _DocumentationOf(Token token) =>
        Docs.DocExtractor.Before(token) is { } block ? Docs.DocRenderer.Hover(Docs.DocParser.Parse(block)) : _Comments(token);

    /// <summary>Le token qui commence à cet indice, s'il y en a un.</summary>
    public Token? TokenAt(uint start) => _tokenAt.GetValueOrDefault(start);

    private RglSymbol? _FindDefaultTrain()
    {
        if (!IsImportable) return null;

        if (Unit.Train is { } train)
            return new RglSymbol(Name, RglSymbolKind.Train, new SourceSpan(Source, train.Span.Start, RUintOf(Math.Min(5, train.Span.Length.Value))), train, this,
                Documentation: DocumentationFor(train.Span));
        if (Unit.Statements.OfType<TrainBlock>().FirstOrDefault(b => b.Name is null) is { Train: { } blockTrain } block)
            return new RglSymbol(Name, RglSymbolKind.Train, new SourceSpan(Source, blockTrain.Span.Start, RUintOf(Math.Min(5, blockTrain.Span.Length.Value))), blockTrain, this,
                Documentation: DocumentationFor(block.Span));
        return null;
    }

    private static Rotomeca.Utils.Types.RUint RUintOf(uint value) => Rotomeca.Utils.Types.RUint.CreateChecked(value);

    private static string _Unquote(string text) => text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    /// <summary>
    /// Les commentaires qui précèdent <paramref name="token"/> sans ligne vide entre eux ni avec lui : leur texte sans les
    /// marques <c>//</c>, <c>/*</c> et <c>*</c>, une ligne par ligne de commentaire.
    /// </summary>
    private static string? _Comments(Token token)
    {
        var lines = new List<string>();
        var breaks = 0;
        for (var i = token.Before.Count - 1; i >= 0; --i)
        {
            var trivia = token.Before[i];
            if (trivia.Kind == TriviaKind.Whitespace) continue;
            if (trivia.Kind == TriviaKind.EndOfLine)
            {
                if (++breaks > 1) break;
                continue;
            }

            if (trivia.Kind is not (TriviaKind.SingleComment or TriviaKind.BlockComment)) break;

            breaks = 0;
            if (trivia.Kind == TriviaKind.SingleComment) lines.Insert(0, Regex.Replace(trivia.Span.Text, @"^/{2,}\s?", "").TrimEnd());
            else
            {
                var body = Regex.Replace(trivia.Span.Text, @"^/\*+|\*+/$", "");
                lines.InsertRange(0, body.Split('\n').Select(l => Regex.Replace(l.TrimEnd('\r'), @"^\s*\*?\s?", "").TrimEnd()).SkipWhile(string.IsNullOrWhiteSpace));
            }
        }

        var text = string.Join('\n', lines).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Les déclarations d'une suite d'instructions de premier niveau (celles d'un fichier ou d'un bloc de train).</summary>
    public static IEnumerable<RglSymbol> SymbolsOf(IEnumerable<IStatement> statements, ModuleInfo module)
    {
        IStatement? previous = null;
        foreach (var statement in statements)
        {
            // Un commentaire écrit avant « export gear f() » précède l'export, pas le gear.
            var doc = module.DocumentationFor(previous is ExportDeclaration { IsInline: true } export ? export.Span : statement.Span);
            previous = statement;
            switch (statement)
            {
                case GearDeclaration { Name.IsMissing: false } gear:
                    yield return new RglSymbol(gear.Name.Name, gear.Keyword switch { TokenKind.GuardKeyword => RglSymbolKind.Guard, TokenKind.EmitKeyword => RglSymbolKind.Emit, _ => RglSymbolKind.Gear },
                        gear.Name.Span, gear, module, gear.ReturnType.Span.Text, Documentation: doc);
                    break;

                case DescriptorDeclaration descriptor:
                    yield return new RglSymbol(descriptor.Name.Name, RglSymbolKind.Mold, descriptor.Name.Span, descriptor, module, Documentation: doc);
                    break;
                case MoldDeclaration { Name.IsMissing: false } mold:
                    yield return new RglSymbol(mold.Name.Name, mold.IsError ? RglSymbolKind.Error : RglSymbolKind.Mold, mold.Name.Span, mold, module, Documentation: doc);
                    break;

                case EnumDeclaration { Name.IsMissing: false } enumeration:
                    yield return new RglSymbol(enumeration.Name.Name, RglSymbolKind.Enum, enumeration.Name.Span, enumeration, module, Documentation: doc);
                    break;

                case IngotDeclaration { Name.IsMissing: false } ingot:
                    yield return new RglSymbol(ingot.Name.Name, RglSymbolKind.Ingot, ingot.Name.Span, ingot, module, Documentation: doc);
                    break;

                case VarDeclaration { IsConst: true, Name.IsMissing: false } constant:
                    yield return new RglSymbol(constant.Name.Name, RglSymbolKind.Constant, constant.Name.Span, constant, module, constant.Type?.Span.Text, constant.Expression, Documentation: doc);
                    break;

                case DatasetDeclaration { Name.IsMissing: false } dataset:
                    yield return new RglSymbol(dataset.Name.Name, RglSymbolKind.Dataset, dataset.Name.Span, dataset, module, Documentation: doc);
                    break;

                case PrecastDeclaration { Name.IsMissing: false } precast:
                    yield return new RglSymbol(precast.Name.Name, RglSymbolKind.Constant, precast.Name.Span, precast, module, precast.Type?.Span.Text, Documentation: doc);
                    break;

                case TrainBlock { Name: { IsMissing: false } trainName } block:
                    yield return new RglSymbol(trainName.Name, RglSymbolKind.Train, trainName.Span, (IAstNode?)block.Train ?? block, module, Documentation: doc);
                    break;
            }
        }
    }
}

/// <summary>Les signatures écrites des déclarations, telles que l'éditeur les montre.</summary>
internal static class Signatures
{
    /// <summary>Les types d'un gear : <c>&lt;T: number&gt;</c>, ou rien.</summary>
    public static string TypeParameters(IReadOnlyList<TypeParameter>? parameters) =>
        parameters is { Count: > 0 } ? "<" + string.Join(", ", parameters.Select(p => Collapse(p.Span.Text))) + ">" : "";

    /// <summary>Le texte sur une ligne : suites d'espaces et sauts de ligne réduits à un espace.</summary>
    public static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// Le type de retour d'un gear tel que l'appelant le voit : celui qui est écrit, ou — pour un gear <c>@async</c> ou <c>@task</c> — la promesse complète
    /// <c>Promise&lt;T ! AsyncException&gt;</c> ; un <c>@fire</c> ne rend rien.
    /// </summary>
    public static string ReturnTextOf(GearDeclaration gear)
    {
        var written = Collapse(gear.ReturnType.Span.Text);
        var names = gear.Notes?.Annotations.OfType<AST.Notes.OtherAnnotation>().Select(a => a.Name.Name).ToArray() ?? [];
        if (names.Contains("fire")) return "void";
        if (!names.Contains("async") && !names.Contains("task")) return written;
        if (written.StartsWith("Promise<", StringComparison.Ordinal)) return written.Contains('!') ? written : written[..^1] + " ! AsyncException>";
        return $"Promise<{written} ! AsyncException>";
    }

    /// <summary>La signature d'un gear, d'un guard, d'un emit ou d'un train : <c>gear pure clamp&lt;T: number&gt;(T v, T lo, T hi): T</c>.</summary>
    public static string Of(GearDeclaration gear) =>
        $"{(gear.Keyword == TokenKind.GuardKeyword ? "guard" : gear.Keyword == TokenKind.EmitKeyword ? "emit" : gear.Keyword == TokenKind.UnsafeKeyword ? "unsafe" : "gear")}{(gear.IsPure ? " pure" : "")} {gear.Name.Name}{TypeParameters(gear.TypeParameters)}"
        + $"({string.Join(", ", gear.Parameters.Select(p => Collapse(p.Span.Text)))}): {ReturnTextOf(gear)}";

    /// <summary>Le nom et les paramètres d'une déclaration appelable, sans le type de retour : pour l'aide à la saisie d'un appel.</summary>
    public static (string Name, IReadOnlyList<string> Parameters, string? Return) Call(RglSymbol symbol) => symbol.Node switch
    {
        DescriptorMember member => (symbol.Name, DescriptorEditorModel.Parameters(symbol, member), symbol.TypeText),
        GearDeclaration gear => (gear.Name.Name + TypeParameters(gear.TypeParameters), [.. gear.Parameters.Select(p => Collapse(p.Span.Text))], ReturnTextOf(gear)),
        TrainDeclaration train => (symbol.Name, [.. (train.Parameters ?? []).Select(p => Collapse(p.Span.Text))], null),
        _ => (symbol.Name, [], null),
    };

    /// <summary>La déclaration d'un symbole, comme elle s'écrit : ce que le survol montre dans son bloc de code.</summary>
    public static string Of(RglSymbol symbol) => symbol switch
    {
        { Node: DescriptorDeclaration descriptor } => $"descriptor {descriptor.CppType.Text} -> {descriptor.Name.Name}{TypeParameters(descriptor.TypeParameters)}",
        { Node: DescriptorMember member } => $"guard {symbol.Name}({string.Join(", ", DescriptorEditorModel.Parameters(symbol, member))}): {symbol.TypeText}",
        { Node: GearDeclaration gear } => Of(gear),
        { Kind: RglSymbolKind.Train, Node: TrainDeclaration train } => $"train {symbol.Name}({string.Join(", ", (train.Parameters ?? []).Select(p => Collapse(p.Span.Text)))})",
        { Kind: RglSymbolKind.Train } => $"train {symbol.Name}",
        { Node: MoldDeclaration mold } => Of(mold),
        { Node: EnumDeclaration enumeration } => Of(enumeration),
        { Node: IngotDeclaration ingot } => $"ingot {ingot.Name.Name}: {Collapse(ingot.Base.Span.Text)}",
        { Node: AST.Notes.AnnotationDeclaration annotation } => Truncate(Collapse(annotation.Span.Text)).TrimEnd(';'),
        { Node: DatasetDeclaration dataset } => $"dataset {dataset.Name.Name} {(dataset.CreatesMold ? "as" : "of")} {dataset.Mold.Name}",
        { Node: PrecastDeclaration precast } => $"precast {(precast.Type is null ? "" : Collapse(precast.Type.Span.Text) + " ")}{precast.Name.Name}",
        { Kind: RglSymbolKind.Constant, Node: VarDeclaration constant } => $"const {(constant.Type is null ? "" : Collapse(constant.Type.Span.Text) + " ")}{constant.Name.Name}: {Truncate(Collapse(constant.Expression.Span.Text))}",
        { Kind: RglSymbolKind.Parameter } => $"{symbol.TypeText} {symbol.Name}",
        { Kind: RglSymbolKind.Variable, Node: VarDeclaration { IsIntef: true } declared } => $"{(declared.IsMutable ? "mut " : "")}var {symbol.Name}: {symbol.TypeText ?? "?"}",
        { Kind: RglSymbolKind.Variable } => $"{(symbol.Node is VarDeclaration { IsMutable: true } ? "mut " : "")}{symbol.TypeText ?? "var"} {symbol.Name}",
        { Kind: RglSymbolKind.Method } => symbol.TypeText ?? symbol.Name,
        { Kind: RglSymbolKind.Error } => $"error {symbol.Name} {{ string message; string where; }}",
        { Kind: RglSymbolKind.Field } => $"{symbol.TypeText} {symbol.Name}",
        { Kind: RglSymbolKind.TypeParameter } => $"{symbol.Name}{(symbol.TypeText is null ? "" : ": " + symbol.TypeText)}",
        { Kind: RglSymbolKind.Variant } => $"{symbol.Container}.{symbol.Name}{(symbol.TypeText is null ? "" : " = " + symbol.TypeText)}",
        _ => symbol.Name,
    };

    /// <summary>Un mold avec ses champs, un par ligne.</summary>
    public static string Of(MoldDeclaration mold)
    {
        var builder = new StringBuilder();
        builder.Append(mold.IsError ? "error " : "mold ").Append(mold.Name.Name).Append(TypeParameters(mold.TypeParameters)).Append(" {");
        foreach (var member in mold.Members) builder.Append("\n    ").Append(Collapse(member.Span.Text)).Append(';');
        return builder.Append("\n}").ToString();
    }

    /// <summary>Un enum avec ses variantes.</summary>
    public static string Of(EnumDeclaration enumeration)
    {
        var builder = new StringBuilder("enum ").Append(enumeration.Name.Name);
        if (enumeration.Underlying is { } underlying) builder.Append(": ").Append(Collapse(underlying.Span.Text));
        builder.Append(" {");
        foreach (var variant in enumeration.Variants) builder.Append("\n    ").Append(Collapse(variant.Span.Text)).Append(',');
        return builder.Append("\n}").ToString();
    }

    /// <summary>Un texte long coupé à 120 caractères.</summary>
    public static string Truncate(string text) => text.Length <= 120 ? text : text[..117] + "…";
}
