using System.Text.Json;
using Rgl.Build;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.Project;

namespace Rgl.Tests;

/// <summary>Tests de <c>rgl init</c> (<see cref="ProjectInitializer"/>).</summary>
public class ProjectInitializerTests
{
    [Fact]
    public void Init_cree_le_squelette_du_projet()
    {
        using var dir = new TempDirectory();
        var diagnostics = new DiagnosticBag();

        var created = ProjectInitializer.Init(dir.Path, diagnostics);

        Assert.Empty(diagnostics.Items);
        // Pas d'icône copiée dans le projet : le programme compilé reçoit celle de RGL par défaut.
        Assert.Equal(["rgl.json", "main.rgl", "src/first.forge"], created);
        Assert.StartsWith("forge: gears\n", File.ReadAllText(Path.Combine(dir.Path, "src", "first.forge")));
        Assert.Contains("train |> done;", File.ReadAllText(Path.Combine(dir.Path, "main.rgl")));
    }

    [Fact]
    public void Init_web_cree_un_serveur_et_une_page_paper()
    {
        using var dir = new TempDirectory();
        var diagnostics = new DiagnosticBag();

        var created = ProjectInitializer.Init(dir.Path, diagnostics, web: true);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(["rgl.json", "main.rgl", "src/Home.paper", "static/site.css"], created);
        Assert.Contains("|> Web.createServer", File.ReadAllText(Path.Combine(dir.Path, "main.rgl")));
        Assert.Contains("|> Home.mount('/')", File.ReadAllText(Path.Combine(dir.Path, "main.rgl")));
        Assert.Contains("|> Web.staticFiles", File.ReadAllText(Path.Combine(dir.Path, "main.rgl")));
        Assert.Contains("@@ title @@", File.ReadAllText(Path.Combine(dir.Path, "src", "Home.paper")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "static", "site.css")));
    }

    [Fact]
    public void Le_manifeste_cree_est_valide_et_porte_le_nom_du_dossier()
    {
        using var dir = new TempDirectory();
        ProjectInitializer.Init(dir.Path, new DiagnosticBag());

        var diagnostics = new DiagnosticBag();
        var manifest = ProjectManifest.Load(Path.Combine(dir.Path, "rgl.json"), diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(new ProjectManifest(Path.GetFileName(dir.Path), "1.0.0", null, ProjectManifest.SupportedLanguageVersion), manifest with { Exclude = ProjectManifest.DefaultExclude });
        Assert.Equal(["out"], manifest!.Exclude);
    }

    [Theory]
    [InlineData("main.rgl")]
    [InlineData("autre.rgl")]
    [InlineData("rgl.json")]
    public void Init_n_ecrase_rien(string existing)
    {
        using var dir = new TempDirectory();
        dir.File(existing, "contenu");
        var diagnostics = new DiagnosticBag();

        Assert.Null(ProjectInitializer.Init(dir.Path, diagnostics));
        Assert.True(diagnostics.HasErrors);
        Assert.Equal("contenu", File.ReadAllText(Path.Combine(dir.Path, existing)));
        Assert.False(File.Exists(Path.Combine(dir.Path, "main.rgl")) && existing != "main.rgl");
    }
}

/// <summary>Tests de la lecture et de la validation de <c>rgl.json</c> (<see cref="ProjectManifest"/>).</summary>
public class ProjectManifestTests
{
    /// <summary>
    /// Écrit un manifeste dans un dossier temporaire puis le charge. <paramref name="manifest"/> est
    /// soit le texte JSON brut, soit un objet sérialisé en JSON.
    /// </summary>
    private static (ProjectManifest? Manifest, DiagnosticBag Diagnostics) Load(object manifest)
    {
        using var dir = new TempDirectory();
        var path = dir.File("rgl.json", manifest as string ?? JsonSerializer.Serialize(manifest));
        var diagnostics = new DiagnosticBag();
        return (ProjectManifest.Load(path, diagnostics), diagnostics);
    }

    [Fact]
    public void L_icone_est_facultative()
    {
        var (manifest, diagnostics) = Load(new { name = "p", version = "1.0.0", rgl = "0" });
        Assert.Empty(diagnostics.Items);
        Assert.Equal(new ProjectManifest("p", "1.0.0", null, "0"), manifest);
    }

