using Rgl.Cli;
using Rgl.Diagnostics;
using Xunit;

namespace Rgl.Tests;

/// <summary>
/// <c>import package</c> (slice V1.3B) : un paquet est une box dont l'index dit ce qui est visible, cherchée parmi les dépendances du fichier qui importe.
/// Les paquets viennent d'une source figée : aucun test n'ouvre Internet.
/// </summary>
public class PackageImportTests
{
    private const string Json = "github.com/alice/json";
    private const string Geometry = "gitlab.com/bob/geometry";
    private const string OtherJson = "codeberg.org/carol/json";

    private static (int Exit, string Error) Compile(TempDirectory project, TempDirectory cache, FakePackageSource source)
    {
        var error = new StringWriter();
        var exit = CliRunner.Run(["compile", "-d", ".", "--emit-cpp"], new CliContext(new StringWriter(), error, project.Path, cache.Path, Packages: source));
        return (exit, error.ToString());
    }

    private static string Dependencies(params string[] addresses) => string.Join(", ", addresses.Select(a => $"\"{a}\": \"1.0.0\""));

    private static TempDirectory Project(string main, string? extraManifest = null, params string[] dependencies)
    {
        var project = new TempDirectory();
        project.File("rgl.json", "{\"name\": \"app\", \"version\": \"1.0.0\", \"rgl\": \"0\", \"dependencies\": {" + Dependencies(dependencies) + "}" + extraManifest + "}");
        project.File("main.rgl", main);
        return project;
    }

    /// <summary>Un paquet dont l'index expose <c>twice</c> (un fichier <c>Ops</c> du paquet) sous le nom de son manifeste.</summary>
    private static Dictionary<string, string> Ops(string function = "twice") => new()
    {
        ["index.forge"] = "forge: index\ndescription: 'i'\n\nimport Ops;\nexport {..." + "Ops};\n",
        ["Ops.forge"] = "forge: gears\ndescription: 'o'\n\nexport gear pure " + function + "(int n): int => n * 2;\ngear pure secret(int n): int => n;\n",
    };

