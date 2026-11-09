using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>
/// Tests de bout en bout des projets de plusieurs fichiers : <c>.forge</c> importés, trains nommés et appelés, erreurs d'import,
/// et programmes compilés pour de vrai avec Zig.
/// </summary>
public class ModulesTests
{
    private static (int ExitCode, string Out, string Error) RunWith(ICompilerToolchain? toolchain, string workingDirectory, string cacheRoot, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, workingDirectory, cacheRoot, Toolchain: toolchain));
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Le cache des tests qui compilent pour de vrai : le runtime n'y est compilé qu'une fois.</summary>
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    private const string Commandes = """
        forge: train
        description: "Commandes"

        const uint FACTEUR: 2;

        train doubler {
            gear pure fois(uint n): uint => n * FACTEUR;
            !train(uint n) n |> fois |> done;
        }

        train quadrupler {
            !train(uint n) n |> doubler |> doubler |> done;
        }

        !train(uint n) n |> quadrupler |> done;
        """;

    private const string Outils = """
        forge: train
        description: "Outils"
        metadata: {"auteur": "Tommy"}

        guard positif(int n): int {
            if (n < 0) { fail "négatif"; }
            done;
        }

        gear pure carre(int n): int => n * n;

        train verifier {
            train(int n) n |> positif |> carre |> done;
        }

        train(int n) n |> verifier |> done;
        """;

    [ZigFact]
    public void Un_projet_de_plusieurs_fichiers_s_execute()
    {
        using var project = new TempDirectory();
        project.File("Commandes.forge", Commandes);
        project.File("Geometry.forge", "forge: mold\ndescription: \"Types\"\n\nmold Vec2 { int x; int y; }\n");
        project.File("main.rgl", """
            import Commandes;
            import Geometry only {Vec2};

            emit code(uint n): uint => n;

            !train([string] args) 4 as uint |> Commandes |> code;
            """);

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        Assert.Equal(16, exec.ExitCode);
    }

    [ZigTheory]
    [InlineData("7", 49)]
    [InlineData("-3", 99)]
    public void Un_train_qui_peut_echouer_donne_un_resultat_dans_une_expression(string value, int exitCode)
    {
        using var project = new TempDirectory();
        project.File("Outils.forge", Outils);
        project.File("main.rgl", $$"""
            import Outils;

            gear essai(int n): uint {
                var r: Outils.verifier(n);
                var v: r ?? 99;
                return v as uint;
            }
            emit code(uint n): uint => n;

            !train([string] args) {{value}} |> essai |> code;
            """);

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        Assert.Equal(exitCode, exec.ExitCode);
    }

    [ZigTheory]
    [InlineData("7", 0)]
    [InlineData("-3", 3)]
    public void Un_train_qui_peut_echouer_envoie_son_echec_au_rail_de_l_appelant(string value, int exitCode)
    {
        using var project = new TempDirectory();
        project.File("Outils.forge", Outils);
        project.File("main.rgl", $"import Outils;\n\ntrain([string] args) {value} |> Outils.verifier !> [$ 3] |> done;\n");

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        Assert.Equal(exitCode, exec.ExitCode);
    }

    [ZigFact]
    public void Un_train_dont_les_rails_traitent_les_echecs_renvoie_la_valeur_du_rail()
    {
        using var project = new TempDirectory();
        project.File("Sauve.forge", """
            forge: train
            description: "x"

            guard positif(int n): int {
                if (n < 0) { fail "négatif"; }
                done;
            }

            train sauve {
                train(int n) n |> positif !> [$ 100] |> done;
            }

            !train |> done;
            """);
        project.File("main.rgl", """
            import Sauve only {sauve};

            emit code(int n): uint => n as uint;

            !train([string] args) -5 |> sauve |> code;
            """);

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        Assert.Equal(100, exec.ExitCode);
    }

    [Fact]
    public void Le_cpp_nomme_ce_qui_vient_d_un_fichier_importe_et_pointe_vers_lui()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Commandes.forge", Commandes);
        project.File("main.rgl", "import Commandes;\n!train([string] args) 1 as uint |> Commandes |> $ 0;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp");

        Assert.Equal("", compile.Error);
        var cpp = File.ReadAllText(Assert.Single(Directory.GetFiles(cache.Path, "main.cpp", SearchOption.AllDirectories)));
        Assert.Contains("rgl_g_Commandes_d_doubler(", cpp);
        Assert.Contains("rgl_g_Commandes_d_train(", cpp);
        Assert.Contains("rgl_g_Commandes_d_doubler_d_fois(", cpp);
        Assert.Contains("Commandes.forge\"", cpp);
    }

    [Fact]
    public void Un_import_introuvable_liste_les_fichiers_du_projet()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Outils.forge", Outils);
        project.File("main.rgl", "import Absent;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E107]: fichier « Absent.forge » introuvable", compile.Error);
        Assert.Contains("fichiers .forge du projet : Outils", compile.Error);
    }

    [Fact]
    public void Un_import_est_un_nom_de_fichier_cherche_dans_tout_le_projet_hors_des_boxes()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("src/lib/Outils.forge", "forge: gears\ndescription: \"o\"\ngear f(): int => 1;\n");
        // Une box est opaque : son contenu n'est pas regardé, même avec un nom identique.
        project.File("Base.box/Outils.forge", "forge: gears\ndescription: \"o\"\ngear f(): int => 2;\n");
        project.File("main.rgl", "import Outils;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp");

        Assert.Equal("", compile.Error);
        Assert.Equal(CliRunner.Success, compile.ExitCode);
    }

    [Fact]
    public void Deux_fichiers_de_meme_nom_hors_des_boxes_sont_refuses()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("a/Outils.forge", "forge: gears\ndescription: \"o\"\ngear f(): int => 1;\n");
        project.File("b/Outils.forge", "forge: gears\ndescription: \"o\"\ngear f(): int => 2;\n");
        project.File("main.rgl", "import Outils;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E107]: plusieurs fichiers portent le nom « Outils »", compile.Error);
    }

    [Fact]
    public void Deux_fichiers_de_meme_nom_empechent_de_compiler_meme_sans_import()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("a/Outils.forge", "forge: gears\ndescription: \"o\"\n");
        project.File("b/Outils.forge", "forge: gears\ndescription: \"o\"\n");
        project.File("main.rgl", "!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E107]: plusieurs fichiers portent le nom « Outils »", compile.Error);
    }

    [Fact]
    public void Les_dossiers_exclus_ne_sont_pas_parcourus_out_par_defaut()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Outils.forge", "forge: gears\ndescription: \"o\"\n");
        project.File("out/Outils.forge", "forge: gears\ndescription: \"o\"\n");
        project.File("vendor/Outils.forge", "forge: gears\ndescription: \"o\"\n");
        project.File("main.rgl", "import Outils;\n!train |> done;\n");

        // Sans manifeste : seul « out » est exclu, « vendor » est un doublon.
        Assert.Equal(CliRunner.Failure, RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp").ExitCode);

        project.File("rgl.json", "{\"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\", \"exclude\": [\"out\", \"vendor\"]}");
        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp");

        Assert.Equal("", compile.Error);
        Assert.Equal(CliRunner.Success, compile.ExitCode);
    }

    /// <summary>Un projet avec une box (index, fichier interne, Util propre à la box), un tools, et un Util du projet.</summary>
    private static TempDirectory BoxProject(string main)
    {
        var project = new TempDirectory();
        project.File("Database.box/index.forge", "forge: index\ndescription: \"Base\"\n\nimport Connexion only {ouvrir, Row, LIMITE};\nexport {ouvrir, Row, LIMITE};\n");
        project.File("Database.box/internal/Connexion.forge", """
            forge: gears
            description: "Connexion"

            import [..] Util as U;

            const uint LIMITE: 7;
            mold Row { uint id; }
            gear pure ouvrir(uint n): uint => U.plus1(n);
            gear secret(): uint => 1;
            """);
        project.File("Database.box/Util.forge", "forge: gears\ndescription: \"Util de la box\"\n\ngear pure plus1(uint n): uint => n + 1000;\n");
        project.File("Util.forge", "forge: gears\ndescription: \"Util\"\n\ngear pure plus1(uint n): uint => n + 1;\n");
        project.File("Vectors.tools/Vec2.forge", "forge: mold\ndescription: \"Vec2\"\n\nmold Vec2 { int x; int y; }\n");
        project.File("Vectors.tools/Maths.forge", "forge: gears\ndescription: \"Maths\"\n\nconst int DEUX: 2;\ngear pure doubler(int n): int => n * DEUX;\n");
        project.File("main.rgl", main);
        return project;
    }

    [ZigFact]
    public void Une_box_expose_son_index_un_tools_ses_noms_et_la_box_ne_voit_que_elle_meme()
    {
        using var project = BoxProject("""
            import box Database as DB;
            import tools Vectors;
            import tools Vectors.Maths;

            emit code(uint n): uint => n;

            gear calcul(): uint {
                DB.Row r: {id: DB.LIMITE};
                Vectors.Vec2 v: {x: Vectors.DEUX, y: 0};
                return DB.ouvrir(r.id) + (doubler(v.x) as uint);
            }

            !train([string] args) calcul() |> code;
            """);

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        // ouvrir(7) passe par le Util du projet (import [..]) : 8 ; doubler(2) : 4.
        Assert.Equal(12, exec.ExitCode);
    }

    [Theory]
    [InlineData("import box Database;\ngear f(): uint => Database.secret();\n!train |> done;", "E035")]
    [InlineData("import Connexion;\n!train |> done;", "E107")]
    [InlineData("import box Absente;\n!train |> done;", "E107")]
    [InlineData("import tools Absent;\n!train |> done;", "E107")]
    [InlineData("import tools Vectors.Absent;\n!train |> done;", "E107")]
    [InlineData("import box Database only {secret};\n!train |> done;", "E109")]
    public void Les_noms_hors_de_l_index_et_les_fichiers_d_une_box_restent_invisibles(string main, string code)
    {
        using var project = BoxProject(main);
        using var cache = new TempDirectory();

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains($"erreur[{code}]", compile.Error);
    }

    [Fact]
    public void Une_box_ne_cherche_pas_hors_d_elle_sans_import_crochets_et_un_export_doit_etre_importe()
    {
        using var project = BoxProject("import box Database;\n!train |> done;");
        using var cache = new TempDirectory();
        project.File("Database.box/internal/Connexion.forge", "forge: gears\ndescription: \"c\"\n\nimport Util as U;\ngear pure ouvrir(uint n): uint => n;\n");
        project.File("Database.box/Util.forge", "forge: gears\ndescription: \"u\"\n\ngear pure plus1(uint n): uint => n;\n");
        // Util existe dans la box : c'est celui-là qui est trouvé. Un fichier du projet, lui, n'est pas visible sans [..].
        project.File("Database.box/internal/Connexion.forge", "forge: gears\ndescription: \"c\"\n\nimport Vec2 as V;\n");
        Assert.Contains("erreur[E107]: fichier « Vec2.forge » introuvable", RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp").Error);

        project.File("Database.box/index.forge", "forge: index\ndescription: \"i\"\n\nexport {inconnu};\n");
        Assert.Contains("erreur[E113]", RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp").Error);
    }

    /// <summary>Une box dont l'index réexporte un fichier à plat (<c>...Vectors</c>) ou tel quel (<c>export Vectors;</c>).</summary>
    private static TempDirectory ExportProject(string index, string main)
    {
        var project = new TempDirectory();
        project.File("Geo.box/index.forge", "forge: index\ndescription: \"Geo\"\n\nimport Vectors;\nimport Autre only {triple};\n" + index);
        project.File("Geo.box/Vectors.forge", "forge: gears\ndescription: \"v\"\n\nexport mold Vec2 { int x; int y; }\nexport gear pure doubler(int n): int => n * 2;\ngear pure cache(int n): int => n;\n");
        project.File("Geo.box/Autre.forge", "forge: gears\ndescription: \"a\"\n\ngear pure triple(int n): int => n * 3;\n");
        project.File("main.rgl", main);
        return project;
    }

    [ZigFact]
    public void L_index_d_une_box_reexporte_un_fichier_a_plat_et_n_en_montre_que_les_exports()
    {
        using var project = ExportProject("export {...Vectors, triple};", """
            import box Geo;

            emit code(uint n): uint => n;

            gear calcul(): uint {
                Geo.Vec2 v: {x: 2, y: 0};
                return (Geo.doubler(v.x) + Geo.triple(1)) as uint;
            }

            !train([string] args) calcul() |> code;
            """);

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        Assert.Equal(7, exec.ExitCode);
    }

    [ZigFact]
    public void L_index_d_une_box_exporte_un_fichier_tel_quel_et_ses_noms_restent_qualifies()
    {
        using var project = ExportProject("export Vectors;\nexport {triple};", """
            import box Geo;
            import box Geo only {Vectors as V};

            emit code(uint n): uint => n;

            gear calcul(): uint {
                Geo.Vectors.Vec2 a: {x: 2, y: 0};
                V.Vec2 b: {x: 1, y: 0};
                return (Geo.Vectors.doubler(a.x) + V.doubler(b.x) + Geo.triple(1)) as uint;
            }

            !train([string] args) calcul() |> code;
            """);

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        Assert.Equal(9, exec.ExitCode);
    }

    [Theory]
    [InlineData("export {...Vectors, triple};", "import box Geo;\ngear f(): int => Geo.cache(1);\n!train |> done;", "E035")]
    [InlineData("export Vectors;", "import box Geo;\ngear f(): int => Geo.doubler(1);\n!train |> done;", "E035")]
    [InlineData("export Vectors;", "import box Geo only {doubler};\n!train |> done;", "E109")]
    [InlineData("export {...Absent};", "import box Geo;\n!train |> done;", "E113")]
    [InlineData("export Absent;", "import box Geo;\n!train |> done;", "E113")]
    [InlineData("export {...Vectors, doubler};", "import box Geo;\n!train |> done;", "E103")]
    public void Un_export_d_index_invalide_ou_un_nom_masque_est_signale(string index, string main, string code)
    {
        using var project = ExportProject(index, main);
        using var cache = new TempDirectory();

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains($"erreur[{code}]", compile.Error);
    }

    [Fact]
    public void Un_genre_index_n_existe_que_dans_l_index_d_une_box()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("A.forge", "forge: index\ndescription: \"a\"\n");
        project.File("main.rgl", "import A;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E104]: le genre index est celui de index.forge", compile.Error);
    }

    [Fact]
    public void Un_tools_dont_deux_fichiers_declarent_le_meme_nom_est_E103()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("T.tools/A.forge", "forge: gears\ndescription: \"a\"\n\ngear pure f(): int => 1;\n");
        project.File("T.tools/B.forge", "forge: gears\ndescription: \"b\"\n\ngear pure f(): int => 2;\n");
        project.File("main.rgl", "import tools T;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E103]: plusieurs fichiers de « T » déclarent « f »", compile.Error);
    }

    [Fact]
    public void Un_fichier_de_meme_nom_dans_une_box_et_dans_le_projet_est_permis()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Util.forge", "forge: gears\ndescription: \"u\"\n");
        project.File("B.box/Util.forge", "forge: gears\ndescription: \"u\"\n");
        project.File("B.box/index.forge", "forge: index\ndescription: \"i\"\n\nimport Util;\n");
        project.File("main.rgl", "import Util;\nimport box B;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp");

        Assert.Equal("", compile.Error);
    }

    [Fact]
    public void Un_import_a_la_mauvaise_casse_est_refuse()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Outils.forge", Outils);
        project.File("main.rgl", "import outils;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E107]: le fichier s'appelle « Outils.forge »", compile.Error);
    }

    [Fact]
    public void Des_imports_circulaires_sont_E108()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("A.forge", "forge: gears\ndescription: \"a\"\nimport B;\ngear f(): int => 1;\n");
        project.File("B.forge", "forge: gears\ndescription: \"b\"\nimport A;\ngear g(): int => 1;\n");
        project.File("main.rgl", "import A;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E108]: imports circulaires : A → B → A", compile.Error);
    }

    [Fact]
    public void Une_erreur_dans_un_fichier_importe_montre_son_chemin_et_arrete_la_compilation()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("A.forge", "forge: gears\ndescription: \"a\"\ngear f(): int => 1 +;\n");
        project.File("main.rgl", "import A;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("A.forge:3:", compile.Error);
    }

    [Fact]
    public void Un_forge_sans_en_tete_et_sans_description_est_signale_avec_son_chemin()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("A.forge", "gear f(): int => 1;\n");
        project.File("B.forge", "forge: gears\ngear g(): int => 1;\n");
        project.File("main.rgl", "import A;\nimport B;\n!train |> done;\n");

        var compile = RunWith(null, project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur[E016]: le fichier .forge n'a pas d'en-tête", compile.Error);
        Assert.Contains("avertissement[W005]", compile.Error);
    }
}
