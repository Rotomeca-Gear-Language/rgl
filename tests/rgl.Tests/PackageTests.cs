using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Project.Packages;

namespace Rgl.Tests;

/// <summary>Une forge de paquets figée : les archives sont fabriquées en mémoire, rien ne passe par Internet.</summary>
internal sealed class FakePackageSource : IPackageSource
{
    private readonly Dictionary<string, byte[]> _archives = new(StringComparer.Ordinal);

    /// <summary>Les demandes reçues, <c>adresse@version</c>, dans l'ordre.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Vrai : une demande lève, comme si le réseau était coupé.</summary>
    public bool Offline { get; set; }

    /// <summary>Publie une version : un <c>rgl.json</c>, un <c>index.forge</c> et les fichiers donnés.</summary>
    public void Publish(string source, string version, string? name = null, Dictionary<string, string>? dependencies = null, Dictionary<string, string>? files = null)
    {
        var all = new Dictionary<string, string>
        {
            ["rgl.json"] = Manifest(name ?? source.Split('/')[^1], version, dependencies),
            [PackageManifest.IndexFile] = "forge: index\ndescription: 'x'\n",
        };
        foreach (var (path, content) in files ?? []) all[path] = content;
        _archives[$"{source}@{version}"] = Archive(all);
    }

    /// <summary>Publie des octets d'archive tels quels.</summary>
    public void PublishRaw(string source, string version, byte[] archive) => _archives[$"{source}@{version}"] = archive;

    /// <summary>Retire une version : elle devient introuvable.</summary>
    public void Remove(string source, string version) => _archives.Remove($"{source}@{version}");

    public PackageVersions Versions(PackageId id)
    {
        if (Offline) throw new InvalidOperationException("réseau interdit");
        var prefix = id.Source + "@";
        var versions = _archives.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k[prefix.Length..]).ToList();
        versions.Sort((a, b) => PackageVersion.Compare(b, a));
        return versions.Count == 0 ? new PackageVersions(null, "dépôt inconnu", NotFound: true) : new PackageVersions(versions, null);
    }

    public PackageFetch Fetch(PackageId id, string version)
    {
        Requests.Add($"{id.Source}@{version}");
        if (Offline) throw new InvalidOperationException("réseau interdit");
        return _archives.TryGetValue($"{id.Source}@{version}", out var bytes)
            ? new PackageFetch(new MemoryStream(bytes), null)
            : new PackageFetch(null, "le tag n'existe pas", NotFound: true);
    }

    /// <summary>Le texte d'un <c>rgl.json</c> de paquet.</summary>
    public static string Manifest(string name, string version, Dictionary<string, string>? dependencies = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = name, ["version"] = version, ["dependencies"] = dependencies ?? [] });

    /// <summary>Une archive <c>.tar.gz</c> comme celle d'une forge : un en-tête pax global puis un dossier racine unique.</summary>
    public static byte[] Archive(Dictionary<string, string> files, string root = "paquet-1.0.0", Action<TarWriter>? more = null)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "0123456789abcdef" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/"));
            foreach (var (path, content) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{root}/{path}") { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) });
            more?.Invoke(tar);
        }
        return output.ToArray();
    }
}

public class PackageTests
{
    private const string Json = "github.com/alice/json";
    private const string Geometry = "gitlab.com/bob/geometry";
    private const string Core = "codeberg.org/carol/core";

    private static (DiagnosticBag Diagnostics, PackageGraph? Graph) Resolve(TempDirectory project, TempDirectory cache, FakePackageSource source, Dictionary<string, string> dependencies, bool locked = false, PackageArchive.Limits? limits = null, params string[] trusted)
    {
        var diagnostics = new DiagnosticBag();
        var graph = new PackageResolver(source, cache.Path, limits, trusted.ToHashSet(StringComparer.Ordinal)).Resolve(project.Path, dependencies, locked, diagnostics);
        return (diagnostics, graph);
    }

