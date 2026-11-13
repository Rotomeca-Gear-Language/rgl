using Rgl.Diagnostics;

namespace Rgl.Project.Packages;

/// <summary>Un paquet récupéré, vérifié et prêt à être lu.</summary>
/// <param name="Id">Son identité.</param>
/// <param name="Version">Sa version exacte.</param>
/// <param name="Manifest">Son <c>rgl.json</c>.</param>
/// <param name="Hash">L'empreinte de son contenu.</param>
/// <param name="Directory">Le dossier du cache qui contient ses fichiers (il porte son <c>index.forge</c>).</param>
/// <param name="Trusted">Vrai si <c>package-trust.binaries</c> autorise le paquet : ses binaires de bibliothèques ont été gardés.</param>
internal sealed record ResolvedPackage(PackageId Id, string Version, PackageManifest Manifest, string Hash, string Directory, bool Trusted = false)
{
    /// <summary>L'adresse du paquet.</summary>
    public string Source => Id.Source;

    /// <summary>Les adresses des paquets qu'il demande, triées.</summary>
    public IReadOnlyList<string> Dependencies => [.. Manifest.Dependencies.Keys];
}

/// <summary>Tous les paquets d'un programme, dépendances de dépendances comprises : un seul exemplaire par adresse.</summary>
internal sealed class PackageGraph
{
    /// <summary>Un programme sans paquet.</summary>
    public static readonly PackageGraph Empty = new([]);

    private readonly Dictionary<string, ResolvedPackage> _bySource;

    /// <param name="packages">Les paquets, chacun après ceux qu'il demande.</param>
    public PackageGraph(IReadOnlyList<ResolvedPackage> packages)
    {
        Packages = packages;
        _bySource = packages.ToDictionary(p => p.Source, StringComparer.Ordinal);
    }

    /// <summary>Les paquets, chacun après ceux qu'il demande.</summary>
    public IReadOnlyList<ResolvedPackage> Packages { get; }

    /// <summary>Le paquet d'adresse <paramref name="source"/>, ou <see langword="null"/>.</summary>
    public ResolvedPackage? this[string source] => _bySource.GetValueOrDefault(source);
}

/// <summary>
/// Trouve les paquets d'un projet : lit les <c>dependencies</c> du manifeste, puis celles de chaque paquet en profondeur, récupère ce que le cache
/// n'a pas, vérifie chaque contenu contre <c>rgl.lock</c> et réécrit le lock si le graphe a changé. Une seule version de chaque paquet : deux
/// demandes différentes sont une erreur (E158). Hors ligne, un paquet déjà en cache et au lock ne demande aucun réseau.
/// </summary>
internal sealed class PackageResolver
{
    private readonly IPackageSource _source;
    private readonly string _packagesRoot;
    private readonly PackageArchive.Limits _limits;
    private readonly IReadOnlySet<string> _trusted;

    /// <param name="source">D'où viennent les archives.</param>
    /// <param name="cacheRoot">Racine du cache de RGL : les paquets sont dans <c>packages/</c>.</param>
    /// <param name="limits">Plafonds de l'extraction ; ceux par défaut sans valeur.</param>
    /// <param name="trusted">Les adresses des paquets que <c>package-trust.binaries</c> autorise : ils gardent leurs binaires de bibliothèques ; aucun sans valeur.</param>
    public PackageResolver(IPackageSource source, string cacheRoot, PackageArchive.Limits? limits = null, IReadOnlySet<string>? trusted = null)
    {
        _source = source;
        _packagesRoot = Path.Combine(cacheRoot, "packages");
        _limits = limits ?? PackageArchive.Limits.Default;
        _trusted = trusted ?? new HashSet<string>();
    }

