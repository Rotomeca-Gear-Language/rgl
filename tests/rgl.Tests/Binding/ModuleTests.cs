using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>
/// Tests des fichiers et des imports : en-têtes, imports (<c>import X;</c>, <c>only</c>), trains nommés et
/// appelables, type de retour déduit, un seul espace de noms par fichier.
/// </summary>
public class ModuleTests
{
    /// <summary>
    /// Lie des fichiers donnés dans l'ordre : les <c>.forge</c> d'abord, le fichier d'entrée en dernier. Chaque fichier est
    /// analysé et vérifié comme à la compilation ; les erreurs de syntaxe sont des erreurs du cas de test.
    /// </summary>
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(params (string Name, string Content)[] files)
    {
        var diagnostics = new DiagnosticBag();
        var modules = new List<SourceModule>();
        for (var i = 0; i < files.Length; ++i)
        {
            var isEntry = i == files.Length - 1;
            var path = isEntry ? "main.rgl" : files[i].Name + ".forge";
            var tokens = new Lexer(new SourceText(path, files[i].Content), diagnostics).Tokenize();
            var unit = new Parser(tokens, diagnostics).Parse();
            Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));

            FileChecks.Check(unit, isEntry, diagnostics);
            if (diagnostics.HasErrors) return (null, diagnostics.Items);
            modules.Add(new SourceModule(files[i].Name, unit, isEntry));
        }

        var program = new Binder(diagnostics).Bind(modules);
        return (program, diagnostics.Items);
    }

    /// <summary>Le programme, en vérifiant qu'aucune erreur n'est signalée.</summary>
    private static IrProgram Valid(params (string Name, string Content)[] files)
    {
        var (program, diagnostics) = Bind(files);
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        return Assert.IsType<IrProgram>(program);
    }

    /// <summary>Codes, fichiers et positions des diagnostics (erreurs et avertissements) des fichiers.</summary>
    private static string[] All(params (string Name, string Content)[] files) =>
        [.. Bind(files).Diagnostics.Select(d => $"{d.Code ?? "-"} {Path.GetFileName(d.Span?.Source.Path)}:{d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];

    private static (string, string) File(string name, string content) => (name, content);

    private const string Entry = "!train |> done;";

    #region En-têtes

    [Fact]
    public void Un_forge_sans_en_tete_est_E016()
    {
        Assert.Equal(["E016 A.forge:1:1"], All(File("A", "mold M { int x; }"), File("main", Entry)));
    }

    [Fact]
    public void Une_description_manquante_avertit_W005_sans_empecher_la_liaison()
    {
        var (program, diagnostics) = Bind(File("A", "forge: mold\nmold M { int x; }"), File("main", "import A;\n" + Entry));

        Assert.NotNull(program);
        var warning = Assert.Single(diagnostics);
        Assert.Equal(("W005", DiagnosticLevel.Warning), (warning.Code, warning.Level));
    }

    [Fact]
    public void Le_fichier_rgl_n_a_pas_d_en_tete_E105()
    {
        Assert.Equal(["E105 main.rgl:1:1"], All(File("main", "forge: train\n" + Entry)));
    }

    [Fact]
    public void Un_genre_inconnu_est_E106()
    {
        Assert.Equal(["E106 A.forge:1:8"], All(File("A", "forge: truc\ndescription: \"x\"\n"), File("main", Entry)));
    }

    [Theory]
    [InlineData("forge: mold\ndescription: \"x\"\ngear f(): int => 1;", "E104 A.forge:3:1")]
    [InlineData("forge: mold\ndescription: \"x\"\nconst int A: 1;", "E104 A.forge:3:1")]
    [InlineData("forge: gears\ndescription: \"x\"\ngear f(): int => 1;\n!train |> done;", "E104 A.forge:4:1")]
    public void Un_genre_n_accepte_que_son_contenu(string content, string expected)
    {
        Assert.Equal([expected], All(File("A", content), File("main", Entry)));
    }

    [Fact]
    public void Un_fichier_train_sans_train_par_defaut_est_E112()
    {
        Assert.Equal(["E112 A.forge:1:1"], All(File("A", "forge: train\ndescription: \"x\"\ntrain a { !train |> done; }"), File("main", Entry)));
    }

    [Fact]
    public void Une_declaration_commune_vient_avant_les_trains_E104()
    {
        const string content = "forge: train\ndescription: \"x\"\ntrain a { !train |> done; }\nconst int A: 1;\n!train |> done;";
        Assert.Equal(["E104 A.forge:4:1"], All(File("A", content), File("main", Entry)));
    }

    [Fact]
    public void Le_train_par_defaut_est_le_dernier_E104()
    {
        const string content = "forge: train\ndescription: \"x\"\n!train |> done;\ntrain a { !train |> done; }";
        Assert.Equal(["E104 A.forge:4:1"], All(File("A", content), File("main", Entry)));
    }

    [Fact]
    public void Le_fichier_rgl_n_a_pas_de_train_nomme_E104()
    {
        Assert.Equal(["E104 main.rgl:1:1"], All(File("main", "train a { !train |> done; }\n!train |> done;")));
    }

    [Fact]
    public void Les_metadonnees_sont_lues_et_ignorees()
    {
        const string header = "forge: gears\ndescription: \"x\"\nmetadata: {\"auteur\": \"T\", \"v\": [1, {\"a\": 2}]}\n";
        Valid(File("A", header + "gear f(): int => 1;"), File("main", "import A;\n" + Entry));
    }

    #endregion

    #region Imports

    private const string Tools = """
        forge: gears
        description: "Outils"

        const uint MAX: 9;
        mold Point { int x; int y; }
        enum Couleur { Rouge, Vert }
        ingot Id: uint;

        gear pure twice(uint n): uint => n * 2;
        """;

    [Fact]
    public void L_acces_qualifie_donne_les_gears_les_constantes_les_molds_les_enums_et_les_ingots()
    {
        var program = Valid(File("Tools", Tools), File("main", """
            import Tools;

            gear calcul(Tools.Point p): uint {
                Tools.Couleur c: Tools.Couleur.Vert;
                Tools.Id id: Tools.Id(3);
                return Tools.twice(Tools.MAX);
            }
            !train |> done;
            """));

        Assert.Contains(program.Gears, g => g.Symbol.QualifiedName == "Tools.twice");
        Assert.Contains(program.Molds, m => m.QualifiedName == "Tools.Point");
        Assert.Contains(program.Enums, e => e.QualifiedName == "Tools.Couleur");
    }

    [Fact]
    public void Only_donne_des_noms_choisis_en_acces_direct_avec_alias()
    {
        Valid(File("Tools", Tools), File("main", """
            import Tools only {twice as dbl, Point, MAX, Couleur as C};

            gear calcul(Point p): uint {
                C c: C.Rouge;
                return dbl(MAX);
            }
            !train |> done;
            """));
    }

    [Fact]
    public void Only_ne_donne_pas_l_acces_qualifie()
    {
        Assert.Equal(["E041 main.rgl:3:19"], All(File("Tools", Tools), File("main", "import Tools only {MAX};\n\ngear f(): uint => Tools.twice(MAX);\n" + Entry)));
    }

    [Fact]
    public void Un_nom_inconnu_de_only_est_E109()
    {
        Assert.Equal(["E109 main.rgl:1:27"], All(File("Tools", Tools), File("main", "import Tools only {twice, nope};\n" + Entry)));
    }

    [Fact]
    public void Un_fichier_ne_voit_que_ce_qu_il_importe()
    {
        var b = File("B", "forge: gears\ndescription: \"x\"\nimport Tools;\ngear f(): uint => Tools.twice(2);");
        Assert.Equal(["E041 main.rgl:2:19"], All(File("Tools", Tools), b, File("main", "import B;\ngear g(): uint => Tools.twice(2);\n" + Entry)));
        Valid(File("Tools", Tools), b, File("main", "import B;\ngear g(): uint => B.f();\n" + Entry));
    }

    [Fact]
    public void Un_fichier_ne_voit_pas_les_noms_du_fichier_d_entree()
    {
        var a = File("A", "forge: gears\ndescription: \"x\"\ngear f(): uint => helper();");
        Assert.Equal(["E035 A.forge:3:19"], All(a, File("main", "gear helper(): uint => 1;\n" + Entry)));
    }

    [Fact]
    public void Deux_fichiers_peuvent_declarer_le_meme_nom()
    {
        Valid(
            File("A", "forge: gears\ndescription: \"x\"\ngear util(): uint => 1;\nmold M { int x; }"),
            File("B", "forge: gears\ndescription: \"x\"\ngear util(): uint => 2;\nmold M { string x; }"),
            File("main", "import A;\nimport B;\ngear util(): uint => A.util() + B.util();\n" + Entry));
    }

    [Fact]
    public void Les_types_d_erreur_standard_se_voient_dans_les_fichiers_importes()
    {
        Valid(File("A", "forge: gears\ndescription: \"x\"\nguard g(int n): int {\n    fail new Exception {message: \"x\"};\n}"), File("main", "import A;\n" + Entry));
    }

    #endregion

    #region Un seul espace de noms

    [Theory]
    [InlineData("mold a { int x; }\nconst int a: 2;", "E103 main.rgl:1:6")]
    [InlineData("mold a { int x; }\ngear a(): int => 1;", "E103 main.rgl:2:6")]
    [InlineData("const int a: 1;\ngear a(): int => 1;", "E103 main.rgl:2:6")]
    public void Un_nom_ne_designe_qu_une_chose(string declarations, string expected)
    {
        Assert.Equal([expected], All(File("main", declarations + "\n" + Entry)));
    }

    [Fact]
    public void Un_import_pris_par_une_declaration_est_E103_et_as_le_renomme()
    {
        var tools = File("Tools", Tools);
        Assert.Equal(["E103 main.rgl:2:6"], All(tools, File("main", "import Tools only {twice};\ngear twice(): int => 1;\n" + Entry)));
        Valid(tools, File("main", "import Tools only {twice as dbl};\ngear twice(): int => 1;\n" + Entry));
    }

    [Fact]
    public void Un_train_et_un_gear_du_meme_nom_sont_E103()
    {
        const string content = "forge: train\ndescription: \"x\"\ngear a(): int => 1;\ntrain a { !train |> done; }\n!train |> done;";
        Assert.Equal(["E103 A.forge:3:6"], All(File("A", content), File("main", Entry)));
    }

    [Fact]
    public void Un_nom_local_ne_reprend_pas_un_nom_commun_mais_deux_trains_ont_chacun_les_leurs()
    {
        const string header = "forge: train\ndescription: \"x\"\n";
        Assert.Equal(["E103 A.forge:5:10"], All(File("A", header + "gear util(): int => 1;\ntrain a {\n    gear util(): int => 2;\n    !train |> done;\n}\n!train |> done;"), File("main", Entry)));
        Valid(File("A", header + "train a {\n    gear util(): int => 1;\n    !train |> done;\n}\ntrain b {\n    gear util(): int => 2;\n    !train |> done;\n}\n!train |> done;"), File("main", Entry));
    }

    #endregion

    #region Trains

    private const string Commandes = """
        forge: train
        description: "Commandes"

        const uint FACTEUR: 2;
        gear pure propre(uint n): uint => n;

        train doubler {
            gear pure fois(uint n): uint => n * FACTEUR;
            !train(uint n) n |> fois |> done;
        }

        train quadrupler {
            !train(uint n) n |> doubler |> doubler |> done;
        }

        !train(uint n) n |> quadrupler |> done;
        """;

    [Fact]
    public void Chaque_train_devient_un_gear_dont_le_type_de_retour_est_deduit()
    {
        var program = Valid(File("Commandes", Commandes), File("main", "import Commandes;\n!train([string] args) 4 as uint |> Commandes |> $ 0;"));

        var trains = program.Gears.Where(g => g.Symbol.IsTrain).Select(g => g.Symbol).ToArray();
        Assert.Equal(["Commandes.doubler", "Commandes.quadrupler", "Commandes.train"], trains.Select(t => t.QualifiedName));
        Assert.All(trains, t =>
        {
            Assert.Equal(PrimitiveType.UInt, t.ReturnType);
            Assert.Equal(GearKind.Gear, t.Kind);
            Assert.Null(t.Errors);
            Assert.True(t.IsPure);
        });
    }

    [Fact]
    public void Un_train_s_appelle_comme_un_gear_partout()
    {
        Valid(File("Commandes", Commandes), File("main", """
            import Commandes only {doubler as dbl, quadrupler};
            import Commandes;

            gear f(uint n): uint => dbl(n) + Commandes.quadrupler(n) + Commandes(n);
            gear pure g(uint n): uint => n |> dbl;
            !train([string] args) 3 as uint |> f |> quadrupler |> $ 0;
            """));
    }

    [Fact]
    public void Un_gear_pure_n_appelle_qu_un_train_sans_effet()
    {
        const string content = """
            forge: train
            description: "x"
            gear ecrire(uint n): void { `{n}` -> console; }
            train bavard {
                !train(uint n) n |> ecrire |> done;
            }
            !train(uint n) n |> done;
            """;
        Assert.Equal(["E057 main.rgl:2:29"], All(File("A", content), File("main", "import A;\ngear pure f(uint n): void { A.bavard(n); }\n" + Entry)));
    }

    [Fact]
    public void Un_train_n_appelle_que_les_trains_ecrits_au_dessus_de_lui()
    {
        const string header = "forge: train\ndescription: \"x\"\n";
        Assert.Equal(["E110 A.forge:3:31"], All(File("A", header + "train a { !train(uint n) n |> b |> done; }\ntrain b { !train(uint n) n |> done; }\n!train(uint n) n |> done;"), File("main", Entry)));
    }

    [Fact]
    public void Les_declarations_communes_n_appellent_aucun_train_du_fichier()
    {
        const string content = "forge: train\ndescription: \"x\"\ngear f(uint n): uint => a(n);\ntrain a { !train(uint n) n |> done; }\n!train(uint n) n |> done;";
        Assert.Equal(["E110 A.forge:3:25"], All(File("A", content), File("main", Entry)));
    }

    [Fact]
    public void Un_gear_local_appelle_les_trains_du_dessus()
    {
        const string content = """
            forge: train
            description: "x"
            train a { !train(uint n) n |> done; }
            train b {
                gear viaA(uint n): uint => a(n);
                !train(uint n) n |> viaA |> done;
            }
            !train(uint n) n |> done;
            """;
        Valid(File("A", content), File("main", Entry));
    }

    private const string Echecs = """
        forge: train
        description: "Échecs"

        guard positif(int n): int {
            if (n < 0) { fail "négatif"; }
            done;
        }
        guard petit(int n): int {
            if (n > 100) { fail "trop grand"; }
            done;
        }

        train verifier {
            train(int n) n |> positif |> petit |> done;
        }

        train sauve {
            train(int n) n |> positif !> [$ 0] |> petit !> [$ 100] |> done;
        }

        train(int n) n |> verifier |> done;
        """;

    [Fact]
    public void Un_train_qui_peut_echouer_est_un_guard_dont_les_erreurs_sont_celles_non_traitees()
    {
        var program = Valid(File("Echecs", Echecs), File("main", "import Echecs;\n!train |> done;"));

        var symbols = program.Gears.Where(g => g.Symbol.IsTrain).ToDictionary(g => g.Symbol.QualifiedName, g => g.Symbol);
        var verifier = symbols["Echecs.verifier"];
        Assert.Equal(GearKind.Guard, verifier.Kind);
        Assert.Equal(PrimitiveType.Int, verifier.ReturnType);
        Assert.Equal(new MoldType(program.Exception), verifier.Errors);

        // Les rails traitent les échecs : ce train-là ne peut pas échouer.
        Assert.Equal(GearKind.Gear, symbols["Echecs.sauve"].Kind);
        Assert.Null(symbols["Echecs.sauve"].Errors);
        // Le train par défaut appelle verifier, qui peut échouer, sans rail : il échoue de même.
        Assert.Equal(program.Exception, ((MoldType)symbols["Echecs.train"].Errors!).Symbol);
    }

    [Fact]
    public void Appele_dans_une_expression_un_train_qui_peut_echouer_donne_un_resultat()
    {
        var program = Valid(File("Echecs", Echecs), File("main", """
            import Echecs;

            gear f(int n): int {
                var r: Echecs.verifier(n);
                return r ?? 0;
            }
            !train |> done;
            """));

        var declaration = program.Gears.Single(g => g.Symbol.Name == "f").Body.Statements.OfType<Rgl.IR.Statements.IrVariableDeclaration>().Single();
        Assert.Equal(new ResultType(PrimitiveType.Int, new MoldType(program.Exception)), declaration.Variable.Type);
    }

    [Fact]
    public void Un_train_qui_peut_echouer_se_traite_comme_un_guard_dans_un_train()
    {
        Valid(File("Echecs", Echecs), File("main", """
            import Echecs;
            train([string] args) 5 |> Echecs.verifier !> [$ 3] |> done;
            """));
    }

    [Fact]
    public void Un_train_qui_peut_echouer_ne_porte_pas_le_bang()
    {
        const string content = "forge: train\ndescription: \"x\"\nguard g(int n): int { done; }\ntrain a { !train(int n) n |> g |> done; }\n!train(int n) n |> done;";
        Assert.Equal(["E064 A.forge:4:11"], All(File("A", content), File("main", Entry)));
    }

    [Fact]
    public void Une_valeur_dollar_s_adapte_au_type_des_autres_fins()
    {
        var program = Valid(File("A", """
            forge: train
            description: "x"
            guard positif(uint n): uint {
                if (n eq 0) { fail "zéro"; }
                done;
            }
            train(uint n) n |> positif !> [$ 0] |> done;
            """), File("main", "import A;\n" + Entry));

        Assert.Equal(PrimitiveType.UInt, program.Gears.Single(g => g.Symbol.IsTrain).Symbol.ReturnType);
    }

    [Fact]
    public void Des_fins_sans_type_commun_sont_E111()
    {
        const string content = """
            forge: train
            description: "x"
            gear texte(Exception e): string => e.message;
            guard positif(uint n): uint {
                if (n eq 0) { fail "zéro"; }
                done;
            }
            train(uint n) n |> positif !> [texte |> done] |> done;
            """;
        Assert.Equal(["E111 A.forge:8:1"], All(File("A", content), File("main", Entry)));
    }

    [Fact]
    public void Un_train_sans_valeur_ne_renvoie_rien()
    {
        var program = Valid(File("A", """
            forge: train
            description: "x"
            gear ecrire(): void { `x` -> console; }
            !train |> ecrire |> done;
            """), File("main", "import A;\n!train |> A |> $ 0;"));

        Assert.Equal(PrimitiveType.Void, program.Gears.Single(g => g.Symbol.IsTrain).Symbol.ReturnType);
    }

    [Fact]
    public void Un_bloc_sans_nom_est_le_train_par_defaut_avec_ses_declarations_locales()
    {
        var program = Valid(File("A", """
            forge: train
            description: "x"
            {
                gear pure local(uint n): uint => n + 1;
                !train(uint n) n |> local |> done;
            }
            """), File("main", "import A;\ngear f(): uint => A(1);\n" + Entry));

        Assert.Contains(program.Gears, g => g.Symbol.QualifiedName == "A.train.local");
        Assert.Contains(program.Gears, g => g.Symbol.QualifiedName == "A.train" && g.Symbol.IsTrain);
    }

    [Fact]
    public void Le_train_d_entree_garde_sa_signature_et_ses_codes_de_sortie()
    {
        var program = Valid(File("main", "!train([string] args) args |> done;"));
        Assert.NotNull(program.Entry.Arguments);
    }

    #region Exports

    private const string Header = "forge: gears\ndescription: \"x\"\n\n";

    private static string[] Codes(params (string Name, string Content)[] files) =>
        [.. Bind(files).Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "-")];

    [Fact]
    public void Sans_aucun_export_tout_le_premier_niveau_est_exporte()
    {
        Valid(File("X", Header + "const uint C: 3;\ngear a(): uint => 1;\nmold M { int n; }"),
            File("main", "import X only {a, M, C};\n!train |> done;"));
    }

    [Theory]
    [InlineData("export gear a(): uint => 1;\ngear b(): uint => 2;")]
    [InlineData("gear a(): uint => 1;\ngear b(): uint => 2;\nexport {a};")]
    public void Avec_un_export_seuls_les_noms_exportes_sont_visibles(string content)
    {
        Valid(File("X", Header + content), File("main", "import X only {a};\ngear f(): uint => a();\n" + Entry));

        Assert.Equal(["E109"], Codes(File("X", Header + content), File("main", "import X only {b};\n" + Entry)));
        Assert.Equal(["E035"], Codes(File("X", Header + content), File("main", "import X;\ngear f(): uint => X.b();\n" + Entry)));
        Valid(File("X", Header + content), File("main", "import X;\ngear f(): uint => X.a();\n" + Entry));
    }

    [Fact]
    public void Export_se_met_devant_un_gear_un_mold_un_enum_un_ingot_une_error_et_une_constante()
    {
        Valid(File("X", Header + """
            export const uint C: 3;
            export gear a(): uint => 1;
            export mold M { int n; }
            export error E { int n; }
            export enum D { Nord, Sud }
            export ingot Id: uint;
            gear cache(): uint => 1;
            """), File("main", "import X only {a, M, E, D, Id, C};\n" + Entry));
    }

    [Fact]
    public void Un_fichier_garde_acces_a_ses_noms_non_exportes()
    {
        Valid(File("X", Header + "gear cache(): uint => 1;\nexport gear a(): uint => cache();"), File("main", "import X only {a};\n" + Entry));
    }

    [Theory]
    [InlineData("export {inconnu};")]
    [InlineData("gear a(): uint => 1;\nexport {a};\nexport {a};")]
    [InlineData("gear a(): uint => 1;\nexport {...Autre};")]
    [InlineData("gear a(): uint => 1;\nexport a;")]
    public void Un_export_invalide_est_signale(string content)
    {
        var codes = Codes(File("X", Header + content), File("main", Entry));

        Assert.Single(codes);
        Assert.Contains(codes[0], new[] { "E113", "E103" });
    }

    [Fact]
    public void Un_export_ne_precede_pas_n_importe_quoi()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("X.forge", Header + "export var x: 3;"), diagnostics).Tokenize();
        new Parser(tokens, diagnostics).Parse();

        Assert.Contains(diagnostics.Items, d => d.Level == DiagnosticLevel.Error);
    }

    #endregion

    [Fact]
    public void Les_operateurs_d_un_mold_importe_s_appliquent_dans_le_fichier_qui_l_importe()
    {
        var program = Valid(File("Geometry", """
            forge: mold
            description: "Vecteurs"

            mold Vec2 {
                float x;
                float y;
                Vec2 +(Vec2 a, Vec2 b) { return {x: a.x + b.x, y: a.y + b.y}; }
                string explicit(Vec2 v) { return `({v.x}, {v.y})`; }
            }
            """), File("main", "import Geometry only {Vec2};\ngear f(Vec2 a): string => (a + a) as string;\n" + Entry));

        Assert.Contains(program.Gears, g => g.Symbol.Name == "Vec2.+");
        Assert.Contains(program.Gears, g => g.Symbol.Name == "f");
    }

    #endregion
}