    [Theory]
    [InlineData(null, "Checked")]          // absente : le programme s'arrête sur débordement
    [InlineData("checked", "Checked")]
    [InlineData("unchecked", "Unchecked")] // roulement
    public void Le_mode_de_debordement_se_lit_dans_overflow(string? overflow, string expected)
    {
        var (manifest, diagnostics) = overflow is null
            ? Load(new { name = "p", version = "1.0.0", rgl = "0" })
            : Load(new { name = "p", version = "1.0.0", rgl = "0", overflow });

        Assert.Empty(diagnostics.Items);
        Assert.Equal(Enum.Parse<OverflowMode>(expected), manifest!.Overflow);
    }

    [Fact]
    public void La_limite_de_recursion_se_lit_dans_recursion()
    {
        Assert.Equal(200u, Load(new { name = "p", version = "1.0.0", rgl = "0" }).Manifest!.RecursionLimit);
        Assert.Equal(500u, Load(new { name = "p", version = "1.0.0", rgl = "0", recursion = 500 }).Manifest!.RecursionLimit);
        Assert.Null(Load(new { name = "p", version = "1.0.0", rgl = "0", recursion = "infinite" }).Manifest!.RecursionLimit);
    }

    [Fact]
    public void Le_mode_sur_se_lit_dans_safe()
    {
        Assert.False(Load(new { name = "p", version = "1.0.0", rgl = "0" }).Manifest!.Safe);
        Assert.True(Load(new { name = "p", version = "1.0.0", rgl = "0", safe = true }).Manifest!.Safe);

        var (manifest, diagnostics) = Load(new { name = "p", version = "1.0.0", rgl = "0", safe = "oui" });
        Assert.Null(manifest);
        Assert.Contains(diagnostics.Items, d => d.Message.Contains("safe"));
    }

    [Fact]
    public void Les_boucles_bornees_se_lisent_dans_loop()
    {
        Assert.False(Load(new { name = "p", version = "1.0.0", rgl = "0" }).Manifest!.BoundedLoops);
        Assert.False(Load(new { name = "p", version = "1.0.0", rgl = "0", loop = "free" }).Manifest!.BoundedLoops);
        Assert.True(Load(new { name = "p", version = "1.0.0", rgl = "0", loop = "safe" }).Manifest!.BoundedLoops);
        Assert.Null(Load(new { name = "p", version = "1.0.0", rgl = "0", loop = "vite" }).Manifest);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("\"beaucoup\"")]
    public void Une_limite_de_recursion_invalide_est_une_erreur(string value)
    {
        var (manifest, diagnostics) = Load($"{{\"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\", \"recursion\": {value}}}");

        Assert.Null(manifest);
        Assert.Contains("recursion", Assert.Single(diagnostics.Items).Message);
    }

    [Fact]
    public void Un_mode_de_debordement_inconnu_est_une_erreur()
    {
        var (manifest, diagnostics) = Load(new { name = "p", version = "1.0.0", rgl = "0", overflow = "parfois" });

        Assert.Null(manifest);
        Assert.Contains("overflow", Assert.Single(diagnostics.Items).Message);
    }

    [Fact]
    public void Le_debordement_se_regle_par_profil()
    {
        var both = Load(new { name = "p", version = "1.0.0", rgl = "0", overflow = new { debug = "checked", release = "unchecked" } }).Manifest!;
        Assert.Equal((Rgl.IR.OverflowMode.Checked, Rgl.IR.OverflowMode.Unchecked), (both.OverflowFor(release: false), both.OverflowFor(release: true)));

        // Un profil absent suit celui de debug ; une chaîne vaut pour les deux.
        var onlyDebug = Load(new { name = "p", version = "1.0.0", rgl = "0", overflow = new { debug = "unchecked" } }).Manifest!;
        Assert.Equal((Rgl.IR.OverflowMode.Unchecked, Rgl.IR.OverflowMode.Unchecked), (onlyDebug.OverflowFor(release: false), onlyDebug.OverflowFor(release: true)));
        var plain = Load(new { name = "p", version = "1.0.0", rgl = "0", overflow = "unchecked" }).Manifest!;
        Assert.Equal(Rgl.IR.OverflowMode.Unchecked, plain.OverflowFor(release: true));

        Assert.Null(Load(new { name = "p", version = "1.0.0", rgl = "0", overflow = new { debug = "parfois" } }).Manifest);
        Assert.Null(Load(new { name = "p", version = "1.0.0", rgl = "0", overflow = new { test = "checked" } }).Manifest);
    }

    [Fact]
    public void Une_cle_inconnue_avertit()
    {
        var (manifest, diagnostics) = Load(new { name = "p", version = "1.0.0", rgl = "0", auteur = "moi" });
        Assert.NotNull(manifest);
        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.Contains("auteur", warning.Message);
    }

