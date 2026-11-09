using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Files;

/// <summary>
/// L'en-tête d'un fichier <c>.forge</c> : <c>forge: genre</c>, puis <c>description: "texte"</c> et
/// <c>metadata: { … }</c> facultatifs. Le fichier <c>.rgl</c> d'entrée n'en a pas.
/// </summary>
/// <param name="Span">De la première clé à la fin de la dernière valeur.</param>
/// <param name="Kind">Le genre du fichier (<c>train</c>, <c>mold</c>, <c>gears</c>…) ; <see langword="null"/> si <c>forge:</c> manque.</param>
/// <param name="Description">La description décodée ; <see langword="null"/> si elle manque.</param>
/// <param name="Metadata">Les métadonnées JSON telles qu'écrites, ignorées par le compilateur ; <see langword="null"/> si elles manquent.</param>
/// <param name="Headers">Les chemins des <c>header:</c> d'un fichier <c>forge: native</c>, entre guillemets tels qu'écrits ; vide sans eux.</param>
/// <param name="Link">Le mode de liaison de <c>link:</c> (<c>static</c>, <c>dynamic</c>) ; <see langword="null"/> s'il manque.</param>
/// <param name="Library">La bibliothèque de <c>library:</c> par plateforme ; <see langword="null"/> si elle manque.</param>
internal sealed record FileHeader(SourceSpan Span, Identifier? Kind, SourceSpan? Description, SourceSpan? Metadata,
    IReadOnlyList<SourceSpan>? Headers = null, Identifier? Link = null, IReadOnlyList<LibraryEntry>? Library = null) : IAstNode;

/// <summary>Une ligne de <c>library: { windows: "lib/x.lib" }</c> : la plateforme et le chemin de la bibliothèque statique.</summary>
/// <param name="Span">De la plateforme au chemin.</param>
/// <param name="Platform">Le nom de la plateforme (<c>windows</c>, <c>linux</c>).</param>
/// <param name="Path">Le chemin, entre guillemets tels qu'écrit.</param>
internal sealed record LibraryEntry(SourceSpan Span, Identifier Platform, SourceSpan Path) : IAstNode;
