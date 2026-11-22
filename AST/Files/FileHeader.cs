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
/// <param name="Settings">Les réglages de compilation et de liaison (<c>include</c>, <c>define</c>, <c>sources</c>, <c>libraries</c>, <c>libdirs</c>, <c>system</c>), dans l'ordre écrit ; vide sans eux.</param>
internal sealed record FileHeader(SourceSpan Span, Identifier? Kind, SourceSpan? Description, SourceSpan? Metadata,
    IReadOnlyList<SourceSpan>? Headers = null, Identifier? Link = null, IReadOnlyList<LibraryEntry>? Library = null,
    IReadOnlyList<HeaderSetting>? Settings = null) : IAstNode;

/// <summary>Une clé de réglage native d'un en-tête : <c>include: "dossier"</c> ou <c>libraries: ["a.lib", "windows:b.dll"]</c>.</summary>
/// <param name="Span">De la clé à la fin de la valeur.</param>
/// <param name="Key">Le nom de la clé.</param>
/// <param name="Values">Les textes écrits, entre guillemets tels qu'écrits.</param>
internal sealed record HeaderSetting(SourceSpan Span, Identifier Key, IReadOnlyList<SourceSpan> Values) : IAstNode;

/// <summary>Une ligne de <c>library: { windows: "lib/x.lib" }</c> : la plateforme et le chemin de la bibliothèque statique.</summary>
/// <param name="Span">De la plateforme au chemin.</param>
/// <param name="Platform">Le nom de la plateforme (<c>windows</c>, <c>linux</c>).</param>
/// <param name="Path">Le chemin, entre guillemets tels qu'écrit.</param>
internal sealed record LibraryEntry(SourceSpan Span, Identifier Platform, SourceSpan Path) : IAstNode;
