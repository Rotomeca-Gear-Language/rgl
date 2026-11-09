using Rgl.Project;
using Rgl.Syntax;

namespace Rgl.Lsp;

/// <summary>
/// La coloration sémantique : chaque nom du document reçoit la sorte de ce qu'il désigne, retrouvée comme pour le survol — un ingot a sa
/// couleur de type (celle d'un type primitif) en italique, un mold ou une error la couleur d'une structure, un enum celle d'un
/// enum et ses variantes celle d'une constante, un gear celle d'une fonction, une variable, un paramètre, un champ, un fichier
/// importé ont chacun la leur. Retaper un nom lui redonne sa couleur : elle ne vient pas de la façon dont il est écrit, mais de ce
/// qu'il est. Le texte sans erreur ni nom inconnu garde la coloration de la grammaire.
/// </summary>
internal static class SemanticTokensProvider
{
    /// <summary>Les sortes de noms, dans l'ordre de leur numéro ; <c>ingot</c> est propre à RGL (un type, pour les thèmes).</summary>
    public static readonly string[] Types =
        ["namespace", "type", "class", "enum", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "function", "method", "ingot", "decorator"];

    /// <summary>Les précisions qu'un nom peut avoir en plus : chacune est un bit du dernier nombre de son jeton.</summary>
    public static readonly string[] Modifiers = ["declaration", "readonly", "defaultLibrary", "static", "deprecated"];

    private const int Declaration = 1 << 0;
    private const int ReadOnly = 1 << 1;
    private const int DefaultLibrary = 1 << 2;
    private const int Deprecated = 1 << 4;

    /// <summary>Les types d'erreur du compilateur : ils viennent de la std, même quand rien ne les déclare dans le document.</summary>
    private static readonly HashSet<string> _standardErrors = ["Exception", "CastError", "LoopError"];

    /// <summary>
    /// Les jetons du document, codés comme le demande le protocole : pour chacun, l'écart de ligne avec le précédent, l'écart de colonne
    /// (depuis le début de la ligne s'il change de ligne), sa longueur, sa sorte et ses précisions.
    /// </summary>
    public static int[] Encode(SemanticDocument document)
    {
        var data = new List<int>();
        var (previousLine, previousStart) = (0, 0);
        foreach (var token in document.Self.Tokens)
        {
            if (token.Kind != TokenKind.Identifier || token.Span.Length.Value == 0) continue;
            if (_Classify(document, token) is not var (type, modifiers)) continue;

            var start = (int)token.Span.Start.Value;
            var line = (int)document.Source[token.Span.Start].Line.Value - 1;
            var column = start - (int)document.Source.LineStart[line].Value;

            data.Add(line - previousLine);
            data.Add(line == previousLine ? column - previousStart : column);
            data.Add((int)token.Span.Length.Value);
            data.Add(type);
            data.Add(modifiers);
            (previousLine, previousStart) = (line, column);
        }
        return [.. data];
    }

    /// <summary>La sorte et les précisions du nom que désigne <paramref name="token"/> ; <see langword="null"/> si rien ne le désigne.</summary>
    private static (int Type, int Modifiers)? _Classify(SemanticDocument document, Token token)
    {
        var start = (int)token.Span.Start.Value;
        var symbol = document.TargetAt(start)?.Symbol;
        if (symbol is null)
        {
            // std.nom : le qualificatif de la std ; Exception… : les erreurs du compilateur.
            if (token.Text == "std" && document.QualifiedNames("std") is not null && document.IsFollowedByDot(token)) return (Index("namespace"), DefaultLibrary);
            return _standardErrors.Contains(token.Text) ? (Index("class"), DefaultLibrary) : null;
        }

        var type = symbol.Kind switch
        {
            RglSymbolKind.Gear or RglSymbolKind.Guard or RglSymbolKind.Emit or RglSymbolKind.Train => "function",
            RglSymbolKind.Method => "method",
            RglSymbolKind.Mold => "struct",
            RglSymbolKind.Error => "class",
            RglSymbolKind.Enum or RglSymbolKind.Dataset => "enum",
            RglSymbolKind.Ingot => "ingot",
            RglSymbolKind.Annotation => "decorator",
            RglSymbolKind.Constant or RglSymbolKind.Variable => "variable",
            RglSymbolKind.Parameter => "parameter",
            RglSymbolKind.TypeParameter => "typeParameter",
            RglSymbolKind.Field => "property",
            RglSymbolKind.Variant => "enumMember",
            RglSymbolKind.Module => "namespace",
            _ => null,
        };
        if (type is null) return null;

        var modifiers = 0;
        if (symbol.Module == document.Self && symbol.NameSpan.Start == token.Span.Start && symbol.NameSpan.Source == token.Span.Source) modifiers |= Declaration;
        if (symbol.Kind is RglSymbolKind.Constant or RglSymbolKind.Variant) modifiers |= ReadOnly;
        // L'éditeur barre un emploi obsolète ; la déclaration elle-même reste lisible.
        if (symbol.IsDeprecated && (modifiers & Declaration) == 0) modifiers |= Deprecated;
        if (symbol.Module is { } module && StdLibrary.Contains(module.Path)) modifiers |= DefaultLibrary;
        return (Index(type), modifiers);
    }

    private static int Index(string type) => Array.IndexOf(Types, type);
}
