using System.Formats.Tar;
using System.IO.Compression;

namespace Rgl.Project.Packages;

/// <summary>
/// Lit l'archive <c>.tar.gz</c> d'un paquet dans un dossier. Une archive vient d'Internet : rien n'en sort du dossier, aucun lien n'est suivi, et la
/// taille et le nombre de fichiers sont plafonnés. Les forges rangent tout sous un dossier racine unique (<c>json-1.2.0/</c>), que l'on retire. Ce qui
/// n'a rien à faire dans un paquet de sources (exécutables, archives, dossiers de build, <c>.git</c>…) n'est pas extrait : voir <see cref="Reason"/>.
/// </summary>
internal static class PackageArchive
{
    /// <summary>Extensions qui ne sont jamais extraites : un paquet livre des sources.</summary>
    private static readonly string[] _alwaysRemoved = ["exe", "msi", "bin", "pdb", "zip", "7z", "tar", "gz"];

    /// <summary>
    /// Extensions des fichiers binaires de bibliothèques : écartées, sauf si <c>package-trust</c> autorise le paquet (un paquet de confiance peut
    /// livrer un <c>.a</c> ou une <c>.dll</c>).
    /// </summary>
    private static readonly string[] _nativeRemoved = ["dll", "so", "dylib", "a", "lib", "obj", "o"];

    /// <summary>Dossiers qui ne sont jamais extraits, à quelque profondeur que ce soit : dépôt, intégration continue, dépendances et sorties de build.</summary>
    private static readonly string[] _foldersRemoved = [".git", ".github", "node_modules", "out", "bin", "obj"];

    /// <summary>Ce que donne une extraction : l'erreur, s'il y en a une, et les fichiers écartés, comptés par raison.</summary>
    /// <param name="Error">Pourquoi l'archive est refusée ; <see langword="null"/> si tout s'est bien passé.</param>
    /// <param name="Removed">Nombre de fichiers écartés par raison (<c>.exe</c>, <c>.dll</c>, <c>.git/</c>…).</param>
    internal sealed record Result(string? Error, IReadOnlyDictionary<string, uint> Removed);

    /// <summary>
    /// Pourquoi un fichier n'est pas extrait ; <see langword="null"/> s'il l'est.
    /// </summary>
    /// <param name="relative">Les segments du chemin du fichier, sans le dossier racine de l'archive.</param>
    /// <param name="keepNative">Vrai si les fichiers binaires de bibliothèques sont gardés (<c>package-trust</c>).</param>
    public static string? Reason(IReadOnlyList<string> relative, bool keepNative)
    {
        foreach (var folder in relative.Take(relative.Count - 1))
        {
            if (_foldersRemoved.Contains(folder, StringComparer.OrdinalIgnoreCase)) return folder + "/";
        }

        var name = relative[^1];
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        // Une bibliothèque partagée versionnée s'écrit libx.so.1.2 : son extension utile est « so ».
        if (name.Contains(".so.", StringComparison.OrdinalIgnoreCase)) extension = "so";
        if (_alwaysRemoved.Contains(extension)) return "." + extension;
        if (!keepNative && _nativeRemoved.Contains(extension)) return "." + extension;
        return null;
    }

    /// <summary>Les plafonds d'une extraction.</summary>
    /// <param name="Files">Nombre de fichiers au plus.</param>
    /// <param name="Bytes">Octets écrits au plus, une fois décompressés.</param>
    internal sealed record Limits(uint Files, long Bytes)
    {
        /// <summary>20 000 fichiers et 100 Mo : bien plus que des sources.</summary>
        public static readonly Limits Default = new(20_000, 100L * 1024 * 1024);
    }

    /// <summary>
    /// Extrait l'archive dans <paramref name="destination"/>, que la fonction crée. Après une erreur le dossier est à moitié écrit : à l'appelant
    /// de le supprimer.
    /// </summary>
    /// <param name="archive">Le flux <c>.tar.gz</c>.</param>
    /// <param name="destination">Dossier à écrire ; il ne doit pas exister.</param>
    /// <param name="limits">Plafonds ; <see cref="Limits.Default"/> sans valeur.</param>
    /// <param name="keepNative">Vrai si <c>package-trust.binaries</c> autorise le paquet : ses binaires de bibliothèques (<c>.dll</c>, <c>.so</c>, <c>.a</c>…) sont gardés.</param>
    /// <returns>L'erreur éventuelle (l'archive est refusée) et les fichiers écartés ; ils ne comptent pas dans les plafonds.</returns>
    public static Result Extract(Stream archive, string destination, Limits? limits = null, bool keepNative = false)
    {
        var removed = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        var error = _Extract(archive, destination, limits ?? Limits.Default, keepNative, removed);
        return new Result(error, removed);
    }

    private static string? _Extract(Stream archive, string destination, Limits limits, bool keepNative, SortedDictionary<string, uint> removed)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var inside = root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        try
        {
            using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip, leaveOpen: true);

            string? archiveRoot = null;
            uint files = 0;
            long bytes = 0;
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue;

                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    return $"« {entry.Name} » est un lien : une archive de paquet n'en contient pas";
                var isDirectory = entry.EntryType == TarEntryType.Directory;
                if (!isDirectory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    return $"« {entry.Name} » n'est ni un fichier ni un dossier";

                var segments = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
                if (segments.Length == 0) continue;
                if (segments.Any(s => s == ".."))
                    return $"« {entry.Name} » sort du dossier du paquet";

                if (archiveRoot is null) archiveRoot = segments[0];
                else if (segments[0] != archiveRoot)
                    return "l'archive n'a pas de dossier racine unique";
                if (segments.Length == 1)
                {
                    if (!isDirectory) return $"« {entry.Name} » est un fichier au niveau de la racine de l'archive, qui ne contient qu'un dossier";
                    continue;
                }

                var relative = segments.Skip(1).ToArray();
                if (isDirectory && _foldersRemoved.Contains(relative[^1], StringComparer.OrdinalIgnoreCase)) continue;
                if (Reason(relative, keepNative) is { } reason)
                {
                    // Un dossier écarté ne se crée pas ; chaque fichier écarté est compté sous sa raison.
                    if (!isDirectory) removed[reason] = removed.GetValueOrDefault(reason) + 1;
                    continue;
                }
                if (relative.FirstOrDefault(_UnsafeSegment) is { } bad)
                    return $"« {entry.Name} » : « {bad} » n'est pas un nom de fichier valide";

                var target = Path.GetFullPath(Path.Combine([root, .. relative]));
                if (!target.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                    return $"« {entry.Name} » sort du dossier du paquet";

                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (++files > limits.Files) return $"l'archive contient plus de {limits.Files} fichiers";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) || Directory.Exists(target))
                    return $"« {entry.Name} » est écrit deux fois (ou deux noms ne diffèrent que par la casse)";
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (entry.DataStream is { } data)
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        bytes += read;
                        if (bytes > limits.Bytes) return $"l'archive fait plus de {limits.Bytes / (1024 * 1024)} Mo une fois décompressée";
                        output.Write(buffer, 0, read);
                    }
                }
            }

            return archiveRoot is null ? "l'archive est vide" : null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException or UnauthorizedAccessException)
        {
            return $"archive illisible : {e.Message}";
        }
    }

    /// <summary>Un nom de fichier que l'on n'écrit pas : caractères interdits, ou qui finit par un point ou une espace (que Windows retire).</summary>
    private static bool _UnsafeSegment(string segment) =>
        segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ');
}
