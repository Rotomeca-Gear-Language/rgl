namespace Rgl.Native;

/// <summary>
/// Une bibliothèque C décrite par un fichier <c>forge: native</c> : ce que le C++ généré inclut et ce que l'édition de liens lui ajoute.
/// </summary>
/// <param name="Headers">Les chemins complets des en-têtes à inclure, dans l'ordre.</param>
/// <param name="IncludeDirectories">Les dossiers des en-têtes, pour que ceux-ci trouvent leurs voisins.</param>
/// <param name="Library">Le chemin complet de la bibliothèque statique de la plateforme ; <see langword="null"/> si le fichier n'en donne pas.</param>
/// <param name="Summary">Le bilan des liaisons : ce qui est converti et ce qui ne l'est pas encore ; vide si elles n'ont pas été engendrées.</param>
/// <param name="Unconverted">Les fonctions, <c>struct</c> et <c>enum</c> de l'en-tête que les liaisons n'ont pas converties, avec la raison.</param>
/// <param name="OtherDeclarations">Le nombre de déclarations de l'en-tête que les liaisons ne connaissent pas encore (variables…).</param>
internal sealed record CLibrary(IReadOnlyList<string> Headers, IReadOnlyList<string> IncludeDirectories, string? Library, string Summary = "",
    IReadOnlyList<CBindings.Unconverted>? Unconverted = null, int OtherDeclarations = 0);
