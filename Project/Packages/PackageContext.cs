namespace Rgl.Project.Packages;

/// <summary>
/// Les paquets d'un programme tels que le chargeur de fichiers les voit : le graphe résolu et ce que le projet demande lui-même. Chaque fichier ne voit que
/// les paquets que <em>son</em> manifeste demande — ceux du projet pour un fichier du projet, ceux du paquet pour un fichier d'un paquet.
/// </summary>
/// <param name="Graph">Tous les paquets, dépendances de dépendances comprises.</param>
/// <param name="ProjectDependencies">Les adresses (et versions) que demande le manifeste du projet.</param>
internal sealed record PackageContext(PackageGraph Graph, IReadOnlyDictionary<string, string> ProjectDependencies)
{
    /// <summary>
    /// Vrai quand les paquets n'ont pas pu être résolus (l'éditeur, hors ligne, avec un paquet absent du cache) : la raison est déjà dite, un
    /// <c>import package</c> est alors signalé comme non récupéré (E155) au lieu de se plaindre d'un nom inconnu.
    /// </summary>
    public bool Unresolved { get; init; }

    /// <summary>Le contexte d'un projet dont les paquets viennent d'être résolus.</summary>
    public static PackageContext For(RglProject project, PackageGraph graph) =>
        new(graph, project.Manifest?.Dependencies ?? new Dictionary<string, string>());

    /// <summary>Le nom d'une portée de fichiers de paquet, unique dans le programme : l'adresse, dont tout ce qui n'est pas lettre ou chiffre devient <c>_</c>.</summary>
    public static string BoxName(string source) => "pkg_" + System.Text.RegularExpressions.Regex.Replace(source, "[^A-Za-z0-9]", "_");
}