    [Fact]
    public void Un_paquet_s_importe_par_son_nom_court_ou_par_son_adresse()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: Ops());
        using var byName = Project("import package json;\ngear f(): int => json.twice(2);\ntrain |> done;\n", null, Json);
        using var byAddress = Project($"import package \"{Json}\" as AliceJson;\ngear f(): int => AliceJson.twice(2);\ntrain |> done;\n", null, Json);
        using var only = Project("import package json only {twice as twicer};\ngear f(): int => twicer(2);\ntrain |> done;\n", null, Json);

        foreach (var project in new[] { byName, byAddress, only })
        {
            var (exit, error) = Compile(project, cache, source);
            Assert.True(exit == 0, error);
        }
    }

    [Fact]
    public void Ce_que_l_index_n_expose_pas_reste_invisible()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: Ops());
        using var project = Project("import package json;\ngear f(): int => json.secret(2);\ntrain |> done;\n", null, Json);

        var (exit, error) = Compile(project, cache, source);

        Assert.Equal(CliRunner.Failure, exit);
        Assert.Contains("erreur[E035]", error);
    }

    [Fact]
    public void Un_nom_ambigu_inconnu_ou_qui_n_est_pas_une_dependance_est_E160()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: Ops());
        source.Publish(OtherJson, "1.0.0", "json", files: Ops("triple"));
        source.Publish(Geometry, "1.0.0", "geometry", files: Ops());

        using var ambiguous = Project("import package json;\ntrain |> done;\n", null, Json, OtherJson);
        var (exit, error) = Compile(ambiguous, cache, source);
        Assert.Equal(CliRunner.Failure, exit);
        Assert.Contains("erreur[E160]", error);
        Assert.Contains($"{Json}, {OtherJson}".Split(", ")[0], error);

        using var disambiguated = Project($"import package \"{OtherJson}\" as Carol;\nimport package \"{Json}\" as Alice;\ngear f(): int => Carol.triple(1) + Alice.twice(1);\ntrain |> done;\n", null, Json, OtherJson);
        Assert.Equal(0, Compile(disambiguated, cache, source).Exit);

        using var unknown = Project("import package absent;\ntrain |> done;\n", null, Json);
        Assert.Contains("erreur[E160]", Compile(unknown, cache, source).Error);

        using var notDependency = Project($"import package \"{Geometry}\" as G;\ntrain |> done;\n", null, Json);
        var notDependencyError = Compile(notDependency, cache, source).Error;
        Assert.Contains("erreur[E160]", notDependencyError);
        Assert.Contains("pas une dépendance", notDependencyError);

        using var invalid = Project("import package \"example.com/a/b\" as X;\ntrain |> done;\n", null, Json);
        Assert.Contains("erreur[E160]", Compile(invalid, cache, source).Error);
    }

    [Fact]
    public void Un_paquet_ne_voit_que_ses_propres_dependances()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Geometry, "1.0.0", "geometry", files: Ops("triple"));
        // json s'appuie sur geometry ; le projet ne la demande pas lui-même.
        source.Publish(Json, "1.0.0", "json", dependencies: new() { [Geometry] = "1.0.0" }, files: new()
        {
            ["index.forge"] = "forge: index\ndescription: 'i'\n\nimport Wrap;\nexport {...Wrap};\n",
            ["Wrap.forge"] = "forge: gears\ndescription: 'w'\n\nimport package geometry;\n\nexport gear pure sextuple(int n): int => geometry.triple(n) * 2;\n",
        });

        using var good = Project("import package json;\ngear f(): int => json.sextuple(1);\ntrain |> done;\n", null, Json);
        var (exit, error) = Compile(good, cache, source);
        Assert.True(exit == 0, error);

        // Pas de propagation transitive : geometry n'est pas une dépendance du projet.
        using var leaking = Project("import package geometry;\ntrain |> done;\n", null, Json);
        Assert.Contains("erreur[E160]", Compile(leaking, cache, source).Error);
    }

    [Fact]
    public void Deux_paquets_peuvent_avoir_des_fichiers_de_meme_nom()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: Ops("twice"));
        source.Publish(Geometry, "1.0.0", "geometry", files: Ops("triple"));
        // Le projet a lui aussi un fichier Ops.
        using var project = Project("import package json;\nimport package geometry;\nimport Ops;\ngear f(): int => json.twice(1) + geometry.triple(1) + Ops.quadruple(1);\ntrain |> done;\n", null, Json, Geometry);
        project.File("Ops.forge", "forge: gears\ndescription: 'p'\n\ngear pure quadruple(int n): int => n * 4;\n");

        var (exit, error) = Compile(project, cache, source);

        Assert.True(exit == 0, error);
    }

    private static Dictionary<string, string> NativePackage() => new()
    {
        ["index.forge"] = "forge: index\ndescription: 'i'\n\nimport Norm;\nexport {...Norm};\n",
        ["Norm.forge"] = "forge: gears\ndescription: 'n'\n\nexport guard normalise(string texte): string;\n",
        ["normalise.rgl.cpp"] = "rgl_native::normalise(rgl::standard::string t) { return t; }\n",
    };

    [Fact]
    public void Le_cpp_d_un_paquet_est_refuse_sauf_package_trust_cpp()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: NativePackage());
        const string main = "import package json;\ntrain |> done;\n";

        using var refused = Project(main, null, Json);
        var (exit, error) = Compile(refused, cache, source);
        Assert.Equal(CliRunner.Failure, exit);
        Assert.Contains("erreur[E150]", error);
        Assert.Contains(Json, error);
        Assert.Contains("package-trust.cpp", error);

        // no-cpp du projet n'est pas un moyen de l'autoriser : seule l'adresse dans package-trust.cpp le fait.
        using var trusted = Project(main, $", \"package-trust\": {{\"cpp\": [\"{Json}\"]}}", Json);
        var accepted = Compile(trusted, cache, source);
        Assert.DoesNotContain("erreur[E150]", accepted.Error);
    }

    [Fact]
    public void Une_bibliotheque_precompilee_d_un_paquet_sans_package_trust_binaries_est_E161()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: new()
        {
            ["index.forge"] = "forge: index\ndescription: 'i'\n\nimport Counter;\nexport {...Counter};\n",
            ["lib/c.hpp"] = "namespace demo { struct C { C() {} }; }\n",
            ["lib/c.lib"] = "binaire",
            ["lib/libc.a"] = "binaire",
            ["Counter.forge"] = "forge: descriptor\ndescription: 'c'\nheader: \"lib/c.hpp\"\nlink: static\nlibrary: {windows: \"lib/c.lib\", linux: \"lib/libc.a\"}\n\ndescriptor demo::C -> Cc {\n    constructor() => demo::C();\n}\n",
        });
        const string main = "import package json;\ntrain |> done;\n";

        // Le C++ est autorisé, pas les binaires : la bibliothèque n'est pas gardée et la liaison est refusée.
        using var project = Project(main, $", \"package-trust\": {{\"cpp\": [\"{Json}\"]}}", Json);
        var (exit, error) = Compile(project, cache, source);

        Assert.Equal(CliRunner.Failure, exit);
        Assert.Contains("erreur[E161]", error);

        using var withBinaries = Project(main, $", \"package-trust\": {{\"cpp\": [\"{Json}\"], \"binaries\": [\"{Json}\"]}}", Json);
        Assert.DoesNotContain("erreur[E161]", Compile(withBinaries, cache, source).Error);
    }

    [Fact]
    public void L_adresse_d_un_import_package_sans_nom_est_une_erreur_de_syntaxe()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: Ops());
        using var project = Project($"import package \"{Json}\";\ntrain |> done;\n", null, Json);

        var (exit, error) = Compile(project, cache, source);

        Assert.Equal(CliRunner.Failure, exit);
        Assert.Contains("as nom", error);
    }

    [Fact]
    public void Un_fichier_qui_s_appelle_package_s_importe_toujours_sans_rien_devant()
    {
        using var cache = new TempDirectory();
        using var project = Project("import package;\ngear f(): int => package.un();\ntrain |> done;\n");
        project.File("package.forge", "forge: gears\ndescription: 'p'\n\ngear pure un(): int => 1;\n");

        var (exit, error) = Compile(project, cache, new FakePackageSource());

        Assert.True(exit == 0, error);
    }

    private static Dictionary<string, string> Extension(string function) => new()
    {
        ["Ext.forge"] = "forge: extend\ndescription: 'e'\nnamespace Std/Ops;\n\nextend int {\n    gear pure " + function + "(this int v): int => v * 2;\n}\n",
    };

    [Fact]
    public void Les_extensions_d_un_paquet_valent_pour_tout_le_programme_et_un_conflit_de_namespace_s_ecrit_avec_l_adresse()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        source.Publish(Json, "1.0.0", "json", files: Extension("twice"));
        source.Publish(Geometry, "1.0.0", "geometry", files: Extension("twice"));

        // Un seul paquet : rien à écrire, l'extension s'applique sans import.
        using var alone = Project("gear f(): int { int n: 2; return n.twice(); }\ntrain |> done;\n", null, Json);
        var (exit, error) = Compile(alone, cache, source);
        Assert.True(exit == 0, error);

        // Deux paquets, même namespace, même nom : l'appel doit dire lequel, par son adresse.
        using var both = Project("gear f(): int { int n: 2; return n.twice(); }\ntrain |> done;\n", null, Json, Geometry);
        var ambiguous = Compile(both, cache, source);
        Assert.Equal(CliRunner.Failure, ambiguous.Exit);
        Assert.Contains("erreur[E119]", ambiguous.Error);
        Assert.Contains("@\"", ambiguous.Error);

        using var written = Project($"gear f(): int {{ int n: 2; return n.<Std/Ops@\"{Json}\">twice() + n.<Std/Ops@\"{Geometry}\">twice(); }}\ntrain |> done;\n", null, Json, Geometry);
        var (writtenExit, writtenError) = Compile(written, cache, source);
        Assert.True(writtenExit == 0, writtenError);

        using var unknown = Project("gear f(): int { int n: 2; return n.<Std/Ops@\"github.com/nobody/none\">twice(); }\ntrain |> done;\n", null, Json, Geometry);
        Assert.Contains("erreur[E119]", Compile(unknown, cache, source).Error);
    }

    [Fact]
    public void Un_namespace_n_est_unique_que_dans_son_paquet()
    {
        using var cache = new TempDirectory();
        var source = new FakePackageSource();
        var files = Extension("twice");
        files["Ext2.forge"] = "forge: extend\ndescription: 'e'\nnamespace Std/Ops;\n\nextend long {\n    gear pure twice(this long v): long => v * 2;\n}\n";
        source.Publish(Json, "1.0.0", "json", files: files);
        using var project = Project("train |> done;\n", null, Json);

        Assert.Contains("erreur[E123]", Compile(project, cache, source).Error);
    }
}