    /// <summary>
    /// Résout les paquets d'un projet. Rend un graphe vide sans dépendance ni lock ; <see langword="null"/> si une erreur a été signalée.
    /// </summary>
    /// <param name="project">Le projet.</param>
    /// <param name="cacheRoot">Racine du cache de RGL.</param>
    /// <param name="source">D'où viennent les archives ; Internet sans valeur.</param>
    /// <param name="locked">Vrai (<c>--locked</c>) : le lock doit déjà être complet et à jour, il n'est jamais modifié.</param>
    /// <param name="diagnostics">Reçoit les erreurs.</param>
    public static PackageGraph? ForProject(RglProject project, string cacheRoot, IPackageSource? source, bool locked, DiagnosticBag diagnostics)
    {
        var dependencies = project.Manifest?.Dependencies ?? new Dictionary<string, string>();
        if (dependencies.Count == 0 && !File.Exists(Path.Combine(project.RootDirectory, PackageLock.FileName))) return PackageGraph.Empty;
        return new PackageResolver(source ?? new HttpPackageSource(), cacheRoot, trusted: project.Manifest?.Trust.Binaries).Resolve(project.RootDirectory, dependencies, locked, diagnostics);
    }

    /// <summary>Résout les <paramref name="dependencies"/> du manifeste de <paramref name="projectRoot"/> ; voir <see cref="ForProject"/>.</summary>
    public PackageGraph? Resolve(string projectRoot, IReadOnlyDictionary<string, string> dependencies, bool locked, DiagnosticBag diagnostics)
    {
        var lockPath = Path.Combine(projectRoot, PackageLock.FileName);
        PackageLock? existing = null;
        if (File.Exists(lockPath))
        {
            existing = PackageLock.Read(lockPath, diagnostics);
            if (existing is null) return null;
        }

        var walk = new Walk(this, existing, locked, diagnostics);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.FileName);
        foreach (var (source, version) in dependencies) walk.Visit("le projet", manifestPath, source, version);
        if (walk.Failed) return null;

        var fresh = new PackageLock(walk.Order.Select(p => new LockedPackage(p.Source, p.Version, p.Manifest.Name, p.Hash, p.Dependencies, p.Trusted)));
        var current = existing?.ToText() ?? new PackageLock([]).ToText();
        if (locked)
        {
            if (current != fresh.ToText())
            {
                diagnostics.Error($"{PackageLock.FileName} n'est pas à jour avec les dépendances (--locked l'interdit de le modifier) : lance « rgl compile » sans --locked", lockPath, DiagnosticCodes.InvalidPackage);
                return null;
            }
        }
        else if (current != fresh.ToText())
        {
            try
            {
                if (fresh.Packages.Count == 0) File.Delete(lockPath);
                else fresh.Write(lockPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Error($"{PackageLock.FileName} ne peut pas être écrit : {e.Message}", lockPath, DiagnosticCodes.InvalidPackage);
                return null;
            }
        }
        return new PackageGraph(walk.Order);
    }

    /// <summary>
    /// Le dossier du cache d'une version d'un paquet. Un paquet de confiance garde des fichiers que les autres n'ont pas (ses binaires) : il a son
    /// propre dossier, <c>1.2.0+native</c>, pour qu'un changement de <c>package-trust</c> ne serve pas un contenu qui n'est pas le bon.
    /// </summary>
    internal string CacheDirectory(PackageId id, string version, bool trusted) =>
        Path.Combine(_packagesRoot, id.Host, id.Owner, id.Repository, trusted ? version + "+native" : version);

