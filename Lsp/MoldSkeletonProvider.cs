using System.Text.RegularExpressions;

namespace Rgl.Lsp;

/// <summary>
/// La valeur d'un mold à écrire d'un coup : après <c>Vector2 vec:</c>, l'éditeur propose en grisé <c>{x: , y: }</c> ; Tab l'écrit et place le curseur après
/// le <c>:</c> du premier champ (les suivants par Tab). Seuls les champs obligatoires (sans valeur par défaut, non nullables) sont écrits, dans l'ordre du mold.
/// </summary>
internal static partial class MoldSkeletonProvider
{
    /// <summary>Le début d'une ligne qui déclare une variable d'un type nommé : <c>[mut|const] Type nom:</c>, rien après.</summary>
    [GeneratedRegex(@"^\s*(?:(?:mut|const)\s+)*(?<type>[A-Za-z_][A-Za-z0-9_.]*(?:<[^<>]*>)?\??)\s+[A-Za-z_][A-Za-z0-9_]*\s*:(?<space>\s*)$")]
    private static partial Regex _Declaration();

    /// <summary>Ce que l'éditeur écrit à la place du curseur : <see cref="Snippet"/> est un extrait de l'éditeur (<c>$1</c>, <c>$2</c>… : les places à remplir).</summary>
    /// <param name="Snippet">L'extrait, avec une espace devant si le curseur suit directement le <c>:</c>.</param>
    internal sealed record Skeleton(string Snippet);

    /// <summary>Le squelette de la valeur à la place <paramref name="offset"/> ; <see langword="null"/> si le curseur n'est pas juste après <c>Type nom:</c> d'un mold qui a des champs obligatoires.</summary>
    public static Skeleton? At(SemanticDocument document, int offset)
    {
        var content = document.Source.Content;
        if (offset < 0 || offset > content.Length) return null;

        var lineStart = offset == 0 ? 0 : content.LastIndexOf('\n', offset - 1) + 1;
        var lineEnd = content.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = content.Length;
        // Rien d'écrit après le curseur : sinon la valeur existe déjà.
        if (content[offset..lineEnd].Trim().Length > 0) return null;

        var match = _Declaration().Match(content[lineStart..offset]);
        if (!match.Success) return null;

        var type = SemanticDocument.StripNullable(match.Groups["type"].Value);
        if (type is "var" or "const" or "mut") return null;
        if (document.ResolveTypeText(type, offset) is not { Kind: RglSymbolKind.Mold or RglSymbolKind.Error }) return null;

        var required = document.MembersOf(type, offset)
            .Where(m => m.Kind == RglSymbolKind.Field).DistinctBy(m => m.Name)
            .Where(m => m.Node is AST.Molds.MoldField { Default: null } field && !field.Type.Span.Text.TrimEnd().EndsWith('?'))
            .ToArray();
        if (required.Length == 0) return null;

        var fields = string.Join(", ", required.Select((f, i) => $"{f.Name}: ${i + 1}"));
        return new Skeleton((match.Groups["space"].Length == 0 ? " " : "") + "{" + fields + "}$0");
    }
}
