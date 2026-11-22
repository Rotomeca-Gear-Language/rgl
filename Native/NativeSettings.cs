using System.Text.RegularExpressions;

namespace Rgl.Native;

/// <summary>Une bibliothèque à lier : son chemin complet, et si elle est dynamique (<c>.dll</c>, <c>.so</c>, <c>.dylib</c>), donc à copier à côté de l'exécutable.</summary>
/// <param name="Path">Le chemin complet du fichier.</param>
/// <param name="IsDynamic">Vrai pour une bibliothèque dynamique.</param>
internal sealed record NativeLibrary(string Path, bool IsDynamic);

/// <summary>
/// Ce qu'il faut pour compiler et lier du code C ou C++ qui n'est pas écrit pour RGL, tel que le manifeste (<c>"native": {…}</c>, pour tout le projet) ou l'en-tête d'un
/// fichier <c>forge: native</c> / <c>forge: descriptor</c> (pour une bibliothèque) le dit : des dossiers d'inclusion, des définitions (<c>-D</c>), des sources à compiler,
/// des bibliothèques à lier (statiques ou dynamiques), les dossiers où en chercher et des bibliothèques du système (<c>-lm</c>, <c>-lws2_32</c>…).
/// </summary>
/// <param name="IncludeDirectories">Les dossiers d'inclusion, en chemins complets.</param>
/// <param name="Defines">Les définitions du préprocesseur : <c>NOM</c> ou <c>NOM=valeur</c>.</param>
/// <param name="Sources">Les fichiers C et C++ à compiler, en chemins complets (les motifs sont déjà développés).</param>
/// <param name="Libraries">Les bibliothèques à lier.</param>
/// <param name="LibraryDirectories">Les dossiers où chercher les bibliothèques du système, en chemins complets.</param>
/// <param name="SystemLibraries">Les noms des bibliothèques du système (<c>m</c>, <c>pthread</c>, <c>ws2_32</c>).</param>
internal sealed partial record NativeSettings(
    IReadOnlyList<string> IncludeDirectories,
    IReadOnlyList<string> Defines,
    IReadOnlyList<string> Sources,
    IReadOnlyList<NativeLibrary> Libraries,
    IReadOnlyList<string> LibraryDirectories,
    IReadOnlyList<string> SystemLibraries)
{
    /// <summary>Les clés d'un manifeste ou d'un en-tête, dans l'ordre où elles sont documentées.</summary>
    public static readonly IReadOnlyList<string> Keys = ["include", "define", "sources", "libraries", "libdirs", "system"];

    /// <summary>Aucun réglage : l'instance que les enregistrements qui en portent un se partagent (leur égalité en dépend).</summary>
    public static readonly NativeSettings Empty = new([], [], [], [], [], []);

    /// <summary>Vrai s'il n'y a rien.</summary>
    public bool IsEmpty => IncludeDirectories.Count + Defines.Count + Sources.Count + Libraries.Count + LibraryDirectories.Count + SystemLibraries.Count == 0;

    /// <summary>
    /// Les options du préprocesseur sous la forme que prend la liste « dossiers d'inclusion » partout où le compilateur la passe à Clang : le chemin d'un dossier (que
    /// l'on préfixe de <c>-I</c>) ou une option <c>-DNOM[=valeur]</c> telle quelle.
    /// </summary>
    public IEnumerable<string> PreprocessorOptions => IncludeDirectories.Concat(Defines.Select(d => "-D" + d));

    /// <summary>La plateforme où tourne le compilateur : <c>windows</c>, <c>linux</c> ou <c>macos</c>.</summary>
    public static string Platform { get; } = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static readonly string[] _platforms = ["windows", "linux", "macos"];

    /// <summary>
    /// Ne garde d'une liste que ce qui vaut pour cette plateforme : un élément écrit <c>windows:ws2_32</c> (ou <c>linux:</c>, <c>macos:</c>) n'est lu que sur elle, et
    /// son préfixe est ôté ; un élément sans préfixe vaut partout.
    /// </summary>
    public static IEnumerable<string> ForThisPlatform(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            var colon = entry.IndexOf(':');
            if (colon > 0 && _platforms.Contains(entry[..colon], StringComparer.Ordinal))
            {
                if (entry[..colon] == Platform) yield return entry[(colon + 1)..];
                continue;
            }
            yield return entry;
        }
    }

    /// <summary>Vrai si ce fichier est une bibliothèque dynamique : <c>.dll</c>, <c>.so</c> (y compris <c>libx.so.1.2</c>) ou <c>.dylib</c>.</summary>
    public static bool IsDynamicLibrary(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".so", StringComparison.Ordinal)
            || name.Contains(".so.", StringComparison.Ordinal) || name.EndsWith(".dylib", StringComparison.Ordinal);
    }

    /// <summary>
    /// Résout des réglages écrits : les chemins relatifs partent de <paramref name="baseDirectory"/>, les motifs de <c>sources</c> sont développés, ce qui est propre à une autre
    /// plateforme est écarté.
    /// </summary>
    /// <param name="raw">Les listes écrites, par clé (<see cref="Keys"/>).</param>
    /// <param name="baseDirectory">Le dossier du manifeste ou du fichier forge.</param>
    /// <param name="restrictTo">Le dossier dont aucun chemin ne doit sortir (celui d'un paquet) ; <see langword="null"/> pour le projet, qui est chez lui.</param>
    /// <param name="forceDynamic">Vrai si <c>link: dynamic</c> est écrit : les bibliothèques sont alors dynamiques.</param>
    /// <param name="fail">Reçoit chaque problème : le message, puis la clé concernée.</param>
    /// <returns>Les réglages ; <see langword="null"/> si un problème a été signalé.</returns>
    public static NativeSettings? Resolve(IReadOnlyDictionary<string, IReadOnlyList<string>> raw, string baseDirectory, string? restrictTo, bool forceDynamic, Action<string, string> fail)
    {
        var ok = true;
        void Problem(string key, string message)
        {
            fail(message, key);
            ok = false;
        }

        IEnumerable<string> Entries(string key) => raw.TryGetValue(key, out var list) ? ForThisPlatform(list) : [];

        string? Path(string key, string entry)
        {
            var full = System.IO.Path.GetFullPath(entry, baseDirectory);
            if (restrictTo is not null && !_Inside(restrictTo, full))
            {
                Problem(key, $"« {entry} » sort du dossier du paquet : un paquet ne lit que ses propres fichiers");
                return null;
            }
            return full;
        }

        var includes = new List<string>();
        foreach (var entry in Entries("include"))
            if (Path("include", entry) is { } full) includes.Add(full);

        var defines = new List<string>();
        foreach (var entry in Entries("define"))
        {
            if (DefinePattern().IsMatch(entry)) defines.Add(entry);
            else Problem("define", $"« {entry} » n'est pas une définition : écris NOM ou NOM=valeur");
        }

        var sources = new List<string>();
        foreach (var entry in Entries("sources"))
        {
            if (Path("sources", entry) is not { } pattern) continue;
            var matches = NativeGlob.Expand(baseDirectory, entry);
            if (matches.Count == 0) Problem("sources", $"aucun fichier C ou C++ ne correspond à « {entry} »");
            foreach (var match in matches)
            {
                if (restrictTo is not null && !_Inside(restrictTo, match)) continue;
                if (!sources.Contains(match, StringComparer.OrdinalIgnoreCase)) sources.Add(match);
            }
            _ = pattern;
        }

        var libraries = new List<NativeLibrary>();
        foreach (var entry in Entries("libraries"))
        {
            if (Path("libraries", entry) is not { } full) continue;
            if (!File.Exists(full)) Problem("libraries", $"la bibliothèque « {entry} » est introuvable");
            else libraries.Add(new NativeLibrary(full, forceDynamic || IsDynamicLibrary(full)));
        }

        var directories = new List<string>();
        foreach (var entry in Entries("libdirs"))
        {
            if (Path("libdirs", entry) is not { } full) continue;
            if (!Directory.Exists(full)) Problem("libdirs", $"le dossier « {entry} » est introuvable");
            else directories.Add(full);
        }

        var system = new List<string>();
        foreach (var entry in Entries("system"))
        {
            if (SystemPattern().IsMatch(entry)) system.Add(entry);
            else Problem("system", $"« {entry} » n'est pas un nom de bibliothèque du système : écris-le sans « -l » ni extension (m, pthread, ws2_32)");
        }

        return ok ? new NativeSettings(includes, defines, sources, libraries, directories, system) : null;
    }

    /// <summary>Vrai si <paramref name="path"/> est dans <paramref name="root"/> (lexicalement : <c>..</c> est déjà résolu par <see cref="System.IO.Path.GetFullPath(string)"/>).</summary>
    private static bool _Inside(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison) || path.Equals(prefix.TrimEnd(System.IO.Path.DirectorySeparatorChar), comparison);
    }

    /// <summary>Réunit des réglages : ceux de gauche d'abord, sans doublon.</summary>
    public NativeSettings Merge(NativeSettings other) => other.IsEmpty ? this : IsEmpty ? other : new NativeSettings(
        [.. IncludeDirectories.Concat(other.IncludeDirectories).Distinct()],
        [.. Defines.Concat(other.Defines).Distinct()],
        [.. Sources.Concat(other.Sources).Distinct(StringComparer.OrdinalIgnoreCase)],
        [.. Libraries.Concat(other.Libraries).DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase)],
        [.. LibraryDirectories.Concat(other.LibraryDirectories).Distinct()],
        [.. SystemLibraries.Concat(other.SystemLibraries).Distinct()]);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(=.*)?$")]
    private static partial Regex DefinePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.+][A-Za-z0-9_.+-]*$")]
    private static partial Regex SystemPattern();
}

