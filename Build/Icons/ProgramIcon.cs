using Rgl.Diagnostics;
using Rgl.Project;

namespace Rgl.Build.Icons;

/// <summary>
/// L'icône d'un programme compilé (sous Windows) : celle de la clé <c>icon</c> de <c>rgl.json</c>,
/// ou l'icône par défaut de RGL si la clé est absente. Elle n'est jamais copiée dans le projet :
/// elle est préparée dans le cache à chaque build, puis liée à l'exécutable.
/// </summary>
internal static class ProgramIcon
{
    /// <summary>
    /// Écrit l'icône du programme dans <paramref name="directory"/>. Une icône <c>.ico</c> est
    /// reprise telle quelle ; un <c>.png</c> est réduit à chaque taille. Une icône introuvable,
    /// illisible ou d'un autre format avertit, et l'icône par défaut la remplace.
    /// </summary>
    /// <param name="project">Le projet.</param>
    /// <param name="directory">Dossier du cache où écrire l'icône.</param>
    /// <param name="diagnostics">Reçoit les avertissements sur l'icône du manifeste.</param>
    /// <returns>Le chemin du fichier <c>.ico</c> écrit.</returns>
    public static string Prepare(RglProject project, string directory, DiagnosticBag diagnostics)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "app.ico");
        File.WriteAllBytes(path, _Icon(project, diagnostics) ?? WindowsIcon.Default());
        return path;
    }

    /// <summary>L'icône du manifeste en <c>.ico</c> ; <see langword="null"/> pour l'icône par défaut.</summary>
    private static byte[]? _Icon(RglProject project, DiagnosticBag diagnostics)
    {
        // Absente du manifeste : icône par défaut. Introuvable : le manifeste l'a déjà signalé.
        if (project.Manifest?.Icon is not { } icon) return null;
        var source = Path.Combine(project.RootDirectory, icon);
        if (!File.Exists(source)) return null;

        var manifest = Path.Combine(project.RootDirectory, ProjectManifest.FileName);
        switch (Path.GetExtension(source).ToLowerInvariant())
        {
            case ".ico":
                return File.ReadAllBytes(source);

            case ".png":
                if (PngImage.Decode(File.ReadAllBytes(source), out var error) is { } image) return WindowsIcon.FromImage(image);
                diagnostics.Warning($"icône « {icon} » illisible ({error}) : l'icône par défaut est utilisée", manifest);
                return null;

            default:
                diagnostics.Warning($"icône « {icon} » : seuls les formats .png et .ico sont pris en charge, l'icône par défaut est utilisée", manifest);
                return null;
        }
    }
}
