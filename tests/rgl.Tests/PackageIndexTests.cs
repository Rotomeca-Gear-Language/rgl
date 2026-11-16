using System.Net;
using System.Text.Json;
using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Project.Packages;
using Rgl.Tests.Runtime;
using Xunit;

namespace Rgl.Tests;

/// <summary>Un index figé : il rend ce qu'on lui a donné, ou échoue comme un réseau coupé.</summary>
internal sealed class FakeIndexSource(string? json) : IIndexSource
{
    public string? Json { get; set; } = json;

    public int Requests { get; private set; }

    public IndexFetch Fetch()
    {
        Requests++;
        return Json is null ? new IndexFetch(null, "réseau coupé") : new IndexFetch(Json, null);
    }
}

/// <summary>Des gists figés, par identifiant et révision.</summary>
internal sealed class FakeGistSource : IGistSource
{
    private readonly Dictionary<string, Dictionary<string, string>> _gists = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public void Publish(string id, string revision, Dictionary<string, string> files) => _gists[$"{id}@{revision}"] = files;

    public GistFetch Fetch(string id, string revision)
    {
        Requests.Add($"{id}@{revision}");
        return _gists.TryGetValue($"{id}@{revision}", out var files) ? new GistFetch(files, null) : new GistFetch(null, "ce gist ou cette révision n'existe pas");
    }
}

/// <summary>
/// L'index de découverte (slice V1.3E) : <c>rgl search</c>, <c>rgl add nom-court</c>, <c>rgl run script</c> et <c>rgl index build</c>. Les paquets, les gists et l'index
/// viennent de sources figées : aucun test n'ouvre Internet.
/// </summary>
public class PackageIndexTests
{
    private const string Json = "github.com/alice/json";
    private const string Geometry = "gitlab.com/bob/geometry";
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private const string Gist = "abcdef0123456789abcdef0123456789";

    private static readonly string _index = $$"""
        {
          "format": "rgl-index",
          "version": 1,
          "packages": [
            {"name": "json", "source": "{{Json}}", "description": "Lit et écrit du JSON", "license": "MIT", "version": "1.2.0", "keywords": ["parse", "serialize"]},
            {"name": "geometry", "source": "{{Geometry}}", "description": "Formes et distances"}
          ],
          "scripts": [
            {"name": "hello", "gist": "{{Gist}}", "revision": "{{Revision}}", "description": "Dit bonjour"}
          ]
        }
        """;

    private static (int Exit, string Out, string Error) Run(TempDirectory directory, TempDirectory cache, FakeIndexSource? index, FakePackageSource? packages, FakeGistSource? gists, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliRunner.Run(args, new CliContext(output, error, directory.Path, cache.Path, Packages: packages, Index: index, Gists: gists));
        return (exit, output.ToString(), error.ToString());
    }