/// <summary>
/// Les motifs de fichiers de <c>sources</c> : <c>*</c> (tout sauf <c>/</c>), <c>?</c> (un caractère) et <c>**</c> (n'importe quels sous-dossiers), relatifs à un dossier. Ne ressortent que
/// les fichiers C et C++ (<c>.c</c>, <c>.cc</c>, <c>.cpp</c>, <c>.cxx</c>) ; les dossiers cachés et <c>out</c> ne sont pas parcourus. Un chemin sans motif désigne ce fichier.
/// </summary>
internal static class NativeGlob
{
    private static readonly string[] _extensions = [".c", ".cc", ".cpp", ".cxx"];

    /// <summary>Les fichiers qui correspondent, en chemins complets, dans l'ordre alphabétique.</summary>
    public static IReadOnlyList<string> Expand(string baseDirectory, string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        if (!normalized.Contains('*') && !normalized.Contains('?'))
        {
            var full = Path.GetFullPath(normalized, baseDirectory);
            return File.Exists(full) ? [full] : [];
        }

        // Le plus long préfixe sans motif est le dossier d'où l'on part.
        var segments = normalized.Split('/');
        var fixedCount = segments.TakeWhile(s => !s.Contains('*') && !s.Contains('?')).Count();
        var root = Path.GetFullPath(string.Join('/', segments.Take(fixedCount)), baseDirectory);
        if (!Directory.Exists(root)) return [];

        var builder = new System.Text.StringBuilder("^");
        var rest = segments.Skip(fixedCount).ToArray();
        for (var i = 0; i < rest.Length; ++i)
        {
            builder.Append(_Segment(rest[i]));
            if (rest[i] != "**" && i < rest.Length - 1) builder.Append('/');
        }
        var expression = new Regex(builder.Append('$').ToString(), RegexOptions.CultureInvariant);
        var found = new List<string>();
        foreach (var file in _Files(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (expression.IsMatch(relative) && _extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) found.Add(file);
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static string _Segment(string segment)
    {
        if (segment == "**") return "(?:.*/)?";
        var builder = new System.Text.StringBuilder();
        foreach (var c in segment)
            builder.Append(c switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(c.ToString()) });
        return builder.ToString();
    }

    private static IEnumerable<string> _Files(string directory)
    {
        foreach (var file in Directory.GetFiles(directory)) yield return file;
        foreach (var child in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name.Equals("out", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in _Files(child)) yield return file;
        }
    }
}
