using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Project;

namespace Rgl.Build;

/// <summary>Résout puis copie les ressources déclarées dans <c>rgl.json</c>.</summary>
internal static class ProjectAssets
{
    /// <summary>Résout les motifs avant la compilation, afin qu'un motif invalide fasse échouer le build sans produire une sortie partielle.</summary>
    public static IReadOnlyList<(string Source, string Relative)>? Resolve(RglProject project, DiagnosticBag diagnostics)
    {
        if (project.Manifest?.Assets is not { Length: > 0 } patterns) return [];

        var files = Directory.EnumerateFiles(project.RootDirectory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }).Select(path => (Source: path, Relative: Path.GetRelativePath(project.RootDirectory, path).Replace('\\', '/')))
          .Where(file => file.Relative != "out" && !file.Relative.StartsWith("out/", StringComparison.OrdinalIgnoreCase))
          .OrderBy(file => file.Relative, StringComparer.Ordinal)
          .ToArray();

        Dictionary<string, (string Source, string Relative)> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (var written in patterns)
        {
            var pattern = written.StartsWith("./", StringComparison.Ordinal) ? written[2..] : written.TrimStart('/');
            var hasGlob = pattern.IndexOfAny(['*', '?']) >= 0;
            var regex = hasGlob ? new Regex(Glob(pattern), RegexOptions.CultureInvariant) : null;
            var matches = files.Where(file => regex?.IsMatch(file.Relative) ??
                file.Relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) || file.Relative.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                diagnostics.Error($"aucune ressource ne correspond à « {written} »", Path.Combine(project.RootDirectory, ProjectManifest.FileName));
                return null;
            }
            foreach (var match in matches) selected[match.Relative] = match;
        }
        return selected.Values.OrderBy(file => file.Relative, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Copie les ressources dans le dossier de l'exécutable, en gardant leur chemin depuis la racine du projet.</summary>
    public static bool Copy(IReadOnlyList<(string Source, string Relative)> assets, string executable, DiagnosticBag diagnostics)
    {
        var output = Path.GetDirectoryName(executable)!;
        foreach (var (source, relative) in assets)
        {
            var destination = Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                diagnostics.Error($"impossible de copier la ressource « {relative} » : {error.Message}", source);
                return false;
            }
        }
        return true;
    }

    private static string Glob(string pattern)
    {
        var result = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; ++i)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; result.Append("(?:.*/)?"); }
                else result.Append(".*");
            }
            else if (pattern[i] == '*') result.Append("[^/]*");
            else if (pattern[i] == '?') result.Append("[^/]");
            else result.Append(Regex.Escape(pattern[i].ToString()));
        }
        return result.Append('$').ToString();
    }
}