    // ---- le format ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Un_index_valide_se_lit_et_s_ecrit_pareil_trie_par_nom()
    {
        var diagnostics = new DiagnosticBag();

        var index = PackageIndex.Parse(_index, "test", diagnostics);

        Assert.NotNull(index);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(["json", "geometry"], index.Packages.Select(p => p.Name));
        Assert.Equal(["parse", "serialize"], index.FindPackage("json")!.Keywords);
        Assert.Equal(Revision, index.FindScript("hello")!.Revision);
        Assert.Equal(Json, index.FindSource("GitHub.com/Alice/Json")!.Source);

        var text = index.ToText();
        Assert.True(text.IndexOf("\"geometry\"", StringComparison.Ordinal) < text.IndexOf("\"json\"", StringComparison.Ordinal));
        Assert.EndsWith("}\n", text);
        Assert.Equal(text, PackageIndex.Parse(text, "test", new DiagnosticBag())!.ToText());
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("[]")]
    [InlineData("{\"format\": \"autre\", \"version\": 1}")]
    [InlineData("{\"format\": \"rgl-index\", \"version\": 2}")]
    [InlineData("{\"format\": \"rgl-index\", \"version\": 1, \"packages\": [{\"name\": \"if x\", \"source\": \"github.com/a/b\"}]}")]
    [InlineData("{\"format\": \"rgl-index\", \"version\": 1, \"packages\": [{\"name\": \"j\", \"source\": \"example.com/a/b\"}]}")]
    [InlineData("{\"format\": \"rgl-index\", \"version\": 1, \"packages\": [{\"name\": \"j\", \"source\": \"github.com/a/b\"}, {\"name\": \"j\", \"source\": \"github.com/a/c\"}]}")]
    [InlineData("{\"format\": \"rgl-index\", \"version\": 1, \"packages\": [{\"name\": \"j\", \"source\": \"github.com/a/b\", \"version\": \"^1\"}]}")]
    [InlineData("{\"format\": \"rgl-index\", \"version\": 1, \"scripts\": [{\"name\": \"s\", \"gist\": \"zz\", \"revision\": \"" + Revision + "\"}]}")]
    [InlineData("{\"format\": \"rgl-index\", \"version\": 1, \"scripts\": [{\"name\": \"s\", \"gist\": \"" + Gist + "\", \"revision\": \"abc\"}]}")]
    public void Un_index_invalide_est_E162(string text)
    {
        var diagnostics = new DiagnosticBag();

        Assert.Null(PackageIndex.Parse(text, "test", diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.InvalidIndex);
    }

    // ---- search et le cache de l'index ---------------------------------------------------------------------------------------------

    [Fact]
    public void Search_cherche_dans_le_nom_la_description_les_mots_cles_et_l_adresse()
    {
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();
        var index = new FakeIndexSource(_index);

        var byName = Run(directory, cache, index, null, null, "search", "jso");
        Assert.Equal(0, byName.Exit);
        Assert.Contains("json", byName.Out);
        Assert.Contains(Json, byName.Out);
        Assert.Contains("Lit et écrit du JSON", byName.Out);
        Assert.DoesNotContain("geometry", byName.Out);
        Assert.Contains("rgl add", byName.Out);

        Assert.Contains("json", Run(directory, cache, index, null, null, "search", "SERIALIZE").Out);
        Assert.Contains("geometry", Run(directory, cache, index, null, null, "search", "distances").Out);
        Assert.Contains("geometry", Run(directory, cache, index, null, null, "search", "bob").Out);
    }

    [Fact]
    public void Search_sans_terme_liste_tout_scripts_compris_et_dit_quand_rien_ne_correspond()
    {
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();
        var index = new FakeIndexSource(_index);

        var all = Run(directory, cache, index, null, null, "search");
        Assert.Contains("json", all.Out);
        Assert.Contains("geometry", all.Out);
        Assert.Contains("hello", all.Out);
        Assert.Contains("rgl run", all.Out);

        var scriptOnly = Run(directory, cache, index, null, null, "search", "bonjour");
        Assert.Contains("hello", scriptOnly.Out);
        Assert.DoesNotContain("rgl add", scriptOnly.Out);

        Assert.Contains("Rien ne correspond", Run(directory, cache, index, null, null, "search", "zzz").Out);
    }

    [Fact]
    public void L_index_lu_est_garde_et_sert_hors_ligne_et_sans_copie_c_est_E162()
    {
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();
        var index = new FakeIndexSource(_index);
        Assert.Equal(0, Run(directory, cache, index, null, null, "search").Exit);
        Assert.True(File.Exists(Path.Combine(cache.Path, "index", "index.json")));

        index.Json = null;
        var offline = Run(directory, cache, index, null, null, "search", "json");
        Assert.Equal(0, offline.Exit);
        Assert.Contains("json", offline.Out);
        Assert.Contains("hors ligne", offline.Error);

        using var fresh = new TempDirectory();
        var none = Run(directory, fresh, index, null, null, "search");
        Assert.Equal(CliRunner.Failure, none.Exit);
        Assert.Contains("erreur[E162]", none.Error);

        var broken = Run(directory, fresh, new FakeIndexSource("pas du json"), null, null, "search");
        Assert.Contains("erreur[E162]", broken.Error);
    }

    // ---- rgl add nom-court -------------------------------------------------------------------------------------------------------------------

    private static TempDirectory Program()
    {
        var project = new TempDirectory();
        project.File("rgl.json", "{\"name\": \"app\", \"version\": \"1.0.0\", \"rgl\": \"0\"}");
        project.File("main.rgl", "train |> done;\n");
        return project;
    }

    [Fact]
    public void Add_d_un_nom_court_prend_l_adresse_dans_l_index_et_ecrit_l_adresse_au_manifeste()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var packages = new FakePackageSource();
        packages.Publish(Json, "1.2.0", "json");
        packages.Publish(Json, "1.0.0", "json");

        var added = Run(project, cache, new FakeIndexSource(_index), packages, null, "add", "json");

        Assert.True(added.Exit == 0, added.Error);
        Assert.Contains($"est {Json} dans l'index", added.Out);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(project.Path, "rgl.json")))!;
        Assert.Equal("1.2.0", manifest["dependencies"].GetProperty(Json).GetString());

        using var withVersion = Program();
        Assert.Equal(0, Run(withVersion, cache, new FakeIndexSource(_index), packages, null, "add", "json@1.0.0").Exit);
        Assert.Equal("1.0.0", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(withVersion.Path, "rgl.json")))!["dependencies"].GetProperty(Json).GetString());
    }

    [Fact]
    public void Add_d_un_nom_court_inconnu_est_E163_avec_les_noms_proches()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var before = File.ReadAllText(Path.Combine(project.Path, "rgl.json"));

        var unknown = Run(project, cache, new FakeIndexSource(_index), new FakePackageSource(), null, "add", "geo");
        Assert.Equal(CliRunner.Failure, unknown.Exit);
        Assert.Contains("erreur[E163]", unknown.Error);

        var absent = Run(project, cache, new FakeIndexSource(_index), new FakePackageSource(), null, "add", "inconnu");
        Assert.Contains("erreur[E163]", absent.Error);
        Assert.Equal(before, File.ReadAllText(Path.Combine(project.Path, "rgl.json")));
    }

    // ---- les sources HTTP -------------------------------------------------------------------------------------------------------------------

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void La_source_http_de_l_index_lit_l_adresse_donnee_ou_celle_par_defaut()
    {
        var handler = new Handler(HttpStatusCode.OK, _index);

        var fetched = new HttpIndexSource(handler, "https://exemple.test/index.json").Fetch();

        Assert.Equal(_index, fetched.Json);
        Assert.Equal(["https://exemple.test/index.json"], handler.Urls);
        Assert.StartsWith("https://", HttpIndexSource.DefaultUrl);

        Assert.Null(new HttpIndexSource(new Handler(HttpStatusCode.NotFound, ""), "https://exemple.test/x").Fetch().Json);
    }

    [Fact]
    public void La_source_http_des_gists_lit_les_fichiers_d_une_revision_et_refuse_ce_qui_est_tronque_ou_absent()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"files\": {\"main.rgl\": {\"content\": \"train |> done;\", \"truncated\": false}, \"Outils.forge\": {\"content\": \"x\"}}}");

        var fetched = new HttpGistSource(handler).Fetch(Gist, Revision);

        Assert.Equal(["Outils.forge", "main.rgl"], fetched.Files!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("train |> done;", fetched.Files["main.rgl"]);
        Assert.Equal([HttpGistSource.Url(Gist, Revision)], handler.Urls);
        Assert.Equal($"https://api.github.com/gists/{Gist}/{Revision}", HttpGistSource.Url(Gist, Revision));

        Assert.Null(new HttpGistSource(new Handler(HttpStatusCode.OK, "{\"files\": {\"main.rgl\": {\"content\": \"\", \"truncated\": true}}}")).Fetch(Gist, Revision).Files);
        Assert.Null(new HttpGistSource(new Handler(HttpStatusCode.NotFound, "")).Fetch(Gist, Revision).Files);
        Assert.Null(new HttpGistSource(new Handler(HttpStatusCode.OK, "pas du json")).Fetch(Gist, Revision).Files);
    }

    // ---- un script, ce qu'il peut être ----------------------------------------------------------------------------------------------------

    private static PackageIndex Index() => PackageIndex.Parse(_index, "test", new DiagnosticBag())!;

    private static (bool Ok, DiagnosticBag Diagnostics) Prepare(Dictionary<string, string> files, string directory)
    {
        var diagnostics = new DiagnosticBag();
        return (IndexCommands.PrepareScript(files, "hello", Index(), directory, diagnostics), diagnostics);
    }

    [Fact]
    public void Un_script_valide_devient_un_projet_dont_le_manifeste_est_celui_du_compilateur()
    {
        using var directory = new TempDirectory();
        var target = Path.Combine(directory.Path, "script");

        var (ok, diagnostics) = Prepare(new()
        {
            ["main.rgl"] = "import Outils;\ntrain |> done;\n",
            ["Outils.forge"] = "forge: gears\ndescription: 'o'\n",
            ["rgl.json"] = $"{{\"name\": \"autre\", \"no-cpp\": false, \"dependencies\": {{\"{Json}\": \"1.2.0\"}}}}",
        }, target);

        Assert.True(ok, string.Join('\n', diagnostics.Items.Select(d => d.Message)));
        Assert.True(File.Exists(Path.Combine(target, "main.rgl")));
        Assert.True(File.Exists(Path.Combine(target, "Outils.forge")));
        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(target, "rgl.json")))!;
        // Le manifeste du gist n'est jamais repris tel quel : le script ne s'autorise ni C++ ni @trusted.
        Assert.Equal("hello", manifest["name"].GetString());
        Assert.True(manifest["no-cpp"].GetBoolean());
        Assert.True(manifest["no-trust"].GetBoolean());
        Assert.Equal("1.2.0", manifest["dependencies"].GetProperty(Json).GetString());
    }

    [Theory]
    [InlineData("deux .rgl")]
    [InlineData("aucun .rgl")]
    [InlineData("fichier étranger")]
    [InlineData("nom invalide")]
    [InlineData("dépendance hors index")]
    [InlineData("package-trust")]
    [InlineData("rgl.json invalide")]
    [InlineData("trop de fichiers")]
    public void Un_script_refuse_ce_qu_un_script_ne_peut_pas_etre_E164(string reason)
    {
        using var directory = new TempDirectory();
        var files = new Dictionary<string, string> { ["main.rgl"] = "train |> done;\n" };
        switch (reason)
        {
            case "deux .rgl": files["autre.rgl"] = "train |> done;\n"; break;
            case "aucun .rgl": files.Clear(); files["Outils.forge"] = "forge: gears\ndescription: 'o'\n"; break;
            case "fichier étranger": files["install.sh"] = "rm -rf /"; break;
            case "nom invalide": files["a b.forge"] = "forge: gears\ndescription: 'o'\n"; break;
            case "dépendance hors index": files["rgl.json"] = "{\"dependencies\": {\"github.com/mallory/evil\": \"1.0.0\"}}"; break;
            case "package-trust": files["rgl.json"] = $"{{\"package-trust\": {{\"cpp\": [\"{Json}\"]}}}}"; break;
            case "rgl.json invalide": files["rgl.json"] = "pas du json"; break;
            case "trop de fichiers": for (var i = 0; i < 21; i++) files[$"f{i}.forge"] = "forge: gears\ndescription: 'o'\n"; break;
        }

        var (ok, diagnostics) = Prepare(files, Path.Combine(directory.Path, "script"));

        Assert.False(ok);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.InvalidScript);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "script")));
    }

    // ---- rgl run -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Run_d_un_nom_qui_n_est_pas_un_script_de_l_index_est_E164()
    {
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();

        var result = Run(directory, cache, new FakeIndexSource(_index), null, new FakeGistSource(), "run", "absent");

        Assert.Equal(CliRunner.Failure, result.Exit);
        Assert.Contains("erreur[E164]", result.Error);
        Assert.Contains("rgl search", result.Error);
    }

    [Fact]
    public void Run_d_une_revision_introuvable_est_E164_et_un_script_avec_du_cpp_est_refuse_a_la_compilation()
    {
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();

        var missing = Run(directory, cache, new FakeIndexSource(_index), null, new FakeGistSource(), "run", "hello");
        Assert.Equal(CliRunner.Failure, missing.Exit);
        Assert.Contains("erreur[E164]", missing.Error);

        // Un bloc cpp : le manifeste écrit par le compilateur interdit le C++ (no-cpp), quoi que dise le gist.
        var gists = new FakeGistSource();
        gists.Publish(Gist, Revision, new()
        {
            ["main.rgl"] = "unsafe gear f(): int => cpp { return 1; }();\ntrain |> done;\n",
            ["rgl.json"] = "{\"no-cpp\": false}",
        });
        var refused = Run(directory, cache, new FakeIndexSource(_index), null, gists, "run", "hello");
        Assert.Equal(CliRunner.Failure, refused.Exit);
        Assert.Contains("erreur[", refused.Error);
    }

    private static readonly string _sharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    [ZigFact]
    public void Run_compile_et_execute_un_script_de_l_index_et_ne_relit_pas_une_revision_deja_ecrite()
    {
        using var directory = new TempDirectory();
        var gists = new FakeGistSource();
        gists.Publish(Gist, Revision, new() { ["main.rgl"] = "train |> done;\n" });
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CliContext(output, error, directory.Path, _sharedCache, Toolchain: ZigToolchain.Locate(_sharedCache), Index: new FakeIndexSource(_index), Gists: gists);

        // Le cache est partagé entre les exécutions des tests : une révision écrite par une exécution précédente ne doit pas cacher la première lecture.
        var written = Path.Combine(_sharedCache, "gists", Gist);
        if (Directory.Exists(written)) Directory.Delete(written, recursive: true);

        var first = CliRunner.Run(["run", "hello"], context);
        var second = CliRunner.Run(["run", "hello"], context);

        Assert.True(first == 0, error.ToString());
        Assert.Equal(0, second);
        Assert.Equal([$"{Gist}@{Revision}"], gists.Requests);
    }

    // ---- rgl index build ---------------------------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> Ops() => new()
    {
        ["index.forge"] = "forge: index\ndescription: 'i'\n\nimport Ops;\nexport {...Ops};\n",
        ["Ops.forge"] = "forge: gears\ndescription: 'o'\n\nexport gear pure twice(int n): int => n * 2;\n",
    };

    private static TempDirectory IndexRepository(string packageEntry = "", string scriptEntry = "")
    {
        var repository = new TempDirectory();
        repository.File("packages/json.json", packageEntry.Length > 0 ? packageEntry : $"{{\"source\": \"{Json}\", \"keywords\": [\"parse\"]}}");
        repository.File("scripts/hello.json", scriptEntry.Length > 0 ? scriptEntry : $"{{\"gist\": \"{Gist}\", \"revision\": \"{Revision}\", \"description\": \"Dit bonjour\"}}");
        return repository;
    }

    [Fact]
    public void Index_build_verifie_chaque_entree_et_ecrit_l_index()
    {
        using var repository = IndexRepository();
        using var cache = new TempDirectory();
        var packages = new FakePackageSource();
        packages.Publish(Json, "1.0.0", "json", files: Ops());
        packages.Publish(Json, "1.3.0", "json", files: Ops());
        var gists = new FakeGistSource();
        gists.Publish(Gist, Revision, new() { ["main.rgl"] = "import package json;\ngear f(): int => json.twice(1);\ntrain |> done;\n", ["rgl.json"] = $"{{\"dependencies\": {{\"{Json}\": \"1.3.0\"}}}}" });

        var built = Run(repository, cache, null, packages, gists, "index", "build");

        Assert.True(built.Exit == 0, built.Error);
        Assert.Contains("1 paquet(s), 1 script(s)", built.Out);
        var index = PackageIndex.Parse(File.ReadAllText(Path.Combine(repository.Path, "index.json")), "index.json", new DiagnosticBag())!;
        var package = Assert.Single(index.Packages);
        Assert.Equal(("json", Json, "1.3.0"), (package.Name, package.Source, package.Version));
        Assert.Equal(["parse"], package.Keywords);
        Assert.Equal("hello", Assert.Single(index.Scripts).Name);
        Assert.Equal("Dit bonjour", index.Scripts[0].Description);
    }

    [Fact]
    public void Index_build_ecrit_ou_il_est_demande_et_refuse_un_dossier_sans_entrees()
    {
        using var repository = IndexRepository();
        using var cache = new TempDirectory();
        var packages = new FakePackageSource();
        packages.Publish(Json, "1.0.0", "json", files: Ops());
        var gists = new FakeGistSource();
        gists.Publish(Gist, Revision, new() { ["main.rgl"] = "train |> done;\n" });

        var built = Run(repository, cache, null, packages, gists, "index", "build", "-o", "sortie/mon-index.json");

        Assert.True(built.Exit == 0, built.Error);
        Assert.True(File.Exists(Path.Combine(repository.Path, "sortie", "mon-index.json")));
        Assert.False(File.Exists(Path.Combine(repository.Path, "index.json")));

        using var empty = new TempDirectory();
        var none = Run(empty, cache, null, packages, gists, "index", "build");
        Assert.Equal(CliRunner.Failure, none.Exit);
        Assert.Contains("erreur[E162]", none.Error);
    }

    [Fact]
    public void Index_build_n_ecrit_rien_si_une_entree_est_mauvaise()
    {
        using var cache = new TempDirectory();
        var packages = new FakePackageSource();
        packages.Publish(Json, "1.0.0", "json", files: Ops());
        var gists = new FakeGistSource();
        gists.Publish(Gist, Revision, new() { ["main.rgl"] = "train |> done;\n" });

        // L'entrée ne s'appelle pas comme le manifeste du paquet.
        using var misnamed = IndexRepository();
        File.Move(Path.Combine(misnamed.Path, "packages", "json.json"), Path.Combine(misnamed.Path, "packages", "autre.json"));
        var name = Run(misnamed, cache, null, packages, gists, "index", "build");
        Assert.Equal(CliRunner.Failure, name.Exit);
        Assert.Contains("erreur[E162]", name.Error);
        Assert.False(File.Exists(Path.Combine(misnamed.Path, "index.json")));

        // Un dépôt sans version publiée.
        using var unpublished = IndexRepository("{\"source\": \"github.com/nobody/none\"}");
        File.Move(Path.Combine(unpublished.Path, "packages", "json.json"), Path.Combine(unpublished.Path, "packages", "none.json"));
        var versions = Run(unpublished, cache, null, packages, gists, "index", "build");
        Assert.Contains("erreur[E155]", versions.Error);

        // Une adresse invalide.
        using var invalid = IndexRepository("{\"source\": \"example.com/a/b\"}");
        Assert.Contains("erreur[E162]", Run(invalid, cache, null, packages, gists, "index", "build").Error);
        Assert.False(File.Exists(Path.Combine(invalid.Path, "index.json")));
    }

    [Fact]
    public void Index_build_refuse_un_paquet_qui_ne_compile_pas_et_un_script_dont_une_dependance_n_est_pas_dans_l_index()
    {
        using var cache = new TempDirectory();
        var gists = new FakeGistSource();
        gists.Publish(Gist, Revision, new() { ["main.rgl"] = "train |> done;\n" });

        var broken = new FakePackageSource();
        broken.Publish(Json, "1.0.0", "json", files: new()
        {
            ["index.forge"] = "forge: index\ndescription: 'i'\n\nimport Ops;\nexport {...Ops};\n",
            ["Ops.forge"] = "forge: gears\ndescription: 'o'\n\nexport gear pure twice(int n): int => n * manquant;\n",
        });
        using var badPackage = IndexRepository();
        var compile = Run(badPackage, cache, null, broken, gists, "index", "build");
        Assert.Equal(CliRunner.Failure, compile.Exit);
        Assert.Contains("erreur[", compile.Error);

        var good = new FakePackageSource();
        good.Publish(Json, "1.0.0", "json", files: Ops());
        var outside = new FakeGistSource();
        outside.Publish(Gist, Revision, new() { ["main.rgl"] = "train |> done;\n", ["rgl.json"] = "{\"dependencies\": {\"github.com/mallory/evil\": \"1.0.0\"}}" });
        using var badScript = IndexRepository();
        var dependency = Run(badScript, cache, null, good, outside, "index", "build");
        Assert.Equal(CliRunner.Failure, dependency.Exit);
        Assert.Contains("erreur[E164]", dependency.Error);
        Assert.False(File.Exists(Path.Combine(badScript.Path, "index.json")));

        var absent = new FakeGistSource();
        using var missingGist = IndexRepository();
        Assert.Contains("erreur[E164]", Run(missingGist, cache, null, good, absent, "index", "build").Error);
    }

    // ---- la ligne de commande ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("search", "a", "b")]
    [InlineData("search", "-x")]
    [InlineData("run")]
    [InlineData("run", "a", "b")]
    [InlineData("run", "--release")]
    [InlineData("index")]
    [InlineData("index", "tout")]
    [InlineData("index", "build", "a", "b")]
    [InlineData("index", "build", "-x")]
    public void Une_ligne_de_commande_de_l_index_invalide_est_une_erreur_d_usage(params string[] args)
    {
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();

        Assert.Equal(CliRunner.UsageError, Run(directory, cache, new FakeIndexSource(_index), null, null, args).Exit);
    }

    [Fact]
    public void Run_transmet_les_arguments_apres_les_deux_tirets()
    {
        var diagnostics = new DiagnosticBag();

        var line = CommandLineParser.Parse(["run", "hello", "--", "a", "-b"], diagnostics)!;

        Assert.Equal(CommandKind.Run, line.Command);
        Assert.Equal(["hello"], line.PackageArguments);
        Assert.Equal(["a", "-b"], line.ProgramArgs);
    }
}
