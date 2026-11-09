using System.Text.RegularExpressions;
using Rgl.AST.Expressions;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Un emploi d'un nom : où il s'écrit, et s'il est la déclaration elle-même.</summary>
/// <param name="Path">Le fichier.</param>
/// <param name="Source">Son texte, pour convertir la plage en ligne et colonne.</param>
/// <param name="Span">Le nom.</param>
/// <param name="IsDeclaration">Vrai pour le nom écrit dans la déclaration.</param>
/// <param name="IsShorthand">Vrai pour <c>{x}</c> : le nom est à la fois un champ et la variable qui le remplit.</param>
internal sealed record Occurrence(string Path, SourceText Source, SourceSpan Span, bool IsDeclaration, bool IsShorthand);

/// <summary>Les emplois d'une déclaration, et la déclaration qu'ils désignent.</summary>
internal sealed record ReferenceSet(RglSymbol Symbol, Token Token, IReadOnlyList<Occurrence> Occurrences);

/// <summary>
/// Trouve tous les emplois de ce que désigne un mot : ceux du document pour une variable ou un paramètre, ceux de tout le projet pour un nom qui s'importe
/// (gear, mold, enum, constante, champ, variante…). Un emploi est un mot qui, lu comme pour le survol, désigne la même déclaration : deux noms égaux
/// qui désignent des déclarations différentes ne se mélangent donc pas. Sert aux références, au surlignage des occurrences et au renommage.
/// </summary>
internal static partial class ReferenceFinder
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex _IdentifierPattern();

    /// <summary>
    /// Les emplois de ce que désigne le mot en <paramref name="offset"/> ; <see langword="null"/> si le curseur n'est pas sur un nom déclaré quelque part
    /// (un mot-clé, un nom inconnu, un membre de collection).
    /// </summary>
    /// <param name="wholeProject">Faux pour ne regarder que le document : le surlignage des occurrences n'a pas à lire les autres fichiers.</param>
    public static ReferenceSet? Find(SemanticDocument document, int offset, bool wholeProject = true)
    {
        var target = document.TargetAt(WordOffset(document, offset));
        if (target is not { Symbol: { } symbol, Token.Kind: TokenKind.Identifier } || !_IsDeclared(symbol)) return null;

        var key = KeyOf(symbol)!;
        var occurrences = new List<Occurrence>();
        occurrences.AddRange(_Scan(document, symbol, key));

        if (wholeProject && _IsShared(symbol))
        {
            foreach (var module in document.Project.Sources)
            {
                if (string.Equals(module.Path, document.Self.Path, StringComparison.OrdinalIgnoreCase) || !module.Source.Content.Contains(symbol.Name, StringComparison.Ordinal)) continue;
                occurrences.AddRange(_Scan(SemanticDocument.Create(module.Path, module.Source.Content, document.Project.SeenFrom(module, document.Self)), symbol, key));
            }
        }
        return new ReferenceSet(symbol, target.Token, occurrences);
    }

    /// <summary>
    /// La place d'un curseur posé à la fin d'un mot (<c>nom|</c>) compte comme sur ce mot, comme dans un éditeur de TypeScript : le mot qui finit à cet endroit
    /// l'emporte sur le signe qui le suit.
    /// </summary>
    public static int WordOffset(SemanticDocument document, int offset) =>
        offset > 0 && document.TokenContaining(offset) is not { Kind: TokenKind.Identifier } && document.TokenContaining(offset - 1) is { Kind: TokenKind.Identifier } ? offset - 1 : offset;

    /// <summary>L'identité d'une déclaration : son fichier, la place de son nom et ce nom ; <see langword="null"/> pour ce qui n'est déclaré nulle part (membres de collection).</summary>
    public static string? KeyOf(RglSymbol symbol) =>
        _IsDeclared(symbol) ? $"{System.IO.Path.GetFullPath(symbol.Module!.Path).ToUpperInvariant()}|{symbol.NameSpan.Start.Value}|{symbol.Name}" : null;

    private static bool _IsDeclared(RglSymbol symbol) =>
        symbol.Module is not null && symbol.NameSpan.Length.Value > 0 && symbol.Kind != RglSymbolKind.Module && symbol.Name.Length > 0;

    /// <summary>Vrai pour un nom qu'un autre fichier peut employer ; une variable, un paramètre et un paramètre générique ne s'emploient que dans leur fichier.</summary>
    private static bool _IsShared(RglSymbol symbol) =>
        symbol.IsTopLevel || symbol.Kind is RglSymbolKind.Field or RglSymbolKind.Variant or RglSymbolKind.Method or RglSymbolKind.Annotation;

    private static IEnumerable<Occurrence> _Scan(SemanticDocument document, RglSymbol symbol, string key)
    {
        foreach (var token in document.Self.Tokens)
        {
            if (token.Kind != TokenKind.Identifier || token.Text != symbol.Name) continue;
            var start = (int)token.Span.Start.Value;
            if (document.TargetAt(start)?.Symbol is not { } found || KeyOf(found) != key) continue;

            var declaration = found.Module == document.Self && found.NameSpan.Start == token.Span.Start;
            var shorthand = document.PathAt(start, inclusiveEnd: true).OfType<MoldFieldValue>().Any(f => f.IsShorthand && f.Name.Span.Start == token.Span.Start);
            yield return new Occurrence(document.Self.Path, document.Source, token.Span, declaration, shorthand);
        }
    }

    /// <summary>
    /// Pourquoi ce nom n'est pas valable pour une déclaration : pas un identificateur, ou un mot que le langage réserve ; <see langword="null"/> s'il l'est.
    /// </summary>
    public static string? WhyInvalidName(string name)
    {
        if (!_IdentifierPattern().IsMatch(name)) return $"« {name} » n'est pas un nom valide : des lettres, des chiffres et _, sans commencer par un chiffre";
        if (SyntaxFacts.IsReserved(name) || SyntaxFacts.Keywords.Any(k => k.Text == name)) return $"« {name} » est un mot du langage";
        return null;
    }

    /// <summary>La raison pour laquelle on ne renomme pas cette déclaration ; <see langword="null"/> si on le peut.</summary>
    public static string? WhyNotRenamable(RglSymbol symbol) => symbol.Module is { } module && StdLibrary.Contains(module.Path)
        ? "ce nom vient de la bibliothèque standard : il ne se renomme pas"
        : null;
}
