using Rgl.AST.Files;
using Rgl.Project;
using Rgl.Project.Packages;

namespace Rgl.Lsp;

/// <summary>Un paquet que le document peut importer, lu pour l'éditeur.</summary>
/// <param name="Package">Le paquet résolu : son adresse, sa version, son manifeste.</param>
/// <param name="Index">Son <c>index.forge</c>, qui montre ce que le paquet exporte (les noms des fichiers qu'il étale, ou qu'il exporte un à un).</param>
internal sealed record EditorPackage(ResolvedPackage Package, ModuleInfo Index)
{
    /// <summary>Le nom court d'un <c>import package</c> : le champ <c>name</c> du manifeste du paquet.</summary>
    public string Name => Package.Manifest.Name;
}

/// <summary>Lit un paquet du cache comme le compilateur le voit : son index, et les noms des fichiers qu'il exporte.</summary>
internal static class PackageModules
{
    private sealed record Cached(DateTime Stamp, EditorPackage Result);

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Le paquet lu ; <see langword="null"/> si son index manque ou ne se lit pas.</summary>
    public static EditorPackage? Read(ResolvedPackage package)
    {
        var indexPath = Path.Combine(package.Directory, PackageManifest.IndexFile);
        try
        {
            if (!File.Exists(indexPath)) return null;
            var stamp = File.GetLastWriteTimeUtc(indexPath);
            lock (_lock)
            {
                if (_cache.TryGetValue(package.Directory, out var cached) && cached.Stamp == stamp && cached.Result.Package == package) return cached.Result;
            }

            var index = ModuleInfo.Build(indexPath, File.ReadAllText(indexPath), package.Manifest.Name);
            var members = _Files(package.Directory).Where(f => !f.Equals(indexPath, StringComparison.OrdinalIgnoreCase))
                .Select(f => ModuleInfo.Build(f, File.ReadAllText(f))).ToList();

            // Ce que l'index exporte : tous les noms exportés d'un fichier étalé (export {...Fichier}), ou un nom écrit (export {a, b}) trouvé dans les fichiers.
            var exported = new Dictionary<string, RglSymbol>(StringComparer.Ordinal);
            foreach (var name in index.Unit.Statements.OfType<ExportDeclaration>().SelectMany(e => e.Names))
            {
                switch (name.Kind)
                {
                    case ExportKind.Spread:
                        if (members.FirstOrDefault(m => m.Name == name.Name.Name) is { } member)
                            foreach (var (symbolName, symbol) in member.Exported) exported.TryAdd(symbolName, symbol);
                        break;
                    case ExportKind.Name:
                        foreach (var candidate in members)
                            if (candidate.Exported.TryGetValue(name.Name.Name, out var symbol)) { exported.TryAdd(name.Name.Name, symbol); break; }
                        break;
                }
            }
            index.SetExported(exported);

            var result = new EditorPackage(package, index);
            lock (_lock) _cache[package.Directory] = new Cached(stamp, result);
            return result;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Les <c>.forge</c> d'un paquet, sans les dossiers cachés ni les boxes (opaques).</summary>
    private static IEnumerable<string> _Files(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*.forge", SearchOption.TopDirectoryOnly).Where(f => Path.GetExtension(f).Equals(".forge", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            yield return file;
        foreach (var child in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name.EndsWith(".box", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in _Files(child)) yield return file;
        }
    }
}