    /// <summary>Supprime un dossier du cache ; ce qui ne se supprime pas reste, sans faire échouer le build.</summary>
    private static void _Delete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Un fichier encore tenu ouvert : le cache le reprendra au prochain passage.
        }
    }

    /// <summary>Un passage de résolution : l'état d'un parcours des dépendances en profondeur.</summary>
    private sealed class Walk(PackageResolver resolver, PackageLock? existing, bool locked, DiagnosticBag diagnostics)
    {
        private readonly Dictionary<string, ResolvedPackage> _chosen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _requestedBy = new(StringComparer.Ordinal);
        private readonly HashSet<string> _conflicts = new(StringComparer.Ordinal);
        private readonly List<string> _stack = [];

        /// <summary>Les paquets trouvés, chacun après ceux qu'il demande.</summary>
        public List<ResolvedPackage> Order { get; } = [];

        /// <summary>Vrai si une erreur a été signalée pendant ce passage.</summary>
        public bool Failed { get; private set; }

        /// <summary>Signale une erreur et fait échouer la résolution.</summary>
        private void _Fail(string code, string message, string path)
        {
            Failed = true;
            diagnostics.Error(message, path, code);
        }

        /// <summary>Visite la demande de <paramref name="requester"/> pour <paramref name="source"/> en <paramref name="version"/>.</summary>
        /// <param name="requester">Qui demande, pour les messages : « le projet » ou <c>adresse version</c>.</param>
        /// <param name="requesterPath">Le manifeste de celui qui demande.</param>
        public void Visit(string requester, string requesterPath, string source, string version)
        {
            if (_chosen.TryGetValue(source, out var known))
            {
                if (known.Version != version)
                {
                    if (_conflicts.Add($"{source}@{version}"))
                        _Fail(DiagnosticCodes.PackageVersionConflict,
                            $"« {source} » : la version {known.Version} est demandée par {_requestedBy[source]}, la version {version} par {requester} ; une seule version d'un paquet peut vivre dans le programme (écris la même dans les deux manifestes)",
                            requesterPath);
                }
                else if (_stack.Contains(source))
                {
                    var from = _stack.IndexOf(source);
                    var cycle = string.Join(" → ", _stack.Skip(from).Append(source));
                    _Fail(DiagnosticCodes.PackageCycle, $"dépendance cyclique entre paquets : {cycle}", requesterPath);
                }
                return;
            }

            var id = PackageId.Parse(source, out _)!;
            var entry = existing?[source];
            if (locked && (entry is null || entry.Version != version))
            {
                _Fail(DiagnosticCodes.InvalidPackage,
                    $"{PackageLock.FileName} ne contient pas « {source} » en version {version} ; --locked interdit de le modifier (lance « rgl compile » sans --locked)", requesterPath);
                return;
            }

            var trusted = resolver._trusted.Contains(source);
            if (locked && entry!.Trusted != trusted)
            {
                _Fail(DiagnosticCodes.InvalidPackage,
                    $"{PackageLock.FileName} a été écrit {(entry.Trusted ? "avec" : "sans")} l'autorisation package-trust (binaries) de « {source} » ; --locked interdit de le modifier (lance « rgl compile » sans --locked)", requesterPath);
                return;
            }

            var directory = resolver.CacheDirectory(id, version, trusted);
            // Le lock ne connaît l'empreinte que du contenu qu'il a vu : une autre autorisation donne un autre contenu, donc on le reprend.
            var expected = entry is not null && entry.Version == version && entry.Trusted == trusted ? entry.Hash : null;
            if (_Ensure(id, version, directory, expected, trusted, requesterPath) is not { } hash) return;

            var label = $"{source} {version}";
            var manifest = PackageManifest.Load(Path.Combine(directory, ProjectManifest.FileName), diagnostics, label);
            if (manifest is null)
            {
                Failed = true;
                return;
            }
            if (manifest.Version != version)
            {
                _Fail(DiagnosticCodes.InvalidPackage,
                    $"paquet « {label} » : le dépôt est étiqueté {PackageVersion.Tag(version)} mais son manifeste dit la version {manifest.Version}", Path.Combine(directory, ProjectManifest.FileName));
                return;
            }
            if (!File.Exists(Path.Combine(directory, PackageManifest.IndexFile)))
            {
                _Fail(DiagnosticCodes.InvalidPackage,
                    $"paquet « {label} » : pas de {PackageManifest.IndexFile} à la racine du dépôt (c'est lui qui dit ce que le paquet rend visible)", directory);
                return;
            }

            var package = new ResolvedPackage(id, version, manifest, hash, directory, trusted);
            _chosen[source] = package;
            _requestedBy[source] = requester;
            _stack.Add(source);
            foreach (var (dependency, dependencyVersion) in manifest.Dependencies)
                Visit(label, Path.Combine(directory, ProjectManifest.FileName), dependency, dependencyVersion);
            _stack.RemoveAt(_stack.Count - 1);
            Order.Add(package);
        }

        /// <summary>
        /// Met le contenu du paquet dans le cache et rend son empreinte. Un dossier du cache qui n'a plus l'empreinte du lock est jeté et
        /// retéléchargé ; un contenu téléchargé qui ne l'a pas est E156.
        /// </summary>
        private string? _Ensure(PackageId id, string version, string directory, string? expected, bool trusted, string requesterPath)
        {
            if (System.IO.Directory.Exists(directory))
            {
                // Sans empreinte au lock, rien ne dit que le dossier est celui du tag (un cache falsifié serait inscrit au lock) : on le jette et on retélécharge.
                if (expected is not null)
                {
                    var cached = PackageHash.OfTree(directory);
                    if (cached == expected) return cached;
                }
                _Delete(directory);
            }

            var fetched = resolver._source.Fetch(id, version);
            if (fetched.Archive is null)
            {
                _Fail(DiagnosticCodes.PackageNotFound,
                    fetched.NotFound ? $"paquet « {id.Source} » {version} introuvable : {fetched.Error}" : $"paquet « {id.Source} » {version} : {fetched.Error}", requesterPath);
                return null;
            }

            var temp = directory + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (fetched.Archive)
                {
                    var extraction = PackageArchive.Extract(fetched.Archive, temp, resolver._limits, trusted);
                    if (extraction.Error is { } problem)
                    {
                        _Fail(DiagnosticCodes.InvalidPackage, $"paquet « {id.Source} » {version} : archive refusée : {problem}", requesterPath);
                        return null;
                    }
                    if (extraction.Removed.Count > 0)
                    {
                        var total = extraction.Removed.Values.Aggregate(0u, (sum, count) => sum + count);
                        var detail = string.Join(", ", extraction.Removed.Select(r => $"{r.Value} {r.Key}"));
                        diagnostics.Info($"paquet « {id.Source} » {version} : {total} fichier(s) qui n'ont rien à faire dans des sources n'ont pas été extraits ({detail})", requesterPath);
                    }
                }

                var hash = PackageHash.OfTree(temp);
                if (expected is not null && hash != expected)
                {
                    _Fail(DiagnosticCodes.PackageHashMismatch,
                        $"paquet « {id.Source} » {version} : le contenu ne correspond pas à {PackageLock.FileName} (attendu {expected}, reçu {hash}) ; le tag {PackageVersion.Tag(version)} a peut-être été déplacé", requesterPath);
                    return null;
                }

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                try
                {
                    System.IO.Directory.Move(temp, directory);
                }
                catch (IOException) when (System.IO.Directory.Exists(directory))
                {
                    // Un autre processus a rangé la même version entre-temps : on garde la sienne.
                    var other = PackageHash.OfTree(directory);
                    if (other != (expected ?? hash))
                    {
                        _Fail(DiagnosticCodes.PackageHashMismatch, $"paquet « {id.Source} » {version} : le contenu ne correspond pas à {PackageLock.FileName} (attendu {expected ?? hash}, reçu {other})", requesterPath);
                        return null;
                    }
                    return other;
                }
                return hash;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _Fail(DiagnosticCodes.InvalidPackage, $"paquet « {id.Source} » {version} : le cache ne peut pas être écrit : {e.Message}", requesterPath);
                return null;
            }
            finally
            {
                _Delete(temp);
            }
        }
    }
}