    [Fact]
    public void Les_ressources_sont_des_motifs_relatifs()
    {
        var (manifest, diagnostics) = Load(new { name = "p", version = "1.0.0", rgl = "0", assets = new[] { "front/html/**", "public/favicon.ico" } });

        Assert.Empty(diagnostics.Items);
        Assert.Equal(["front/html/**", "public/favicon.ico"], manifest!.Assets);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/absolu")]
    [InlineData("C:/absolu")]
    public void Une_ressource_ne_sort_pas_du_projet(string asset)
    {
        var (manifest, diagnostics) = Load(new { name = "p", version = "1.0.0", rgl = "0", assets = new[] { asset } });

        Assert.Null(manifest);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Une_icone_introuvable_avertit()
    {
        var (manifest, diagnostics) = Load(new { name = "p", version = "1.0.0", rgl = "0", icon = "absente.png" });
        Assert.NotNull(manifest);
        Assert.Equal(DiagnosticLevel.Warning, Assert.Single(diagnostics.Items).Level);
    }

    [Theory]
    [InlineData("""{ "version": "1.0.0", "rgl": "0" }""")]
    [InlineData("""{ "name": "p", "rgl": "0" }""")]
    [InlineData("""{ "name": "p", "version": "1.0.0" }""")]
    [InlineData("""{ "name": "p", "version": "1.0.0", "rgl": "1" }""")]
    [InlineData("""{ "name": "", "version": "1.0.0", "rgl": "0" }""")]
    [InlineData("""{ "name": 3, "version": "1.0.0", "rgl": "0" }""")]
    [InlineData("""["p"]""")]
    [InlineData("""{ "name": """)]
    public void Manifestes_invalides(string json)
    {
        var (manifest, diagnostics) = Load(json);
        Assert.Null(manifest);
        Assert.True(diagnostics.HasErrors);
    }
}

/// <summary>Tests de la résolution du fichier d'entrée (<see cref="RglProject"/>).</summary>
public class RglProjectTests
{
    [Fact]
    public void Un_fichier_rgl_isole_se_compile_sans_manifeste()
    {
        using var dir = new TempDirectory();
        dir.File("demo.rgl");
        var diagnostics = new DiagnosticBag();

        var project = RglProject.Load("demo.rgl", null, dir.Path, diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.NotNull(project);
        Assert.Null(project.Manifest);
        Assert.Equal("demo", project.Name);
    }

    [Fact]
    public void D_prend_le_seul_fichier_rgl_du_dossier_sans_les_sous_dossiers()
    {
        using var dir = new TempDirectory();
        var entry = dir.File(Path.Combine("app", "main.rgl"));
        dir.File(Path.Combine("app", "src", "autre.rgl"));
        dir.File(Path.Combine("app", "src", "first.forge"));

        var project = RglProject.Load(null, "app", dir.Path, new DiagnosticBag());

        Assert.Equal(entry, project?.EntryFile);
    }

    [Theory]
    [InlineData]
    [InlineData("a.rgl", "b.rgl")]
    public void D_exige_exactement_un_fichier_rgl(params string[] files)
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "app"));
        foreach (var file in files)
            dir.File(Path.Combine("app", file));
        var diagnostics = new DiagnosticBag();

        Assert.Null(RglProject.Load(null, "app", dir.Path, diagnostics));
        Assert.True(diagnostics.HasErrors);
    }

    [Theory]
    [InlineData("first.forge")]
    [InlineData("main.txt")]
    [InlineData("absent.rgl")]
    public void Fichiers_d_entree_refuses(string file)
    {
        using var dir = new TempDirectory();
        dir.File("first.forge");
        dir.File("main.txt");
        var diagnostics = new DiagnosticBag();

        Assert.Null(RglProject.Load(file, null, dir.Path, diagnostics));
        Assert.True(diagnostics.HasErrors);
    }
}

/// <summary>Tests de l'emplacement du cache (<see cref="Cache"/>).</summary>
public class CacheTests
{
    [Fact]
    public void Chaque_projet_a_son_dossier_de_cache()
    {
        var a = new RglProject(Path.Combine("x", "jeu"), "main.rgl", null);
        var b = new RglProject(Path.Combine("y", "jeu"), "main.rgl", null);

        var dirA = Cache.ProjectDirectory("cache", a);

        Assert.Equal(dirA, Cache.ProjectDirectory("cache", a));
        Assert.NotEqual(dirA, Cache.ProjectDirectory("cache", b));
        Assert.StartsWith(Path.Combine("cache", "projets", "main-"), dirA);
    }
}