    private static string[] Codes(DiagnosticBag diagnostics) => [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "")];

    private static string Messages(DiagnosticBag diagnostics) => string.Join("\n", diagnostics.Items.Select(d => d.Message));

    private static string LockPath(TempDirectory project) => Path.Combine(project.Path, PackageLock.FileName);

    // ---- Identité ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Une_adresse_de_paquet_s_ecrit_hote_compte_depot_en_minuscules()
    {
        var id = PackageId.Parse("GitHub.com/Alice/Json", out var error);
        Assert.Null(error);
        Assert.Equal("github.com/alice/json", id!.Source);
    }

    [Theory]
    [InlineData("https://github.com/alice/json", "sans schéma")]
    [InlineData("github.com/alice", "hôte/compte/dépôt")]
    [InlineData("example.com/alice/json", "pas pris en charge")]
    [InlineData("github.com/alice/json.git", ".git")]
    [InlineData("github.com/../json", "pas un nom")]
    [InlineData("github.com/al ice/json", "pas un nom")]
    public void Une_adresse_invalide_est_refusee_avec_la_raison(string text, string reason)
    {
        Assert.Null(PackageId.Parse(text, out var error));
        Assert.Contains(reason, error);
    }

    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("0.0.1", true)]
    [InlineData("1.2", false)]
    [InlineData("^1.2.0", false)]
    [InlineData("01.2.0", false)]
    [InlineData("1.2.0-beta", false)]
    public void Une_version_est_exacte(string version, bool valid) => Assert.Equal(valid, PackageVersion.IsValid(version));

    // ---- Manifestes --------------------------------------------------------------------------------------------------------------

    private static ProjectManifest? LoadProject(string dependencies, out DiagnosticBag diagnostics)
    {
        using var dir = new TempDirectory();
        var path = dir.File("rgl.json", $$"""{"name": "app", "version": "1.0.0", "rgl": "0"{{dependencies}}}""");
        diagnostics = new DiagnosticBag();
        return ProjectManifest.Load(path, diagnostics);
    }

    [Fact]
    public void Le_manifeste_du_projet_lit_ses_dependances_triees()
    {
        var manifest = LoadProject(""", "dependencies": {"gitlab.com/bob/geometry": "2.0.1", "github.com/alice/json": "1.2.0"}""", out var diagnostics);
        Assert.NotNull(manifest);
        Assert.Empty(Codes(diagnostics));
        Assert.Equal([Json, Geometry], manifest.Dependencies.Keys);
        Assert.Equal("1.2.0", manifest.Dependencies[Json]);
    }

    [Fact]
    public void Sans_dependances_le_manifeste_n_en_a_pas() => Assert.Empty(LoadProject("", out _)!.Dependencies);

    [Theory]
    [InlineData(""", "dependencies": {"github.com/alice/json": "^1.2.0"}""", "exactement")]
    [InlineData(""", "dependencies": {"github.com/alice/json": "1.2"}""", "exactement")]
    [InlineData(""", "dependencies": {"example.com/a/b": "1.0.0"}""", "pas pris en charge")]
    [InlineData(""", "dependencies": {"github.com/alice/json": 1}""", "exactement")]
    [InlineData(""", "dependencies": ["github.com/alice/json"]""", "est un objet")]
    [InlineData(""", "dependencies": {"github.com/alice/json": "1.0.0", "GitHub.com/Alice/Json": "1.0.0"}""", "deux fois")]
    public void Une_dependance_mal_ecrite_est_E157(string dependencies, string reason)
    {
        Assert.Null(LoadProject(dependencies, out var diagnostics));
        Assert.Equal([DiagnosticCodes.InvalidPackage], Codes(diagnostics).Distinct());
        Assert.Contains(reason, Messages(diagnostics));
    }

    [Fact]
    public void Le_manifeste_d_un_paquet_exige_un_nom_et_une_version_valides()
    {
        using var dir = new TempDirectory();
        foreach (var (text, reason) in new[]
        {
            ("""{"version": "1.0.0"}""", "« name » absente"),
            ("""{"name": "json"}""", "« version » absente"),
            ("""{"name": "json", "version": "1"}""", "trois nombres"),
            ("""{"name": "std", "version": "1.0.0"}""", "bibliothèque standard"),
            ("""{"name": "mon-json", "version": "1.0.0"}""", "identifiant"),
            ("""{"name": "if", "version": "1.0.0"}""", "mot réservé"),
            ("""{"name": "json", "version": "1.0.0", "dependencies": {"x": "1.0.0"}}""", "dependencies"),
        })
        {
            var diagnostics = new DiagnosticBag();
            Assert.Null(PackageManifest.Load(dir.File("rgl.json", text), diagnostics));
            Assert.Contains(reason, Messages(diagnostics));
        }
    }

    // ---- Archive et empreinte ----------------------------------------------------------------------------------------------------

    private static string? Extract(byte[] archive, string destination, PackageArchive.Limits? limits = null) => Extraction(archive, destination, limits).Error;

    private static PackageArchive.Result Extraction(byte[] archive, string destination, PackageArchive.Limits? limits = null, bool keepNative = false)
    {
        using var stream = new MemoryStream(archive);
        return PackageArchive.Extract(stream, destination, limits, keepNative);
    }

    [Fact]
    public void L_archive_est_extraite_sans_son_dossier_racine_et_sans_son_en_tete_pax()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "out");
        var problem = Extract(FakePackageSource.Archive(new() { ["a.txt"] = "A", ["sous/b.txt"] = "B" }), target);
        Assert.Null(problem);
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(target, "sous", "b.txt")));
        Assert.Equal(2, Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void Un_lien_dans_l_archive_est_refuse()
    {
        using var dir = new TempDirectory();
        var archive = FakePackageSource.Archive(new() { ["a.txt"] = "A" }, more: tar => tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "paquet-1.0.0/lien") { LinkName = "/etc/passwd" }));
        Assert.Contains("lien", Extract(archive, Path.Combine(dir.Path, "out")));
    }

    [Fact]
    public void Un_chemin_qui_sort_du_dossier_est_refuse()
    {
        using var dir = new TempDirectory();
        var archive = FakePackageSource.Archive(new() { ["../../evil.txt"] = "x" });
        Assert.Contains("sort du dossier", Extract(archive, Path.Combine(dir.Path, "out")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir.Path)!, "evil.txt")));
    }

    [Fact]
    public void Une_archive_sans_dossier_racine_unique_est_refusee()
    {
        using var dir = new TempDirectory();
        var archive = FakePackageSource.Archive(new() { ["a.txt"] = "A" }, more: tar => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "autre/b.txt") { DataStream = new MemoryStream([1]) }));
        Assert.Contains("racine unique", Extract(archive, Path.Combine(dir.Path, "out")));
    }

    [Fact]
    public void Les_plafonds_de_taille_et_de_fichiers_arretent_l_extraction()
    {
        using var dir = new TempDirectory();
        var archive = FakePackageSource.Archive(new() { ["a.txt"] = "A", ["b.txt"] = "B", ["c.txt"] = new string('x', 5000) });
        Assert.Contains("plus de 2 fichiers", Extract(archive, Path.Combine(dir.Path, "un"), new PackageArchive.Limits(2, 1 << 20)));
        Assert.Contains("décompressée", Extract(archive, Path.Combine(dir.Path, "deux"), new PackageArchive.Limits(10, 1024)));
    }

    [Fact]
    public void Une_archive_illisible_est_une_erreur_et_non_une_exception()
    {
        using var dir = new TempDirectory();
        Assert.Contains("illisible", Extract([1, 2, 3, 4, 5], Path.Combine(dir.Path, "out")));
    }

    [Fact]
    public void L_empreinte_ne_depend_que_du_contenu_et_des_chemins()
    {
        using var a = new TempDirectory();
        using var b = new TempDirectory();
        a.File("x.txt", "1");
        a.File("d/y.txt", "2");
        b.File("d/y.txt", "2");
        b.File("x.txt", "1");
        Assert.Equal(PackageHash.OfTree(a.Path), PackageHash.OfTree(b.Path));
        Assert.StartsWith("sha256:", PackageHash.OfTree(a.Path));

        b.File("x.txt", "1 ");
        Assert.NotEqual(PackageHash.OfTree(a.Path), PackageHash.OfTree(b.Path));

        using var c = new TempDirectory();
        c.File("z.txt", "1");
        c.File("d/y.txt", "2");
        Assert.NotEqual(PackageHash.OfTree(a.Path), PackageHash.OfTree(c.Path));
    }

    // ---- Résolution et lock ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Un_paquet_est_recupere_une_fois_puis_ecrit_au_lock()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Assert.Empty(Codes(diagnostics));
        Assert.Equal([$"{Json}@1.2.0"], source.Requests);
        var package = Assert.Single(graph!.Packages);
        Assert.Equal("json", package.Manifest.Name);
        Assert.True(File.Exists(Path.Combine(package.Directory, PackageManifest.IndexFile)));

        var text = File.ReadAllText(LockPath(project));
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
        var entry = new PackageLock(PackageLock.Read(LockPath(project), new DiagnosticBag())!.Packages)[Json]!;
        Assert.Equal("1.2.0", entry.Version);
        Assert.Equal("json", entry.Name);
        Assert.Equal(PackageHash.OfTree(package.Directory), entry.Hash);
    }

    [Fact]
    public void Avec_le_lock_et_le_cache_aucun_reseau_n_est_necessaire()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        Resolve(project, cache, source, new() { [Json] = "1.2.0" });
        var before = File.ReadAllText(LockPath(project));

        source.Offline = true;
        source.Requests.Clear();
        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Assert.Empty(Codes(diagnostics));
        Assert.NotNull(graph);
        Assert.Empty(source.Requests);
        Assert.Equal(before, File.ReadAllText(LockPath(project)));
    }

    [Fact]
    public void Les_dependances_de_dependances_sont_trouvees_dans_l_ordre_et_figees()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Core, "0.3.1", "core");
        source.Publish(Geometry, "2.0.1", "geometry", new() { [Core] = "0.3.1" });
        source.Publish(Json, "1.2.0", "json", new() { [Core] = "0.3.1" });

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.2.0", [Geometry] = "2.0.1" });

        Assert.Empty(Codes(diagnostics));
        // Chaque paquet vient après ceux qu'il demande ; core, demandé deux fois, n'est récupéré qu'une fois.
        Assert.Equal([Core, Json, Geometry], graph!.Packages.Select(p => p.Source));
        Assert.Equal(1, source.Requests.Count(r => r.StartsWith(Core)));
        var locked = PackageLock.Read(LockPath(project), new DiagnosticBag())!;
        Assert.Equal([Core], locked[Json]!.Dependencies);
        Assert.Equal([Core, Json, Geometry], locked.Packages.Select(p => p.Source));
    }

    [Fact]
    public void Deux_versions_d_un_meme_paquet_sont_un_conflit_E158_qui_dit_qui_demande_quoi()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Core, "0.3.1", "core");
        source.Publish(Core, "0.4.0", "core");
        source.Publish(Json, "1.2.0", "json", new() { [Core] = "0.4.0" });

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Core] = "0.3.1", [Json] = "1.2.0" });

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.PackageVersionConflict], Codes(diagnostics));
        var message = Messages(diagnostics);
        Assert.Contains("0.3.1 est demandée par le projet", message);
        Assert.Contains($"0.4.0 par {Json} 1.2.0", message);
        Assert.DoesNotContain($"{Core}@0.4.0", string.Join(",", source.Requests));
        Assert.False(File.Exists(LockPath(project)));
    }

    [Fact]
    public void Des_paquets_qui_se_demandent_les_uns_les_autres_sont_un_cycle_E159()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", new() { [Geometry] = "1.0.0" });
        source.Publish(Geometry, "1.0.0", "geometry", new() { [Json] = "1.0.0" });

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.0.0" });

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.PackageCycle], Codes(diagnostics));
        Assert.Contains($"{Json} → {Geometry} → {Json}", Messages(diagnostics));
    }

    [Fact]
    public void Un_paquet_ou_une_version_inconnus_sont_E155()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json");

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "9.9.9" });

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.PackageNotFound], Codes(diagnostics));
        Assert.Contains("introuvable", Messages(diagnostics));
    }

    [Fact]
    public void Un_contenu_qui_ne_correspond_pas_au_lock_est_E156_et_le_lock_reste()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        Resolve(project, cache, source, new() { [Json] = "1.2.0" });
        var lockText = File.ReadAllText(LockPath(project));

        // Le tag a été déplacé : même version, autre contenu ; et le cache est vide.
        source.Publish(Json, "1.2.0", "json", files: new() { ["evil.forge"] = "x" });
        Directory.Delete(Path.Combine(cache.Path, "packages"), recursive: true);
        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.PackageHashMismatch], Codes(diagnostics));
        Assert.Contains("déplacé", Messages(diagnostics));
        Assert.Equal(lockText, File.ReadAllText(LockPath(project)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(cache.Path, "packages"), "*", SearchOption.AllDirectories).Where(d => d.Contains(".tmp-")));
    }

    [Fact]
    public void Un_cache_abime_est_retelecharge_quand_le_lock_connait_le_bon_contenu()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        var (_, first) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });
        File.WriteAllText(Path.Combine(first!.Packages[0].Directory, PackageManifest.IndexFile), "abîmé");

        source.Requests.Clear();
        var (diagnostics, again) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Assert.Empty(Codes(diagnostics));
        Assert.NotNull(again);
        Assert.Single(source.Requests);
        Assert.Equal("forge: index\ndescription: 'x'\n", File.ReadAllText(Path.Combine(again.Packages[0].Directory, PackageManifest.IndexFile)));
    }

    [Fact]
    public void Sans_lock_un_cache_existant_n_est_pas_cru_et_est_retelecharge()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        var (_, first) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });
        File.WriteAllText(Path.Combine(first!.Packages[0].Directory, PackageManifest.IndexFile), "falsifié");
        File.Delete(LockPath(project));

        source.Requests.Clear();
        var (diagnostics, again) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Assert.Empty(Codes(diagnostics));
        Assert.Single(source.Requests);
        Assert.Equal(first.Packages[0].Hash, again!.Packages[0].Hash);
    }

    [Fact]
    public void Avec_locked_le_lock_doit_etre_complet_et_n_est_jamais_modifie()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        var dependencies = new Dictionary<string, string> { [Json] = "1.2.0" };

        var (withoutLock, none) = Resolve(project, cache, source, dependencies, locked: true);
        Assert.Null(none);
        Assert.Equal([DiagnosticCodes.InvalidPackage], Codes(withoutLock));
        Assert.False(File.Exists(LockPath(project)));

        Resolve(project, cache, source, dependencies);
        var text = File.ReadAllText(LockPath(project));
        var (ok, graph) = Resolve(project, cache, source, dependencies, locked: true);
        Assert.Empty(Codes(ok));
        Assert.NotNull(graph);
        Assert.Equal(text, File.ReadAllText(LockPath(project)));

        // Le manifeste monte la version : le lock n'est plus à jour.
        source.Publish(Json, "1.3.0", "json");
        var (stale, none2) = Resolve(project, cache, source, new() { [Json] = "1.3.0" }, locked: true);
        Assert.Null(none2);
        Assert.Equal([DiagnosticCodes.InvalidPackage], Codes(stale));
        Assert.Equal(text, File.ReadAllText(LockPath(project)));
    }

    [Fact]
    public void Un_lock_perime_est_reecrit_et_un_lock_sans_dependance_est_supprime()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        source.Publish(Json, "1.3.0", "json");
        Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Resolve(project, cache, source, new() { [Json] = "1.3.0" });
        Assert.Equal("1.3.0", PackageLock.Read(LockPath(project), new DiagnosticBag())![Json]!.Version);

        var (diagnostics, graph) = Resolve(project, cache, source, []);
        Assert.Empty(Codes(diagnostics));
        Assert.Empty(graph!.Packages);
        Assert.False(File.Exists(LockPath(project)));
    }

    [Fact]
    public void Un_lock_illisible_est_E157_et_n_est_pas_ecrase()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        File.WriteAllText(LockPath(project), "pas du json");

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.InvalidPackage], Codes(diagnostics));
        Assert.Equal("pas du json", File.ReadAllText(LockPath(project)));
        Assert.Empty(source.Requests);
    }

    [Fact]
    public void Un_paquet_mal_forme_est_E157()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();

        // Étiqueté 1.2.0 mais le manifeste dit 1.1.0.
        source.PublishRaw(Json, "1.2.0", FakePackageSource.Archive(new() { ["rgl.json"] = FakePackageSource.Manifest("json", "1.1.0"), [PackageManifest.IndexFile] = "x" }));
        // Sans index.forge.
        source.PublishRaw(Geometry, "1.0.0", FakePackageSource.Archive(new() { ["rgl.json"] = FakePackageSource.Manifest("geometry", "1.0.0") }));
        // Sans manifeste.
        source.PublishRaw(Core, "1.0.0", FakePackageSource.Archive(new() { [PackageManifest.IndexFile] = "x" }));

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.2.0", [Geometry] = "1.0.0", [Core] = "1.0.0" });

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.InvalidPackage, DiagnosticCodes.InvalidPackage, DiagnosticCodes.InvalidPackage], Codes(diagnostics));
        var messages = Messages(diagnostics);
        Assert.Contains("étiqueté v1.2.0 mais son manifeste dit la version 1.1.0", messages);
        Assert.Contains("pas de index.forge", messages);
        Assert.Contains("pas de rgl.json", messages);
        Assert.False(File.Exists(LockPath(project)));
    }

    [Fact]
    public void Une_archive_dangereuse_est_E157_et_ne_laisse_rien_dans_le_cache()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.PublishRaw(Json, "1.0.0", FakePackageSource.Archive(new() { ["../../evil"] = "x" }));

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.0.0" });

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.InvalidPackage], Codes(diagnostics));
        Assert.Contains("archive refusée", Messages(diagnostics));
        var left = Directory.EnumerateFileSystemEntries(Path.Combine(cache.Path, "packages"), "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(left, entry => File.Exists(entry) || entry.Contains(".tmp-"));
    }

    // ---- Fichiers qui n'ont rien à faire dans des sources ------------------------------------------------------------------------

    private static Dictionary<string, string> WithBinaries() => new()
    {
        ["index.forge"] = "forge: index",
        ["docs/logo.png"] = "png",
        ["outils/compile.exe"] = "exe",
        ["installeur.MSI"] = "msi",
        ["data.bin"] = "bin",
        ["release.zip"] = "zip",
        ["lib/x.dll"] = "dll",
        ["lib/libx.so"] = "so",
        ["lib/libx.so.1.2"] = "so",
        ["lib/libx.dylib"] = "dylib",
        ["lib/libx.a"] = "a",
        ["lib/x.lib"] = "lib",
        ["build/x.obj"] = "obj",
        ["build/x.o"] = "o",
        [".git/config"] = "git",
        [".github/workflows/ci.yml"] = "ci",
        ["node_modules/y/index.js"] = "js",
        ["out/debug/app"] = "out",
        ["src/bin/tool"] = "bin",
    };

    private static string[] Files(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    [Fact]
    public void Les_executables_archives_et_dossiers_de_build_ne_sont_pas_extraits_et_les_binaires_de_bibliotheques_non_plus()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "out");
        var result = Extraction(FakePackageSource.Archive(WithBinaries()), target);

        Assert.Null(result.Error);
        Assert.Equal(["docs/logo.png", "index.forge"], Files(target));
        Assert.Equal(1u, result.Removed[".exe"]);
        Assert.Equal(1u, result.Removed[".msi"]);
        Assert.Equal(1u, result.Removed[".bin"]);
        Assert.Equal(1u, result.Removed[".zip"]);
        Assert.Equal(1u, result.Removed[".dll"]);
        Assert.Equal(2u, result.Removed[".so"]);
        Assert.Equal(1u, result.Removed[".git/"]);
        Assert.Equal(1u, result.Removed["node_modules/"]);
        Assert.Equal(1u, result.Removed["bin/"]);
    }

    [Fact]
    public void Un_paquet_de_confiance_garde_ses_binaires_de_bibliotheques_mais_jamais_ses_executables()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "out");
        var result = Extraction(FakePackageSource.Archive(WithBinaries()), target, keepNative: true);

        Assert.Null(result.Error);
        Assert.Equal(["build/x.o", "build/x.obj", "docs/logo.png", "index.forge", "lib/libx.a", "lib/libx.dylib", "lib/libx.so", "lib/libx.so.1.2", "lib/x.dll", "lib/x.lib"],
            Files(target).Where(f => !f.StartsWith("build/") || f.EndsWith(".o") || f.EndsWith(".obj")));
        Assert.DoesNotContain(".dll", result.Removed.Keys);
        Assert.Contains(".exe", result.Removed.Keys);
        Assert.Contains(".zip", result.Removed.Keys);
        Assert.False(File.Exists(Path.Combine(target, ".git", "config")));
    }

    [Fact]
    public void Les_fichiers_ecartes_ne_changent_pas_l_empreinte_et_sont_signales_par_une_notice()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json", files: new() { ["outils/a.exe"] = "un", ["lib/x.dll"] = "dll" });

        var (diagnostics, graph) = Resolve(project, cache, source, new() { [Json] = "1.2.0" });

        Assert.Empty(Codes(diagnostics));
        var notice = Assert.Single(diagnostics.Items, d => d.Level == DiagnosticLevel.Info);
        Assert.Contains("2 fichier(s)", notice.Message);
        Assert.Contains("1 .dll", notice.Message);
        Assert.Contains("1 .exe", notice.Message);
        var lockText = File.ReadAllText(LockPath(project));

        // Le tag est republié avec un autre exécutable : seul ce qui est gardé compte, le lock reste valable.
        source.Publish(Json, "1.2.0", "json", files: new() { ["outils/a.exe"] = "autre contenu", ["lib/x.dll"] = "autre" });
        Directory.Delete(Path.Combine(cache.Path, "packages"), recursive: true);
        var (again, graph2) = Resolve(project, cache, source, new() { [Json] = "1.2.0" }, locked: true);

        Assert.Empty(Codes(again));
        Assert.Equal(graph!.Packages[0].Hash, graph2!.Packages[0].Hash);
        Assert.Equal(lockText, File.ReadAllText(LockPath(project)));
    }

    [Fact]
    public void Package_trust_garde_les_binaires_a_part_dans_le_cache_et_se_note_au_lock()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json", files: new() { ["lib/x.dll"] = "dll" });
        var dependencies = new Dictionary<string, string> { [Json] = "1.2.0" };

        var (_, plain) = Resolve(project, cache, source, dependencies);
        Assert.False(File.Exists(Path.Combine(plain!.Packages[0].Directory, "lib", "x.dll")));
        Assert.False(plain.Packages[0].Trusted);
        Assert.DoesNotContain("trusted", File.ReadAllText(LockPath(project)));

        // L'autorisation arrive : un autre dossier du cache, un autre contenu, le lock le dit.
        var (diagnostics, trusted) = Resolve(project, cache, source, dependencies, trusted: Json);
        Assert.Empty(Codes(diagnostics));
        var package = trusted!.Packages[0];
        Assert.True(package.Trusted);
        Assert.EndsWith("1.2.0+native", package.Directory.Replace('\\', '/'));
        Assert.True(File.Exists(Path.Combine(package.Directory, "lib", "x.dll")));
        Assert.NotEqual(plain.Packages[0].Hash, package.Hash);
        Assert.True(PackageLock.Read(LockPath(project), new DiagnosticBag())![Json]!.Trusted);

        // Et elle repart : on revient au contenu sans binaires.
        var (_, back) = Resolve(project, cache, source, dependencies);
        Assert.False(back!.Packages[0].Trusted);
        Assert.Equal(plain.Packages[0].Hash, back.Packages[0].Hash);
    }

    [Fact]
    public void Avec_locked_un_changement_de_package_trust_est_E157()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json", files: new() { ["lib/x.dll"] = "dll" });
        var dependencies = new Dictionary<string, string> { [Json] = "1.2.0" };
        Resolve(project, cache, source, dependencies);
        var text = File.ReadAllText(LockPath(project));

        var (diagnostics, graph) = Resolve(project, cache, source, dependencies, locked: true, trusted: Json);

        Assert.Null(graph);
        Assert.Equal([DiagnosticCodes.InvalidPackage], Codes(diagnostics));
        Assert.Contains("package-trust", Messages(diagnostics));
        Assert.Equal(text, File.ReadAllText(LockPath(project)));
    }

    [Fact]
    public void Package_trust_lit_ses_deux_listes_d_adresses_et_refuse_le_reste()
    {
        var manifest = LoadProject(""", "package-trust": {"binaries": ["GitHub.com/Alice/Json"], "cpp": ["github.com/alice/json", "gitlab.com/bob/geometry"]}""", out var diagnostics);
        Assert.NotNull(manifest);
        Assert.Empty(Codes(diagnostics));
        Assert.Equal([Json], manifest.Trust.Binaries);
        Assert.Equal([Json, Geometry], manifest.Trust.Cpp.Order());
        Assert.Same(PackageTrust.None, LoadProject("", out _)!.Trust);

        foreach (var (text, reason) in new[]
        {
            (""", "package-trust": ["github.com/alice/json"]""", "est un objet"),
            (""", "package-trust": {"binaries": "github.com/alice/json"}""", "tableau d'adresses"),
            (""", "package-trust": {"binaries": ["example.com/a/b"]}""", "pas pris en charge"),
            (""", "package-trust": {"natif": []}""", "inconnu"),
        })
        {
            Assert.Null(LoadProject(text, out var bad));
            Assert.Equal([DiagnosticCodes.InvalidPackage], Codes(bad).Distinct());
            Assert.Contains(reason, Messages(bad));
        }
    }

    [Fact]
    public void Cpp_trust_ne_connait_pas_les_paquets()
    {
        Assert.Null(LoadProject(""", "cpp-trust": ["package:github.com/alice/json"]""", out var diagnostics));
        Assert.Contains("cpp-trust", Messages(diagnostics));
    }

    [Fact]
    public void Compile_avec_package_trust_donne_le_binaire_au_paquet_de_confiance()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json", files: new() { ["lib/x.dll"] = "dll" });
        project.File("rgl.json", "{\"name\": \"app\", \"version\": \"1.0.0\", \"rgl\": \"0\", \"dependencies\": {\"" + Json + "\": \"1.2.0\"}, \"package-trust\": {\"binaries\": [\"" + Json + "\"]}}");
        project.File("main.rgl", "train |> done;\n");

        var (exit, error) = Compile(project, cache, source);

        Assert.True(exit == 0, error);
        Assert.Contains("\"trusted\": true", File.ReadAllText(LockPath(project)));
        Assert.True(File.Exists(Path.Combine(cache.Path, "packages", "github.com", "alice", "json", "1.2.0+native", "lib", "x.dll")));
    }

    // ---- Téléchargement ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void L_archive_d_une_version_s_adresse_selon_la_forge()
    {
        var version = "1.2.0";
        Assert.Equal("https://codeload.github.com/alice/json/tar.gz/refs/tags/v1.2.0", HttpPackageSource.ArchiveUrl(PackageId.Parse(Json, out _)!, version));
        Assert.Equal("https://gitlab.com/bob/geometry/-/archive/v1.2.0/geometry-v1.2.0.tar.gz", HttpPackageSource.ArchiveUrl(PackageId.Parse(Geometry, out _)!, version));
        Assert.Equal("https://codeberg.org/carol/core/archive/v1.2.0.tar.gz", HttpPackageSource.ArchiveUrl(PackageId.Parse(Core, out _)!, version));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return respond(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void La_source_http_rend_l_archive_et_distingue_l_absence_d_une_panne()
    {
        var archive = FakePackageSource.Archive(new() { ["a.txt"] = "A" });
        var id = PackageId.Parse(Json, out _)!;

        var good = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var fetched = new HttpPackageSource(good).Fetch(id, "1.0.0");
        Assert.NotNull(fetched.Archive);
        using (fetched.Archive) Assert.Equal(archive.Length, fetched.Archive.Length);
        Assert.Equal(["https://codeload.github.com/alice/json/tar.gz/refs/tags/v1.0.0"], good.Urls);

        var missing = new HttpPackageSource(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).Fetch(id, "1.0.0");
        Assert.Null(missing.Archive);
        Assert.True(missing.NotFound);

        var broken = new HttpPackageSource(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).Fetch(id, "1.0.0");
        Assert.Null(broken.Archive);
        Assert.False(broken.NotFound);
        Assert.Contains("500", broken.Error);

        var offline = new HttpPackageSource(new FakeHandler(_ => throw new HttpRequestException("pas de réseau"))).Fetch(id, "1.0.0");
        Assert.Null(offline.Archive);
        Assert.Contains("téléchargement impossible", offline.Error);
    }

    // ---- De bout en bout : rgl compile -------------------------------------------------------------------------------------------

    private static (int Exit, string Error) Compile(TempDirectory project, TempDirectory cache, FakePackageSource source, params string[] options)
    {
        var error = new StringWriter();
        var exit = CliRunner.Run(["compile", "-d", ".", "--emit-cpp", .. options], new CliContext(new StringWriter(), error, project.Path, cache.Path, Packages: source));
        return (exit, error.ToString());
    }

    private static void Write(TempDirectory project, string dependencies)
    {
        project.File("rgl.json", "{\"name\": \"app\", \"version\": \"1.0.0\", \"rgl\": \"0\", \"dependencies\": {" + dependencies + "}}");
        project.File("main.rgl", "train |> done;\n");
    }

    [Fact]
    public void Compile_recupere_les_paquets_et_ecrit_rgl_lock()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        Write(project, $"\"{Json}\": \"1.2.0\"");

        var (exit, error) = Compile(project, cache, source);

        Assert.True(exit == 0, error);
        Assert.Contains(Json, File.ReadAllText(LockPath(project)));
        Assert.Equal([$"{Json}@1.2.0"], source.Requests);

        // Au second passage, le cache et le lock suffisent.
        source.Offline = true;
        Assert.Equal(0, Compile(project, cache, source).Exit);
        Assert.Equal(0, Compile(project, cache, source, "--locked").Exit);
    }

    [Fact]
    public void Compile_locked_sans_lock_echoue_et_un_conflit_de_versions_aussi()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Core, "0.3.1", "core");
        source.Publish(Core, "0.4.0", "core");
        source.Publish(Json, "1.2.0", "json", new() { [Core] = "0.4.0" });
        Write(project, $"\"{Json}\": \"1.2.0\"");

        var (exit, error) = Compile(project, cache, source, "--locked");
        Assert.Equal(1, exit);
        Assert.Contains("E157", error);
        Assert.False(File.Exists(LockPath(project)));

        Write(project, $"\"{Core}\": \"0.3.1\", \"{Json}\": \"1.2.0\"");
        var (conflict, message) = Compile(project, cache, source);
        Assert.Equal(1, conflict);
        Assert.Contains("E158", message);
    }

    [Fact]
    public void Le_cache_compte_et_vide_les_paquets_comme_le_reste()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.2.0", "json");
        Write(project, $"\"{Json}\": \"1.2.0\"");
        Compile(project, cache, source);

        Assert.True(CacheCommand.Size(cache.Path) > 0);
        Assert.True(Directory.Exists(Path.Combine(cache.Path, "packages")));

        var cleared = CliRunner.Run(["cache", "clear"], new CliContext(new StringWriter(), new StringWriter(), project.Path, cache.Path));
        Assert.Equal(0, cleared);
        Assert.False(Directory.Exists(Path.Combine(cache.Path, "packages")));
        // Le lock, lui, est au projet : le build suivant retélécharge selon lui.
        Assert.True(File.Exists(LockPath(project)));
        Assert.Equal(0, Compile(project, cache, source, "--locked").Exit);
    }

    [Fact]
    public void L_option_locked_se_lit_pour_compile_et_exec()
    {
        var diagnostics = new DiagnosticBag();
        var line = CommandLineParser.Parse(["compile", "-d", ".", "--locked"], diagnostics);
        Assert.True(line!.Locked);
        Assert.False(CommandLineParser.Parse(["compile", "-d", "."], diagnostics)!.Locked);
    }
}
