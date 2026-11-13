using System.Text.Json;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Project.Packages;
using Xunit;

namespace Rgl.Tests;

/// <summary>Un <c>git</c> figé : il répond ce qu'on lui a dit et note ce qu'on lui demande.</summary>
internal sealed class FakeGit : IGitRunner
{
    public bool Inside { get; set; } = true;
    public string Status { get; set; } = "";
    public List<string> Tags { get; } = [];
    public string? Remote { get; set; } = "https://github.com/alice/json.git";
    public bool PushFails { get; set; }
    public bool Missing { get; set; }

    /// <summary>Les commandes reçues, une ligne chacune.</summary>
    public List<string> Commands { get; } = [];

    public (int Exit, string Output, string Error)? Run(string workingDirectory, params string[] arguments)
    {
        if (Missing) return null;
        Commands.Add(string.Join(' ', arguments));
        switch (arguments)
        {
            case ["rev-parse", "--is-inside-work-tree"]: return Inside ? (0, "true", "") : (128, "", "pas un dépôt");
            case ["status", "--porcelain"]: return (0, Status, "");
            case ["tag", "--list", var tag]: return (0, Tags.Contains(tag) ? tag : "", "");
            case ["remote", "get-url", "origin"]: return Remote is null ? (2, "", "pas de remote") : (0, Remote, "");
            case ["tag", "-d", var deleted]: Tags.Remove(deleted); return (0, "", "");
            case ["tag", var created]: Tags.Add(created); return (0, "", "");
            case ["push", "origin", _]: return PushFails ? (1, "", "refusé") : (0, "", "");
            default: return (1, "", "commande inconnue");
        }
    }
}

public class PackageCommandTests
{
    private const string Json = "github.com/alice/json";
    private const string Geometry = "gitlab.com/bob/geometry";

    private static (int Exit, string Out, string Error) Run(TempDirectory project, TempDirectory cache, FakePackageSource? source, FakeGit? git, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliRunner.Run(args, new CliContext(output, error, project.Path, cache.Path, Packages: source, Git: git));
        return (exit, output.ToString(), error.ToString());
    }

    private static TempDirectory Program(string dependencies = "", string extra = "")
    {
        var project = new TempDirectory();
        project.File("rgl.json", "{\n    \"name\": \"app\",\n    \"version\": \"1.0.0\",\n    \"rgl\": \"0\"" + (dependencies.Length > 0 ? $",\n    \"dependencies\": {{{dependencies}}}" : "") + extra + "\n}\n");
        project.File("main.rgl", "train |> done;\n");
        return project;
    }

