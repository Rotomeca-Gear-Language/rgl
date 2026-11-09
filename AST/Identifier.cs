using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST;

/// <summary>Un nom écrit dans le source : nom de paramètre, de gear, de type…</summary>
/// <param name="Span">Morceau du source qui contient le nom ; vide si le nom manque.</param>
internal sealed record Identifier(SourceSpan Span) : IAstNode
{
    /// <summary>Le nom tel qu'écrit, sans les espaces d'un nom qualifié (<c>Commandes . ajouter</c>) ; vide si le nom manque.</summary>
    public string Name => Span.Text.Any(char.IsWhiteSpace) ? string.Concat(Span.Text.Where(c => !char.IsWhiteSpace(c))) : Span.Text;

    /// <summary>Vrai si le nom était attendu mais absent (récupération sur erreur).</summary>
    public bool IsMissing => Span.Length.Value == 0;
}
