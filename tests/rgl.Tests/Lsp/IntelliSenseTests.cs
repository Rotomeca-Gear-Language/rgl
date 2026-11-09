using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>
/// Tests de l'aide à l'écriture : autocomplétion avec import automatique, survol avec le commentaire de la déclaration, aide à la
/// saisie d'un appel et accès à la définition, sur de vrais fichiers de projet.
/// </summary>
public class IntelliSenseTests
{
    private const string Geometry = """
        forge: mold
        description: "Vecteurs 2D"

        // Un vecteur du plan.
        // Ses deux coordonnées sont des doubles.
        mold Vec2 {
            // L'abscisse.
            double x;
            double y;
        }

        /* Les points cardinaux. */
        enum Direction { Nord, Sud }

        /**
         * Longueur d'un vecteur.
         * Toujours positive.
         */
        gear pure longueur(Vec2 v): double => v.x * v.x + v.y * v.y;

        gear<T> inutile(): int => 0;
        """;

    private const string Tools = """
        forge: gears
        description: "Outils"

        // Borne une valeur.
        gear clamp<T: number>(T v, T lo, T hi): T => v;
        gear double2(int n): int => n * 2;
        """;

    /// <summary>Un projet avec ses fichiers ; le dernier texte est celui du document ouvert, <c>|</c> marquant le curseur.</summary>
    private static (SemanticDocument Document, int Offset, TempDirectory Directory, string Text) Open(string main)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("src/Geometry.forge", Geometry.Replace("gear<T> inutile(): int => 0;", ""));
        directory.File("src/Tools.forge", Tools);
        // Le curseur est le « | » qui n'est pas un début de « |> ».
        var cursor = System.Text.RegularExpressions.Regex.Match(main, @"\|(?!>)");
        var text = main.Remove(cursor.Index, 1);
        var path = directory.File("main.rgl", text);
        var offset = cursor.Index;
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, text, project), offset, directory, text);
    }

    private static string Hover(string main)
    {
        var (document, offset, directory, _) = Open(main);
        using (directory) return HoverProvider.At(document, offset)?.Markdown ?? "";
    }

    [Theory]
    [InlineData("@async\ngear len|t(int a): int { return a; }\n!train |> done;", "Promise<int ! AsyncException>")]
    [InlineData("@task\ngear len|t(int a): Promise<int> { return a; }\n!train |> done;", "Promise<int ! AsyncException>")]
    [InlineData("@async\ngear len|t(): void { }\n!train |> done;", "Promise<void ! AsyncException>")]
    [InlineData("@fire\ngear len|t(): void { }\n!train |> done;", "gear lent(): void")]
    public void Le_survol_d_un_gear_async_ou_task_montre_la_promesse_complete(string main, string expected) => Assert.Contains(expected, Hover(main));

    [Fact]
    public void Le_type_d_un_appel_async_est_la_promesse_et_le_survol_explique_les_mots_de_l_asynchrone()
    {
        Assert.Contains("Promise<int ! AsyncException>", Hover("@async\ngear lent(): int { return 1; }\ngear g(): void {\n    var p: lent();\n    var q: |p;\n}\n!train |> done;"));
        Assert.Contains("annulation", Hover("@async\ngear lent(): int { return 1; }\ngear g(): void {\n    var p: lent();\n    can|cel p;\n}\n!train |> done;"));
    }

    private static IReadOnlyList<CompletionItem> Complete(string main)
    {
        var (document, offset, directory, text) = Open(main);
        using (directory)
        {
            var before = text[..offset];
            var line = (uint)before.Count(c => c == '\n');
            var character = (uint)(offset - (before.LastIndexOf('\n') + 1));
            return CompletionProvider.Complete(document.Self.Path, text, line, character, document.Project);
        }
    }

    #region La std

    [Fact]
    public void Use_propose_les_fichiers_et_les_boxes_de_la_std()
    {
        var names = Complete("use <|").Select(i => i.Label).ToArray();

        Assert.Contains("Math", names);
        Assert.Contains("Console", names);
        Assert.DoesNotContain("Private", names);
    }

    [Fact]
    public void Use_only_propose_les_noms_du_fichier_choisi()
    {
        var names = Complete("use <Math> only {|").Select(i => i.Label).ToArray();

        Assert.Contains("sqrt", names);
    }

    [Fact]
    public void Le_survol_d_un_nom_pris_dans_la_std_donne_sa_signature()
    {
        var hover = Hover("use <Math> only {sqrt};\ngear g(): double? => sq|rt(2.0);\n!train |> done;");

        Assert.Contains("sqrt", hover);
    }

    #endregion

    #region Survol

    [Fact]
    public void Le_survol_d_un_gear_importe_montre_sa_signature_et_son_commentaire()
    {
        var hover = Hover("import Geometry only {longueur};\ngear g(): double => lon|gueur({x: 1.0, y: 2.0});\n!train |> done;");

        Assert.Contains("gear pure longueur(Vec2 v): double", hover);
        Assert.Contains("Longueur d'un vecteur.\nToujours positive.", hover);
        Assert.Contains("Geometry.forge", hover);
    }

    [Fact]
    public void Le_survol_d_un_mold_montre_ses_champs_et_le_commentaire_qui_le_precede()
    {
        var hover = Hover("import Geometry only {Vec2};\ngear g(Ve|c2 v): double => 0.0;\n!train |> done;");

        Assert.Contains("mold Vec2 {\n    double x;\n    double y;\n}", hover);
        Assert.Contains("Un vecteur du plan.\nSes deux coordonnées sont des doubles.", hover);
    }

    [Fact]
    public void Le_survol_d_un_champ_montre_son_type_et_son_commentaire()
    {
        var hover = Hover("import Geometry only {Vec2};\ngear g(Vec2 v): double => v.|x;\n!train |> done;");

        Assert.Contains("double x", hover);
        Assert.Contains("L'abscisse.", hover);
        Assert.Contains("membre de* `Vec2`", hover);
    }

    [Fact]
    public void Le_survol_d_une_variable_montre_son_type_meme_deduit()
    {
        var hover = Hover("import Geometry only {longueur};\ngear g(): double {\n    var d: longueur({x: 1.0, y: 2.0});\n    return |d;\n}\n!train |> done;");

        Assert.Contains("var d: double", hover);
        Assert.Contains("variable locale", hover);
    }

    [Fact]
    public void Le_survol_d_un_parametre_generique_et_d_une_variante_et_d_un_mot_cle()
    {
        Assert.Contains("T: number", Hover("gear f<T: number>(T v): T => |v;\n!train |> done;").Length > 0 ? Hover("gear f<T: number>(T| v): T => v;\n!train |> done;") : "");
        Assert.Contains("Direction.Sud", Hover("import Geometry only {Direction};\ngear g(): Direction => Direction.S|ud;\n!train |> done;"));
        Assert.Contains("Déclare un gear", Hover("ge|ar g(): int => 1;\n!train |> done;"));
        Assert.Contains("Entier signé de 32 bits", Hover("gear g(): in|t => 1;\n!train |> done;"));
    }

    [Fact]
    public void Le_survol_d_un_nom_d_import_decrit_le_fichier()
    {
        var hover = Hover("import Geo|metry only {longueur};\n!train |> done;");

        Assert.Contains("forge: mold", hover);
        Assert.Contains("Vecteurs 2D", hover);
    }

    #endregion

    #region Autocomplétion

    [Fact]
    public void Un_nom_d_un_autre_fichier_se_propose_et_son_import_s_ecrit_en_le_choisissant()
    {
        var items = Complete("gear g(): double => lon|\n!train |> done;");

        var item = Assert.Single(items, i => i.Label == "longueur");
        Assert.Equal("Geometry.forge", item.Source);
        var edit = Assert.Single(item.Edits!);
        Assert.Equal((0u, 0u, "import Geometry only {longueur};\n\n"), (edit.Line, edit.Character, edit.Text));
    }

    [Fact]
    public void L_import_s_ajoute_apres_les_autres_ou_complete_celui_du_meme_fichier()
    {
        var after = Complete("import Tools only {clamp};\ngear g(): double => lon|\n!train |> done;");
        Assert.Equal((1u, 0u, "import Geometry only {longueur};\n"), After(after, "longueur"));

        var merged = Complete("import Geometry only {Vec2};\ngear g(): double => lon|\n!train |> done;");
        Assert.Equal((0u, 26u, ", longueur"), (merged.Single(i => i.Label == "longueur") is var i && i.Edits is [var e] ? (e.Line, e.Character, e.Text) : default));

        static (uint, uint, string) After(IReadOnlyList<CompletionItem> items, string label) => items.Single(i => i.Label == label).Edits![0] is var e ? (e.Line, e.Character, e.Text) : default;
    }

    [Fact]
    public void Un_nom_deja_importe_se_propose_sans_import()
    {
        var items = Complete("import Geometry only {longueur};\ngear g(): double => lon|\n!train |> done;");

        Assert.Null(Assert.Single(items, i => i.Label == "longueur").Edits);
    }

    [Fact]
    public void Un_fichier_importe_en_entier_se_propose_par_son_nom_et_ses_noms_apres_un_point()
    {
        var names = Complete("import Geometry;\ngear g(): double => Geometry.|\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Equal(["Vec2", "Direction", "longueur"], names);
    }

    [Fact]
    public void Apres_un_point_l_autocompletion_suit_le_type_de_la_valeur()
    {
        var fields = Complete("import Geometry only {Vec2, longueur};\ngear g(Vec2 v): double {\n    var w: v;\n    return w.|\n}\n!train |> done;").Where(i => i.Source?.StartsWith("extension") != true).Select(i => i.Label).ToArray();
        Assert.Equal(["x", "y"], fields);

        var variants = Complete("import Geometry only {Direction};\ngear g(): int {\n    return Direction.|\n}\n!train |> done;").Select(i => i.Label).ToArray();
        Assert.Equal(["Nord", "Sud"], variants);

        var items = Complete("gear g([int] liste): int {\n    return liste.|\n}\n!train |> done;").Select(i => i.Label).ToArray();
        Assert.Contains("length", items);
        Assert.Contains("push", items);
        Assert.DoesNotContain("has", items);

        var inferred = Complete("import Geometry only {Vec2};\ngear g(): double {\n    var v: new Vec2 {x: 1.0, y: 2.0};\n    return v.y|;\n}\n!train |> done;").Where(i => i.Source?.StartsWith("extension") != true).Select(i => i.Label).ToArray();
        Assert.Equal(["x", "y"], inferred);
    }

    [Fact]
    public void Seul_ce_qui_est_visible_a_l_endroit_du_curseur_se_propose()
    {
        var items = Complete("gear g(int n): int {\n    if (n > 0) {\n        var dedans: 1;\n    }\n    var apres: 2;\n    |\n    var plusloin: 3;\n}\ngear h(int autre): int => 0;\n!train |> done;")
            .Select(i => i.Label).ToArray();

        Assert.Contains("n", items);
        Assert.Contains("apres", items);
        Assert.DoesNotContain("dedans", items);       // bloc refermé
        Assert.DoesNotContain("plusloin", items);     // pas encore déclaré
        Assert.DoesNotContain("autre", items);        // paramètre d'un autre gear
    }

    [Fact]
    public void Une_position_de_type_ne_propose_que_des_types()
    {
        var items = Complete("gear g(Ve|c2 v): int => 0;\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("Vec2", items);        // à importer
        Assert.Contains("int", items);
        Assert.DoesNotContain("longueur", items);
        Assert.DoesNotContain("gear", items);
    }

    [Fact]
    public void Une_ligne_import_propose_les_fichiers_puis_les_noms_du_fichier()
    {
        var files = Complete("import |\n!train |> done;").Select(i => i.Label).ToArray();
        Assert.Contains("Geometry", files);
        Assert.Contains("Tools", files);
        Assert.Contains("tools", files);

        var names = Complete("import Tools only {clamp, |\n!train |> done;").Select(i => i.Label).ToArray();
        Assert.Equal(["double2"], names);
    }

    [Fact]
    public void Le_commentaire_d_une_declaration_est_dans_la_documentation_de_la_proposition()
    {
        var item = Complete("gear g(): int => cla|\n!train |> done;").Single(i => i.Label == "clamp");

        Assert.Contains("Borne une valeur.", item.Documentation);
        Assert.Contains("gear clamp<T: number>(T v, T lo, T hi): T", item.Documentation);
    }

    #endregion

    #region Appel et définition

    [Fact]
    public void L_aide_a_la_saisie_donne_la_signature_et_le_parametre_en_cours()
    {
        var (document, offset, directory, _) = Open("import Tools only {clamp};\ngear g(): int => clamp(1, |\n!train |> done;");
        using (directory)
        {
            var signature = SignatureHelpProvider.At(document, offset)!;

            Assert.Equal("clamp<T: number>(T v, T lo, T hi): T", signature.Label);
            Assert.Equal(1, signature.Active);
            Assert.Equal("T lo", signature.Label[signature.Parameters[1].Start..signature.Parameters[1].End]);
        }
    }

    [Fact]
    public void L_aide_a_la_saisie_montre_la_valeur_par_defaut_d_un_parametre()
    {
        var (document, offset, directory, _) = Open("gear f(int a, string b: \"x\", string c: __CALLER__): int => a;\ngear g(): int => f(1, |\n!train |> done;");
        using (directory)
        {
            var signature = SignatureHelpProvider.At(document, offset)!;

            Assert.Equal("f(int a, string b: \"x\", string c: __CALLER__): int", signature.Label);
            Assert.Equal("string b: \"x\"", signature.Label[signature.Parameters[1].Start..signature.Parameters[1].End]);
        }
    }

    [Fact]
    public void L_aide_a_la_saisie_conserve_toutes_les_surcharges()
    {
        var (document, offset, directory, _) = Open("gear f(int x): int => x;\ngear f(string x): string => x;\ngear g(): int => f(|\n!train |> done;");
        using (directory)
        {
            var signature = SignatureHelpProvider.At(document, offset)!;
            Assert.Equal("f(int x): int", signature.Label);
            Assert.Equal(["f(string x): string"], signature.Alternatives!.Select(s => s.Label));
        }
    }

    [Fact]
    public void La_valeur_du_pipeline_est_le_premier_argument_de_l_aide_a_la_saisie()
    {
        var (document, offset, directory, _) = Open("import Tools only {clamp};\ngear g(int v): int => v |> clamp(|\n!train |> done;");
        using (directory) Assert.Equal(1, SignatureHelpProvider.At(document, offset)!.Active);
    }

    [Fact]
    public void L_acces_a_la_definition_mene_au_fichier_qui_declare_le_nom()
    {
        var (document, offset, directory, _) = Open("import Geometry only {longueur};\ngear g(): double => lon|gueur({x: 1.0, y: 2.0});\n!train |> done;");
        using (directory)
        {
            var symbol = document.TargetAt(offset)!.Symbol!;

            Assert.Equal("Geometry.forge", Path.GetFileName(symbol.Module!.Path));
            Assert.Equal("longueur", symbol.NameSpan.Text);
        }
    }

    #endregion

    [Fact]
    public void Aucune_position_ni_aucun_texte_tronque_ne_fait_echouer_l_analyse()
    {
        const string program = """
            import Geometry only {Vec2, longueur};
            import Tools;
            // Un gear.
            gear pure f<T: number>(T v, mut [Vec2] liste): T {
                var w: v;
                Vec2 p: {x: 1.0, y: 2.0};
                liste.push(p);
                if (T is int) { return w; }
                switch (w) { case 1: break; default: break; }
                var r: [1, 2, 3] ||> (int n) => n * 2;
                `{p.x} {liste[0].y} {longueur(p)}` -> console;
                return v;
            }
            enum Couleur { Rouge, Vert }
            error Rate { string cause; }
            !train([string] args) args |> f<int> !> [done] |> done;
            """;
        var (document, _, directory, _) = Open(program.Replace("import Geometry", "|import Geometry"));
        using (directory)
        {
            for (var length = 0; length <= program.Length; length += 3)
            {
                var text = program[..length];
                var truncated = SemanticDocument.Create(document.Self.Path, text, document.Project);
                for (var offset = 0; offset <= text.Length; offset += 2)
                {
                    HoverProvider.At(truncated, offset);
                    SignatureHelpProvider.At(truncated, offset);
                    truncated.TargetAt(offset);
                }

                var line = (uint)text.Count(c => c == '\n');
                CompletionProvider.Complete(document.Self.Path, text, line, (uint)(text.Length - (text.LastIndexOf('\n') + 1)), document.Project);
                CompletionProvider.Complete(document.Self.Path, text + ".", line, (uint)(text.Length - (text.LastIndexOf('\n') + 1)) + 1, document.Project);
            }
        }
    }

    [Fact]
    public void Les_parametres_generiques_d_un_mold_sont_remplaces_dans_le_type_des_champs_et_des_appels()
    {
        const string declarations = "mold Pair<T, U> {\n    T first;\n    U second;\n}\ngear premier<T, U>(Pair<T, U> p): T => p.first;\n";

        var members = Complete(declarations + "gear g(Pair<int, string> p): int {\n    return p.|\n}\n!train |> done;");
        Assert.Equal([("first", "int"), ("second", "string")], members.Where(i => i.Source?.StartsWith("extension") != true).Select(i => (i.Label, i.Detail!)));

        var hover = Hover(declarations + "gear g(Pair<int, string> p): int {\n    var x: premier(p);\n    return |x;\n}\n!train |> done;");
        Assert.Contains("var x: int", hover);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("ulong")]
    [InlineData("double")]
    public void Un_type_numerique_propose_ses_bornes(string type)
    {
        var labels = Complete("gear f(): void {\n    var m: " + type + ".|;\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Equal(["min", "max"], labels);
    }

    [Fact]
    public void Un_dictionnaire_propose_keys_values_put_et_without_mais_pas_un_tableau()
    {
        var dictionary = Complete("gear f([string:int] d): void {\n    d.|\n}\n!train |> done;").Select(i => i.Label).ToArray();
        var array = Complete("gear f([int] a): void {\n    a.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("keys", dictionary);
        Assert.Contains("values", dictionary);
        Assert.Contains("put", dictionary);
        Assert.Contains("without", dictionary);
        Assert.DoesNotContain("put", array);
        Assert.DoesNotContain("keys", array);
    }

    [Theory]
    [InlineData("gear f([string:int] d): void {\n    var k: d.keys();\n    var x: |k;\n}\n!train |> done;", "[string]")]
    [InlineData("gear f([string:int] d): void {\n    var v: d.values();\n    var x: |v;\n}\n!train |> done;", "[int]")]
    [InlineData("gear f([string:int] d): void {\n    var p: d.put('a', 1);\n    var x: |p;\n}\n!train |> done;", "[string:int]")]
    [InlineData("gear f(): void {\n    var r: parseJson<int>('3');\n    var x: |r;\n}\n!train |> done;", "int ! ParseError")]
    [InlineData("gear f(): void {\n    var r: toJson(3);\n    var x: |r;\n}\n!train |> done;", "string ! ParseError")]
    [InlineData("gear f(): void {\n    var m: int.max;\n    var x: |m;\n}\n!train |> done;", "var m: int")]
    public void Le_type_des_nouveaux_membres_et_intrinseques_est_connu(string main, string expected) => Assert.Contains(expected, Hover(main));
}
