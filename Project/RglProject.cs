using Rgl.Diagnostics;

namespace Rgl.Project;

/// <summary>
/// Un projet RGL : son fichier <c>.rgl</c> d'entrée et le dossier qui le contient, où se trouve
/// aussi le manifeste <c>rgl.json</c>.
/// </summary>
/// <param name="RootDirectory">Dossier racine du projet : celui du fichier d'entrée.</param>
/// <param name="EntryFile">Chemin complet du fichier <c>.rgl</c> d'entrée.</param>
/// <param name="Manifest">Absent pour un fichier <c>.rgl</c> isolé, compilé sans manifeste.</param>
internal sealed record RglProject(string RootDirectory, string EntryFile, ProjectManifest? Manifest)
{
    /// <summary>Extension du fichier d'entrée, unique dans un projet.</summary>
    public const string EntryExtension = ".rgl";

    /// <summary>Extension des autres fichiers source, qui portent un en-tête <c>forge:</c>.</summary>
    public const string ForgeExtension = ".forge";

    /// <summary>Fin du nom d'un fichier de test : <c>calcul.test.rgl</c> se lance seul (<c>rgl exec calcul.test.rgl</c>) et n'est jamais le point d'entrée du dossier.</summary>
    public const string TestSuffix = ".test" + EntryExtension;

    /// <summary>Vrai pour un fichier de test (<c>nom.test.rgl</c>).</summary>
    public static bool IsTestFile(string path) => path.EndsWith(TestSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Nom du projet : celui du manifeste, sinon celui du fichier d'entrée. Un fichier de test a le sien (<c>projet-calcul</c>), pour que son exécutable et
    /// son cache ne remplacent pas ceux du programme.
    /// </summary>
    public string Name => IsTestFile(EntryFile)
        ? $"{Manifest?.Name ?? "tests"}-{Path.GetFileName(EntryFile)[..^TestSuffix.Length]}"
        : Manifest?.Name ?? Path.GetFileNameWithoutExtension(EntryFile);

    /// <summary>
    /// Trouve le fichier d'entrée (donné directement, ou seul <c>.rgl</c> du dossier de <c>-d</c>,
    /// sans ses sous-dossiers) puis charge le manifeste voisin s'il existe.
    /// </summary>
    /// <param name="file">Fichier donné en argument, ou <c>null</c> si <paramref name="directory"/> est donné.</param>
    /// <param name="directory">Dossier donné par <c>-d</c>, ou <c>null</c> si <paramref name="file"/> est donné.</param>
    /// <param name="workingDirectory">Dossier courant, base des chemins relatifs.</param>
    /// <param name="diagnostics">Reçoit les erreurs de résolution et celles du manifeste.</param>
    /// <returns>Le projet, ou <c>null</c> si une erreur a été signalée.</returns>
    public static RglProject? Load(string? file, string? directory, string workingDirectory, DiagnosticBag diagnostics)
    {
        var entry = file is not null
            ? ResolveFile(Path.GetFullPath(file, workingDirectory), diagnostics)
            : ResolveDirectory(Path.GetFullPath(directory!, workingDirectory), diagnostics);
        if (entry is null)
            return null;

        var root = Path.GetDirectoryName(entry)!;
        var manifestPath = Path.Combine(root, ProjectManifest.FileName);
        ProjectManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            manifest = ProjectManifest.Load(manifestPath, diagnostics);
            if (manifest is null)
                return null;
        }

        return new RglProject(root, entry, manifest);
    }

    /// <summary>Vérifie que le fichier donné existe et porte l'extension <c>.rgl</c>.</summary>
    /// <returns>Le chemin du fichier, ou <c>null</c> si une erreur a été signalée.</returns>
    private static string? ResolveFile(string path, DiagnosticBag diagnostics)
    {
        if (!File.Exists(path))
        {
            diagnostics.Error(Directory.Exists(path)
                ? "c'est un dossier : utiliser « -d <dossier> »"
                : "fichier introuvable", path);
            return null;
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(ForgeExtension, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Error("un fichier .forge n'est pas un point d'entrée : donner le fichier .rgl du projet", path);
            return null;
        }
        if (!extension.Equals(EntryExtension, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Error("le fichier d'entrée doit avoir l'extension .rgl", path);
            return null;
        }

        return path;
    }

    /// <summary>Trouve l'unique fichier <c>.rgl</c> du dossier, sans descendre dans ses sous-dossiers.</summary>
    /// <returns>Le chemin du fichier, ou <c>null</c> s'il n'y en a aucun ou plusieurs.</returns>
    private static string? ResolveDirectory(string path, DiagnosticBag diagnostics)
    {
        if (!Directory.Exists(path))
        {
            diagnostics.Error("dossier introuvable", path);
            return null;
        }

        var candidates = Directory.GetFiles(path, "*" + EntryExtension, SearchOption.TopDirectoryOnly)
            // Sous Windows, le motif « *.rgl » attrape aussi « *.rglx » (correspondance sur 8.3).
            .Where(f => Path.GetExtension(f).Equals(EntryExtension, StringComparison.OrdinalIgnoreCase) && !IsTestFile(f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        switch (candidates.Length)
        {
            case 1:
                return candidates[0];
            case 0:
                diagnostics.Error("aucun fichier .rgl dans ce dossier", path);
                return null;
            default:
                var names = string.Join(", ", candidates.Select(Path.GetFileName));
                diagnostics.Error($"un projet n'a qu'un fichier .rgl, ce dossier en contient {candidates.Length} : {names}", path);
                return null;
        }
    }
}