    private static Dictionary<string, JsonElement> Manifest(TempDirectory project) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(project.Path, "rgl.json")))!;

    private static string[] DependencyNames(TempDirectory project) =>
        Manifest(project).TryGetValue("dependencies", out var d) ? [.. d.EnumerateObject().Select(p => p.Name)] : [];

    private static FakePackageSource Source()
    {
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json");
        source.Publish(Json, "1.2.0", "json");
        source.Publish(Json, "1.10.0", "json");
        source.Publish(Geometry, "2.0.0", "geometry");
        return source;
    }

    // ---- add / remove / restore --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Add_ecrit_le_manifeste_et_le_lock_avec_la_version_demandee_ou_la_derniere()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var source = Source();

        var exact = Run(project, cache, source, null, "add", $"{Json}@1.2.0");
        Assert.True(exact.Exit == 0, exact.Error);
        Assert.Contains($"Ajouté : {Json} 1.2.0", exact.Out);
        Assert.Equal("1.2.0", Manifest(project)["dependencies"].GetProperty(Json).GetString());
        Assert.Contains(Json, File.ReadAllText(Path.Combine(project.Path, "rgl.lock")));
        // Les autres clés du manifeste restent.
        Assert.Equal("app", Manifest(project)["name"].GetString());

        // Sans version : la plus récente, comparée nombre par nombre (1.10.0 est après 1.2.0).
        using var other = Program();
        var latest = Run(other, cache, source, null, "add", Geometry);
        Assert.True(latest.Exit == 0, latest.Error);
        Assert.Equal("2.0.0", Manifest(other)["dependencies"].GetProperty(Geometry).GetString());
        using var third = Program();
        Assert.Equal(0, Run(third, cache, source, null, "add", Json).Exit);
        Assert.Equal("1.10.0", Manifest(third)["dependencies"].GetProperty(Json).GetString());
    }

    [Fact]
    public void Add_d_un_paquet_deja_la_ne_change_rien_et_d_une_autre_version_la_remplace()
    {
        using var project = Program($"\"{Json}\": \"1.0.0\"");
        using var cache = new TempDirectory();
        var source = Source();

        var same = Run(project, cache, source, null, "add", $"{Json}@1.0.0");
        Assert.Equal(0, same.Exit);
        Assert.Contains("déjà une dépendance", same.Out);

        var replaced = Run(project, cache, source, null, "add", $"{Json}@1.2.0");
        Assert.Equal(0, replaced.Exit);
        Assert.Contains("1.0.0 → 1.2.0", replaced.Out);
        Assert.Equal("1.2.0", Manifest(project)["dependencies"].GetProperty(Json).GetString());
    }

    [Fact]
    public void Add_qui_echoue_laisse_le_manifeste_et_le_lock_intacts()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var source = Source();
        var manifest = File.ReadAllText(Path.Combine(project.Path, "rgl.json"));

        var unknown = Run(project, cache, source, null, "add", "github.com/nobody/none@1.0.0");
        Assert.Equal(CliRunner.Failure, unknown.Exit);
        Assert.Contains("erreur[E155]", unknown.Error);

        var badVersion = Run(project, cache, source, null, "add", $"{Json}@^1.0");
        Assert.Equal(CliRunner.Failure, badVersion.Exit);
        var badAddress = Run(project, cache, source, null, "add", "example.com/a/b");
        Assert.Equal(CliRunner.Failure, badAddress.Exit);

        Assert.Equal(manifest, File.ReadAllText(Path.Combine(project.Path, "rgl.json")));
        Assert.False(File.Exists(Path.Combine(project.Path, "rgl.lock")));
    }

    [Fact]
    public void Add_signale_le_cpp_d_un_paquet_et_dit_comment_l_autoriser()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: new() { ["normalise.rgl.cpp"] = "// c++\n", ["Norm.forge"] = "forge: gears\ndescription: 'n'\n" });

        var result = Run(project, cache, source, null, "add", $"{Json}@1.0.0");

        Assert.Equal(0, result.Exit);
        Assert.Contains("normalise.rgl.cpp", result.Out);
        Assert.Contains("package-trust.cpp", result.Out);
    }

    [Fact]
    public void Remove_retire_la_dependance_et_supprime_le_lock_quand_il_n_y_en_a_plus()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var source = Source();
        Run(project, cache, source, null, "add", $"{Json}@1.0.0");
        Run(project, cache, source, null, "add", $"{Geometry}@2.0.0");

        var removed = Run(project, cache, source, null, "remove", Json);
        Assert.Equal(0, removed.Exit);
        Assert.Equal([Geometry], DependencyNames(project));
        Assert.DoesNotContain(Json, File.ReadAllText(Path.Combine(project.Path, "rgl.lock")));

        Assert.Equal(0, Run(project, cache, source, null, "remove", Geometry).Exit);
        Assert.Empty(DependencyNames(project));
        Assert.False(File.Exists(Path.Combine(project.Path, "rgl.lock")));

        var absent = Run(project, cache, source, null, "remove", Json);
        Assert.Equal(CliRunner.Failure, absent.Exit);
        Assert.Contains("n'est pas une dépendance", absent.Error);
    }

    [Fact]
    public void Restore_retelecharge_les_paquets_selon_le_lock()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var source = Source();
        Run(project, cache, source, null, "add", $"{Json}@1.2.0");
        Directory.Delete(Path.Combine(cache.Path, "packages"), recursive: true);
        source.Requests.Clear();

        var restored = Run(project, cache, source, null, "restore");

        Assert.Equal(0, restored.Exit);
        Assert.Contains("1 paquet(s) prêt(s)", restored.Out);
        Assert.Equal([$"{Json}@1.2.0"], source.Requests);

        using var none = Program();
        Assert.Contains("Aucun paquet", Run(none, cache, source, null, "restore").Out);
    }

    // ---- tree / outdated / update --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tree_montre_le_graphe_et_ne_developpe_un_paquet_qu_une_fois()
    {
        using var project = Program($"\"{Json}\": \"1.0.0\", \"{Geometry}\": \"2.0.0\"");
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Geometry, "2.0.0", "geometry");
        source.Publish(Json, "1.0.0", "json", dependencies: new() { [Geometry] = "2.0.0" });

        var tree = Run(project, cache, source, null, "tree");

        Assert.True(tree.Exit == 0, tree.Error);
        var lines = tree.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("app 1.0.0", lines[0]);
        // Les adresses sont triées : json d'abord, qui développe geometry ; geometry, direct aussi, n'est pas redéveloppé.
        Assert.Equal($"|-- {Json} 1.0.0", lines[1]);
        Assert.Equal($"|   `-- {Geometry} 2.0.0", lines[2]);
        Assert.Equal($"`-- {Geometry} 2.0.0", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void Tree_d_un_conflit_de_versions_dit_qui_demande_quoi()
    {
        using var project = Program($"\"{Json}\": \"1.0.0\", \"{Geometry}\": \"2.0.0\"");
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Geometry, "2.0.0", "geometry");
        source.Publish(Geometry, "3.0.0", "geometry");
        source.Publish(Json, "1.0.0", "json", dependencies: new() { [Geometry] = "3.0.0" });

        var tree = Run(project, cache, source, null, "tree");

        Assert.Equal(CliRunner.Failure, tree.Exit);
        Assert.Contains("erreur[E158]", tree.Error);
    }

    [Fact]
    public void Outdated_liste_les_paquets_qui_ont_une_version_plus_recente()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var source = Source();
        Run(project, cache, source, null, "add", $"{Json}@1.2.0");
        Run(project, cache, source, null, "add", $"{Geometry}@2.0.0");

        var outdated = Run(project, cache, source, null, "outdated");

        Assert.Equal(0, outdated.Exit);
        Assert.Contains(Json, outdated.Out);
        Assert.Contains("1.2.0", outdated.Out);
        Assert.Contains("1.10.0", outdated.Out);
        Assert.DoesNotContain(Geometry, outdated.Out);

        Run(project, cache, source, null, "update");
        Assert.Contains("à jour", Run(project, cache, source, null, "outdated").Out);
    }

    [Fact]
    public void Update_monte_les_dependances_directes_ou_celle_qu_on_nomme_et_garde_le_reste_si_la_resolution_echoue()
    {
        using var project = Program();
        using var cache = new TempDirectory();
        var source = Source();
        Run(project, cache, source, null, "add", $"{Json}@1.0.0");
        source.Publish(Geometry, "1.0.0", "geometry");
        Run(project, cache, source, null, "add", $"{Geometry}@1.0.0");

        var one = Run(project, cache, source, null, "update", Json);
        Assert.Equal(0, one.Exit);
        Assert.Contains($"{Json} 1.0.0 → 1.10.0", one.Out);
        Assert.Equal("1.0.0", Manifest(project)["dependencies"].GetProperty(Geometry).GetString());

        var all = Run(project, cache, source, null, "update");
        Assert.Contains($"{Geometry} 1.0.0 → 2.0.0", all.Out);
        Assert.Equal("2.0.0", Manifest(project)["dependencies"].GetProperty(Geometry).GetString());

        Assert.Contains("Tout est à jour", Run(project, cache, source, null, "update").Out);
        var notDependency = Run(project, cache, source, null, "update", "github.com/nobody/none");
        Assert.Equal(CliRunner.Failure, notDependency.Exit);
    }

    // ---- usage -------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("tree", "extra")]
    [InlineData("add", "a", "b")]
    [InlineData("update", "--dry-run")]
    [InlineData("restore", "-x")]
    public void Une_ligne_de_commande_de_paquets_invalide_est_une_erreur_d_usage(params string[] args)
    {
        using var project = Program();
        using var cache = new TempDirectory();

        var result = Run(project, cache, Source(), null, args);

        Assert.Equal(CliRunner.UsageError, result.Exit);
    }

    [Fact]
    public void Sans_manifeste_la_commande_le_dit()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();

        var result = Run(project, cache, Source(), null, "restore");

        Assert.Equal(CliRunner.Failure, result.Exit);
        Assert.Contains("rgl.json est introuvable", result.Error);
    }

    // ---- init --package ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Init_package_cree_un_manifeste_de_paquet_valide_et_un_index_sans_ecraser_rien()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();

        var created = Run(project, cache, null, null, "init", "--package");

        Assert.True(created.Exit == 0, created.Error);
        Assert.Contains("Paquet créé", created.Out);
        var diagnostics = new DiagnosticBag();
        var manifest = PackageManifest.Load(Path.Combine(project.Path, "rgl.json"), diagnostics);
        Assert.NotNull(manifest);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Empty(diagnostics.Items);
        Assert.True(File.Exists(Path.Combine(project.Path, "index.forge")));
        Assert.False(File.Exists(Path.Combine(project.Path, "main.rgl")));

        var again = Run(project, cache, null, null, "init", "--package");
        Assert.Equal(CliRunner.Failure, again.Exit);
    }

    // ---- publish ------------------------------------------------------------------------------------------------------------------------------

    private static TempDirectory Package(string version = "1.3.0", string ops = "export gear pure twice(int n): int => n * 2;", string comment = "/// Double un entier.\n")
    {
        var project = new TempDirectory();
        project.File("rgl.json", $"{{\"name\": \"json\", \"version\": \"{version}\", \"description\": \"d\", \"license\": \"MIT\"}}");
        project.File("index.forge", "forge: index\ndescription: 'i'\n\nimport Ops;\nexport {...Ops};\n");
        project.File("Ops.forge", $"forge: gears\ndescription: 'o'\n\n{comment}{ops}\n");
        return project;
    }

    [Fact]
    public void Publish_verifie_le_paquet_puis_cree_et_pousse_le_tag()
    {
        using var project = Package();
        using var cache = new TempDirectory();
        var git = new FakeGit();

        var result = Run(project, cache, Source(), git, "publish");

        Assert.True(result.Exit == 0, result.Error);
        Assert.Contains($"Publié : {Json} 1.3.0 (tag v1.3.0)", result.Out);
        Assert.Equal(["v1.3.0"], git.Tags);
        Assert.Contains("push origin v1.3.0", git.Commands);
    }

    [Fact]
    public void Publish_dry_run_fait_les_verifications_sans_rien_creer()
    {
        using var project = Package();
        using var cache = new TempDirectory();
        var git = new FakeGit();

        var result = Run(project, cache, Source(), git, "publish", "--dry-run");

        Assert.True(result.Exit == 0, result.Error);
        Assert.Contains("peut être publié", result.Out);
        Assert.Empty(git.Tags);
        Assert.DoesNotContain(git.Commands, c => c.StartsWith("push"));
        // Le projet temporaire de vérification n'est pas laissé dans le cache.
        Assert.False(Directory.Exists(Path.Combine(cache.Path, "publish")) && Directory.GetDirectories(Path.Combine(cache.Path, "publish")).Length > 0);
    }

    [Fact]
    public void Publish_refuse_un_depot_sale_un_tag_existant_une_version_deja_publiee_ou_un_depot_absent()
    {
        using var project = Package("1.2.0");
        using var cache = new TempDirectory();

        var dirty = Run(project, cache, Source(), new FakeGit { Status = " M Ops.forge" }, "publish");
        Assert.Equal(CliRunner.Failure, dirty.Exit);
        Assert.Contains("pas commitées", dirty.Error);

        var tagged = new FakeGit();
        tagged.Tags.Add("v1.2.0");
        Assert.Contains("existe déjà", Run(project, cache, Source(), tagged, "publish").Error);

        // 1.2.0 est déjà publiée sur la forge (la source figée en a une).
        var published = Run(project, cache, Source(), new FakeGit(), "publish");
        Assert.Equal(CliRunner.Failure, published.Exit);
        Assert.Contains("déjà publiée", published.Error);

        Assert.Contains("pas un dépôt Git", Run(project, cache, Source(), new FakeGit { Inside = false }, "publish").Error);
        Assert.Contains("git est introuvable", Run(project, cache, Source(), new FakeGit { Missing = true }, "publish").Error);
    }

    [Fact]
    public void Publish_refuse_un_paquet_qui_ne_compile_pas_ou_dont_la_documentation_a_des_avertissements()
    {
        using var cache = new TempDirectory();

        using var broken = Package(ops: "export gear pure twice(int n): int => n * missing;");
        var compile = Run(broken, cache, Source(), new FakeGit(), "publish");
        Assert.Equal(CliRunner.Failure, compile.Exit);
        Assert.Contains("erreur[", compile.Error);

        using var documented = Package(comment: "/// Double un entier.\n/// <param name=\"inconnu\">pas un paramètre</param>\n");
        var git = new FakeGit();
        var docs = Run(documented, cache, Source(), git, "publish");
        Assert.Equal(CliRunner.Failure, docs.Exit);
        Assert.Contains("avertissement(s)", docs.Error);
        Assert.Empty(git.Tags);
    }

    [Fact]
    public void Publish_retire_le_tag_local_quand_le_push_echoue()
    {
        using var project = Package();
        using var cache = new TempDirectory();
        var git = new FakeGit { PushFails = true };

        var result = Run(project, cache, Source(), git, "publish");

        Assert.Equal(CliRunner.Failure, result.Exit);
        Assert.Contains("n'a pas pu être poussé", result.Error);
        Assert.Empty(git.Tags);
    }

    [Fact]
    public void Publish_demande_un_manifeste_de_paquet_valide_et_un_index()
    {
        using var cache = new TempDirectory();

        using var noIndex = new TempDirectory();
        noIndex.File("rgl.json", "{\"name\": \"json\", \"version\": \"1.0.0\"}");
        Assert.Contains("index.forge est introuvable", Run(noIndex, cache, Source(), new FakeGit(), "publish").Error);

        using var badName = new TempDirectory();
        badName.File("rgl.json", "{\"name\": \"std\", \"version\": \"1.0.0\"}");
        badName.File("index.forge", "forge: index\ndescription: 'i'\n");
        Assert.Contains("erreur[E157]", Run(badName, cache, Source(), new FakeGit(), "publish").Error);
    }

    [Fact]
    public void Un_paquet_publie_avec_des_dependances_les_resout_pour_la_verification()
    {
        using var cache = new TempDirectory();
        var source = Source();
        using var project = new TempDirectory();
        project.File("rgl.json", $"{{\"name\": \"json\", \"version\": \"1.3.0\", \"dependencies\": {{\"{Geometry}\": \"2.0.0\"}}}}");
        project.File("index.forge", "forge: index\ndescription: 'i'\n\nimport Wrap;\nexport {...Wrap};\n");
        project.File("Wrap.forge", "forge: gears\ndescription: 'w'\n\nimport package geometry;\n\n/// Ne fait rien.\nexport gear pure un(): int => 1;\n");

        var result = Run(project, cache, source, new FakeGit(), "publish", "--dry-run");

        Assert.True(result.Exit == 0, result.Error);
        Assert.Contains(source.Requests, r => r == $"{Geometry}@2.0.0");
    }

    // ---- la liste des versions d'une forge ---------------------------------------------------------------------------------------------

    private sealed class TagsHandler(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
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
    public void Les_versions_d_un_depot_sont_ses_tags_vX_Y_Z_du_plus_recent_au_plus_ancien()
    {
        var handler = new TagsHandler(System.Net.HttpStatusCode.OK, "[{\"name\": \"v1.2.0\"}, {\"name\": \"latest\"}, {\"name\": \"v1.10.0\"}, {\"name\": \"v2.0\"}, {\"name\": \"v0.9.1\"}]");
        var source = new HttpPackageSource(handler);

        var versions = source.Versions(PackageId.Parse(Json, out _)!);

        Assert.Equal(["1.10.0", "1.2.0", "0.9.1"], versions.Versions);
        Assert.Equal(["https://api.github.com/repos/alice/json/tags?per_page=100"], handler.Urls);
        Assert.Equal("https://gitlab.com/api/v4/projects/bob%2Fgeometry/repository/tags?per_page=100", HttpPackageSource.TagsUrl(PackageId.Parse(Geometry, out _)!));
        Assert.Equal("https://codeberg.org/api/v1/repos/carol/core/tags?limit=50", HttpPackageSource.TagsUrl(PackageId.Parse("codeberg.org/carol/core", out _)!));

        var missing = new HttpPackageSource(new TagsHandler(System.Net.HttpStatusCode.NotFound, "")).Versions(PackageId.Parse(Json, out _)!);
        Assert.True(missing.NotFound);
        var broken = new HttpPackageSource(new TagsHandler(System.Net.HttpStatusCode.OK, "pas du json")).Versions(PackageId.Parse(Json, out _)!);
        Assert.Null(broken.Versions);
        Assert.NotNull(broken.Error);
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0", null)]
    [InlineData("v1.2", null)]
    [InlineData("v01.2.0", null)]
    public void Un_tag_n_est_une_version_que_s_il_s_ecrit_vX_Y_Z(string tag, string? expected) =>
        Assert.Equal(expected, PackageVersion.FromTag(tag));

    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.2.0", "1.2.0", 0)]
    [InlineData("0.9.9", "1.0.0", -1)]
    public void Les_versions_se_comparent_nombre_par_nombre(string left, string right, int sign) =>
        Assert.Equal(sign, Math.Sign(PackageVersion.Compare(left, right)));

    [Fact]
    public void Les_remotes_de_git_donnent_l_adresse_du_paquet()
    {
        using var cache = new TempDirectory();
        foreach (var remote in new[] { "https://github.com/alice/json.git", "git@github.com:alice/json.git", "https://github.com/alice/json", "ssh://git@github.com/alice/json.git" })
        {
            using var project = Package("1.0.0");
            var git = new FakeGit { Remote = remote };
            // 1.0.0 est publiée dans la source figée : l'erreur nomme l'adresse lue du remote.
            Assert.Contains($"« {Json} »", Run(project, cache, Source(), git, "publish", "--dry-run").Error);
        }
    }
}
