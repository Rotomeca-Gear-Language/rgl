using Rgl.Project.Packages;

namespace Rgl.Project;

/// <summary>
/// Autorisations de code natif par fichier d’origine, sans autorisation transitive des imports. Un fichier d'un paquet n'a le droit au C++
/// que si le projet racine le dit dans <c>package-trust.cpp</c> ; le projet seul autorise, jamais un paquet ni ses dépendances.
/// </summary>
internal sealed class NativePolicy(ProjectManifest? manifest, string root, PackageGraph? packages = null)
{
    public static NativePolicy Unrestricted { get; } = new(null, "");

    /// <summary>Le paquet qui contient le fichier <paramref name="path"/> ; <see langword="null"/> s'il est au projet ou dans la std.</summary>
    public ResolvedPackage? PackageOf(string path)
    {
        if (packages is null || packages.Packages.Count == 0 || !Path.IsPathFullyQualified(path)) return null;
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return packages.Packages.FirstOrDefault(p => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Directory)) + Path.DirectorySeparatorChar, comparison));
    }

    public bool AllowsCpp(string path) => PackageOf(path) is { } package ? manifest?.Trust.Cpp.Contains(package.Source) == true : manifest?.NoCpp != true || Exempt(path);
    public bool AllowsTrust(string path) => PackageOf(path) is { } package ? manifest?.NoTrust != true || manifest?.Trust.Cpp.Contains(package.Source) == true : manifest?.NoTrust != true || Exempt(path);

    /// <summary>Vrai si le fichier peut lier une bibliothèque précompilée : celles d'un paquet ne sont gardées qu'avec <c>package-trust.binaries</c>.</summary>
    public bool AllowsBinaries(string path) => PackageOf(path) is not { } package || manifest?.Trust.Binaries.Contains(package.Source) == true;

    /// <summary>Ce qui interdit le C++ de ce fichier, pour les messages : <c>no-cpp</c>, ou le paquet et la clé du projet qui l'autoriserait.</summary>
    public string Refusal(string path) => PackageOf(path) is { } package ? $"le paquet « {package.Source} » (écris-le dans package-trust.cpp du projet pour l'autoriser)" : "no-cpp";

    public static bool ValidTrustPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || Path.IsPathRooted(path) || path.Replace('\\', '/').StartsWith('/') || path.Contains(':') || !path.EndsWith(".forge", StringComparison.OrdinalIgnoreCase)) return false;
        var depth = 0;
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") { if (--depth < 0) return false; }
            else depth++;
        }
        return depth > 0;
    }

    private bool Exempt(string path)
    {
        if (StdLibrary.Contains(path)) return true;
        if (manifest is null || root.Length == 0 || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var physicalRoot = Physical(root);
            var source = Physical(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!source.StartsWith(Path.TrimEndingDirectorySeparator(physicalRoot) + Path.DirectorySeparatorChar, comparison)) return false;
            return manifest.CppTrust.Any(p => ValidTrustPath(p) && string.Equals(source, Physical(Path.Combine(root, p.Replace('\\', Path.DirectorySeparatorChar))), comparison));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static string Physical(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        var directory = parent is null ? full : Path.Combine(Physical(parent), Path.GetFileName(full));
        FileSystemInfo info = Directory.Exists(directory) ? new DirectoryInfo(directory) : new FileInfo(directory);
        return info.LinkTarget is null ? directory : info.ResolveLinkTarget(true)?.FullName ?? directory;
    }
}
