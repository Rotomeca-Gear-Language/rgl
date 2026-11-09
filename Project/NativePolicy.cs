namespace Rgl.Project;

/// <summary>Autorisations de code natif par fichier d’origine, sans autorisation transitive des imports.</summary>
internal sealed class NativePolicy(ProjectManifest? manifest, string root)
{
    public static NativePolicy Unrestricted { get; } = new(null, "");
    public bool AllowsCpp(string path) => manifest?.NoCpp != true || Exempt(path);
    public bool AllowsTrust(string path) => manifest?.NoTrust != true || Exempt(path);

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
