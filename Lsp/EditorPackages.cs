using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Project.Packages;

namespace Rgl.Lsp;

/// <summary>
/// Une source de paquets qui n'a rien : l'éditeur lit les paquets du cache et de <c>rgl.lock</c>, il ne télécharge jamais (« rgl restore » le fait). Un paquet absent
/// du cache est signalé comme introuvable, avec cette consigne.
/// </summary>
internal sealed class OfflinePackageSource : IPackageSource
{
    /// <inheritdoc/>
    public PackageFetch Fetch(PackageId id, string version) =>
        new(null, "il n'est pas dans le cache ou son contenu n'est pas celui de rgl.lock : lance « rgl restore »", NotFound: true);

    /// <inheritdoc/>
    public PackageVersions Versions(PackageId id) => new(null, "l'éditeur ne lit pas les versions publiées");
}

/// <summary>Les paquets d'un projet vus par l'éditeur : résolus depuis le cache, sans réseau et sans modifier <c>rgl.lock</c>.</summary>
/// <param name="Context">Ce que le chargeur de fichiers reçoit (<see cref="PackageContext.Unresolved"/> si la résolution a échoué).</param>
/// <param name="Diagnostics">Ce que la résolution a signalé (E155 à E159), sans position : l'éditeur les montre sur la première ligne.</param>
internal sealed record EditorPackageResult(PackageContext Context, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Le graphe, vide si la résolution a échoué.</summary>
    public PackageGraph Graph => Context.Graph;
}

/// <summary>Résout les paquets d'un projet pour l'éditeur, en mémoire jusqu'à ce que le manifeste ou le lock change.</summary>
internal static class EditorPackages
{
    /// <summary>Combien de temps un échec est gardé : un « rgl restore » lancé dans un terminal ne change ni le manifeste ni le lock, mais le cache.</summary>
    private const long FailureLifetime = 1500;

    private sealed record Entry(DateTime Manifest, DateTime Lock, long At, bool Failed, EditorPackageResult Result);

    /// <summary>La racine du cache où sont les paquets ; celle de RGL, les tests y mettent un dossier à eux.</summary>
    public static Func<string> CacheRoot { get; set; } = Rgl.Build.Cache.DefaultRoot;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Entry> _results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Un projet sans dépendance : rien à résoudre.</summary>
    public static EditorPackageResult None(ProjectManifest? manifest) =>
        new(new PackageContext(PackageGraph.Empty, manifest?.Dependencies ?? new Dictionary<string, string>()), []);

    /// <summary>Les paquets du projet de <paramref name="root"/>.</summary>
    public static EditorPackageResult For(string root, ProjectManifest? manifest)
    {
        var manifestPath = Path.Combine(root, ProjectManifest.FileName);
        var lockPath = Path.Combine(root, PackageLock.FileName);
        if (manifest is null || manifest.Dependencies.Count == 0 && !File.Exists(lockPath)) return None(manifest);

        var stamps = (Manifest: File.GetLastWriteTimeUtc(manifestPath), Lock: File.GetLastWriteTimeUtc(lockPath));
        lock (_lock)
        {
            if (_results.TryGetValue(root, out var known) && known.Manifest == stamps.Manifest && known.Lock == stamps.Lock
                && (!known.Failed || Environment.TickCount64 - known.At < FailureLifetime))
                return known.Result;
        }

        var diagnostics = new DiagnosticBag();
        var graph = new PackageResolver(new OfflinePackageSource(), CacheRoot(), trusted: manifest.Trust.Binaries, writeLock: false)
            .Resolve(root, manifest.Dependencies, locked: false, diagnostics);
        var result = graph is null
            ? new EditorPackageResult(new PackageContext(PackageGraph.Empty, manifest.Dependencies) { Unresolved = true }, diagnostics.Items)
            : new EditorPackageResult(new PackageContext(graph, manifest.Dependencies), []);
        lock (_lock) _results[root] = new Entry(stamps.Manifest, stamps.Lock, Environment.TickCount64, graph is null, result);
        return result;
    }
}
