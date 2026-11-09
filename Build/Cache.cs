using System.Security.Cryptography;
using System.Text;
using Rgl.Project;

namespace Rgl.Build;

/// <summary>
/// Cache de RGL : C++ généré et artefacts de build. Hors du dossier du projet, et sous
/// Windows dans <c>%LOCALAPPDATA%</c> pour ne pas être synchronisé avec un profil itinérant.
/// </summary>
internal static class Cache
{
    /// <summary>Racine du cache : <c>%LOCALAPPDATA%\rgl</c> sous Windows, <c>~/.cache/rgl</c> ailleurs.</summary>
    public static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            // La variable d'environnement prime (le système, lui, rend toujours le dossier du profil) : un processus lancé avec un autre LOCALAPPDATA a son propre cache.
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return Path.Combine(string.IsNullOrEmpty(local) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : local, "rgl");
        }

        // XDG_CACHE_HOME vaut ~/.cache par défaut ; on le respecte s'il est défini.
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var root = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdg;
        return Path.Combine(root, "rgl");
    }

    /// <summary>
    /// Dossier propre à un projet : son nom (pour s'y retrouver) suivi d'une empreinte de son chemin,
    /// pour que deux projets homonymes ne partagent pas leur cache.
    /// </summary>
    /// <param name="cacheRoot">Racine du cache (voir <see cref="DefaultRoot"/>).</param>
    /// <param name="project">Projet dont on veut le dossier.</param>
    public static string ProjectDirectory(string cacheRoot, RglProject project)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory));
        if (OperatingSystem.IsWindows())
            root = root.ToUpperInvariant(); // chemins insensibles à la casse

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12];
        return Path.Combine(cacheRoot, "projets", $"{SafeName(project.Name)}-{hash}");
    }

    /// <summary>
    /// Rend un nom de projet utilisable comme nom de dossier : caractères interdits et espaces
    /// remplacés par <c>_</c>, 40 caractères au plus.
    /// </summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return safe.Length > 40 ? safe[..40] : safe;
    }
}
