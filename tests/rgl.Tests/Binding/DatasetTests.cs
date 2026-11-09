using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>
/// Tests des datasets (slice A1.2A) : <c>dataset Moves of Move { attaquer: {…} }</c>, une table dont chaque entrée a un nom, un identifiant typé
/// par dataset (<c>Moves.id</c>) et <c>get()</c> pour lire l'entrée.
/// </summary>
public class DatasetTests
{
    /// <summary>Lie un projet de fichiers (le premier est le fichier d'entrée) ; rend le programme et les diagnostics.</summary>
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics, TempDirectory Directory) Bind(string main, params (string Path, string Content)[] files)
    {
        var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "main.rgl"), main);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(directory.Path, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        return (program, diagnostics.Items, directory);
    }

    private static string[] Errors(string main, params (string Path, string Content)[] files)
    {
        var (program, diagnostics, directory) = Bind(main + "\n!train |> done;", files);
        using (directory)
        {
            Assert.Null(program);
            return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "")];
        }
    }

    /// <summary>Compile et exécute un programme ; rend le code de sortie et ce qu'il écrit.</summary>
    private static (int Code, string Output) Run(string main, params (string Path, string Content)[] files)
    {
        var (program, diagnostics, directory) = Bind(main, files);
        using (directory)
        {
            Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));
            var cpp = Path.Combine(directory.Path, "main.cpp");
            File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
            var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
                Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), cpp);
            var result = ZigBuild.RunWithInput(executable, null, "");
            return (result.Code, result.Output.Replace("\r\n", "\n"));
        }
    }

    /// <summary>Un programme dont le train écrit <paramref name="body"/>, après <paramref name="declarations"/>.</summary>
    private static (int Code, string Output) RunBody(string declarations, string body, params (string Path, string Content)[] files) =>
        Run($"{declarations}\nemit fin(): uint {{\n{body}\nreturn 0;\n}}\n!train |> fin;", files);

    private const string Classes = """
        mold Class { int<1..100> atk; int<1..100> def; }
        dataset Classes of Class {
            guerrier: {atk: 50, def: 40},
            mage: {atk: 20, def: 10},
        }
        """;

    #region Lecture

    [ZigFact]
    public void Un_dataset_donne_ses_entrees_par_leur_identifiant()
    {
        var (code, output) = RunBody(Classes, "`{Classes.guerrier.get().atk} {Classes.mage.get().def} {Classes.length}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("50 10 2", output);
    }

    [ZigFact]
    public void Un_identifiant_se_range_dans_un_mold_et_se_lit_avec_get()
    {
        var (_, output) = RunBody(Classes + "\nmold Player { string nom; Classes.id job; }",
            "Player p: {nom: 'Zoe', job: Classes.mage};\n`{p.nom} {p.job.get().atk}` -> console;");

        Assert.Equal("Zoe 20", output);
    }

    [ZigFact]
    public void Un_dataset_as_cree_son_mold_depuis_les_entrees()
    {
        var (_, output) = RunBody("dataset Moves as Move {\n  attaquer: {name: 'Attaquer', power: 5, ratio: 0.5, actif: true},\n  soin: {name: 'Soin', power: -20, ratio: 1.5, actif: false},\n}",
            "Move m: Moves.soin.get();\n`{m.name} {m.power} {m.ratio} {m.actif}` -> console;");

        Assert.Equal("Soin -20 1.5 false", output);
    }

    [ZigFact]
    public void Un_mold_cree_peut_avoir_un_champ_tableau_de_litteraux()
    {
        var (_, output) = RunBody("dataset Items as Item {\n  epee: {nom: 'Epee', tags: ['arme', 'fer']},\n}", "`{Items.epee.get().tags[1]}` -> console;");

        Assert.Equal("fer", output);
    }

    [ZigFact]
    public void Un_dataset_peut_referencer_un_autre_par_l_identifiant()
    {
        var (_, output) = RunBody(Classes + "\nmold Hero { string nom; Classes.id job; }\ndataset Heros of Hero { zoe: {nom: 'Zoe', job: Classes.mage}, }",
            "`{Heros.zoe.get().job.get().atk}` -> console;");

        Assert.Equal("20", output);
    }

    [ZigFact]
    public void Les_references_circulaires_entre_datasets_passent()
    {
        var (_, output) = RunBody(
            "mold Monstre { string nom; Quetes.id quete; }\nmold Quete { string titre; Monstres.id cible; }\n"
            + "dataset Monstres of Monstre { loup: {nom: 'Loup', quete: Quetes.chasse}, }\ndataset Quetes of Quete { chasse: {titre: 'Chasse', cible: Monstres.loup}, }",
            "`{Monstres.loup.get().quete.get().cible.get().nom}` -> console;");

        Assert.Equal("Loup", output);
    }

    [ZigFact]
    public void Get_marche_sur_un_resultat_de_gear_et_une_parenthese()
    {
        var (_, output) = RunBody(Classes + "\ngear premier(): Classes.id => Classes.guerrier;",
            "`{premier().get().atk} {(Classes.find('mage') ?? Classes.guerrier).get().atk}` -> console;");

        Assert.Equal("50 20", output);
    }

    #endregion

    #region Noms

    [ZigFact]
    public void Name_et_find_font_l_aller_retour_des_sauvegardes()
    {
        var (_, output) = RunBody(Classes,
            "Classes.id id: Classes.mage;\nstring sauve: id.name();\nClasses.id? relu: Classes.find(sauve);\nClasses.id? rien: Classes.find('inconnu');\n"
            + "`{sauve} {(relu ?? Classes.guerrier).get().atk} {rien eq null}` -> console;");

        Assert.Equal("mage 20 true", output);
    }

    #endregion

    #region Fichiers de donnees

    private const string ClassesJson = """{"guerrier": {"atk": 50, "def": 40}, "mage": {"atk": 20, "def": 10}}""";

    [ZigFact]
    public void Un_dataset_lit_ses_entrees_d_un_fichier_json_objet()
    {
        var (_, output) = RunBody("mold Class { int<1..100> atk; int<1..100> def; }\ndataset Classes from \"classes.json\" of Class;",
            "`{Classes.mage.get().atk} {Classes.length}` -> console;", ("data/classes.json", ClassesJson));

        Assert.Equal("20 2", output);
    }

    [ZigFact]
    public void Un_fichier_json_tableau_nomme_ses_entrees_par_leur_champ_id()
    {
        var (_, output) = RunBody("dataset Moves from \"moves.json\" as Move;",
            "`{Moves.braise.get().name} {Moves.braise.get().power} {Moves.braise.name()}` -> console;",
            ("moves.json", """[{"id": "attaquer", "name": "Attaquer", "power": 5}, {"id": "braise", "name": "Braise", "power": 12}]"""));

        Assert.Equal("Braise 12 braise", output);
    }

    [ZigFact]
    public void Un_texte_du_fichier_devient_une_variante_d_enum_ou_une_reference()
    {
        var (_, output) = RunBody(
            Classes + "\nenum Element { Neutre, Feu }\nmold Move { string name; Element element; }\ndataset Moves from \"moves.json\" of Move;\n"
            + "mold Hero { string nom; Classes.id job; [Moves.id] coups; }\ndataset Heros from \"heros.json\" of Hero;",
            "Hero h: Heros.zoe.get();\n`{h.job.get().atk} {h.coups[1].get().element} {h.coups.length}` -> console;",
            ("moves.json", """{"attaquer": {"name": "Attaquer", "element": "Neutre"}, "braise": {"name": "Braise", "element": "Feu"}}"""),
            ("heros.json", """{"zoe": {"nom": "Zoe", "job": "mage", "coups": ["attaquer", "braise"]}}"""));

        Assert.Equal("20 Feu 2", output);
    }

    #endregion

    #region Fichiers

    [ZigFact]
    public void Un_dataset_d_un_fichier_importe_s_utilise_par_son_nom_ou_qualifie()
    {
        var bdd = "forge: gears\ndescription: \"Données\"\n\ndataset Moves as Move {\n  attaquer: {name: 'Attaquer', power: 5},\n  soin: {name: 'Soin', power: -20},\n}\n";
        var joueur = "forge: gears\ndescription: \"Joueur\"\n\nimport bdd only {Moves};\n\nmold Player { string nom; Moves.id coup; }\n\ngear nommer(Player p): string => p.coup.get().name;\n";
        var (_, output) = Run(
            "import bdd;\nimport joueur only {Player, nommer};\n\nemit fin(): uint {\n    Player p: {nom: 'Zoe', coup: bdd.Moves.soin};\n    bdd.Moves.id autre: bdd.Moves.attaquer;\n"
            + "    `{nommer(p)} {autre.get().power} {bdd.Moves.length}` -> console;\n    return 0;\n}\n\n!train |> fin;",
            ("src/bdd.forge", bdd), ("src/joueur.forge", joueur));

        Assert.Equal("Soin 5 2", output);
    }

    #endregion

    #region Erreurs

    [Fact]
    public void Les_identifiants_de_deux_datasets_ne_se_confondent_pas()
    {
        Assert.Equal(["E003"], Errors(Classes + "\ndataset Moves as Move { a: {n: 1}, }\ngear f(): void { Classes.id x: Moves.a; }"));
    }

    [Fact]
    public void Un_nom_d_entree_mal_ecrit_est_une_erreur_du_dataset()
    {
        Assert.Equal(["E138"], Errors(Classes + "\ngear f(): void { Classes.id x: Classes.guerier; }"));
    }

    [Fact]
    public void Un_mold_inconnu_est_une_erreur()
    {
        Assert.Contains("E138", Errors("dataset Moves of Move { a: {n: 1}, }"));
    }

    [Fact]
    public void Une_entree_en_double_est_une_erreur()
    {
        Assert.Equal(["E138"], Errors("dataset Moves as Move { a: {n: 1}, a: {n: 2}, }"));
    }

    [Fact]
    public void Une_entree_ne_prend_pas_le_nom_d_un_membre()
    {
        Assert.Equal(["E138"], Errors("dataset Moves as Move { id: {n: 1}, }"));
    }

    [Fact]
    public void Un_dataset_vide_est_une_erreur()
    {
        Assert.Equal(["E138"], Errors("mold Move { int n; }\ndataset Moves of Move { }"));
    }

    [Fact]
    public void Un_mold_cree_ne_se_deduit_que_de_litteraux()
    {
        Assert.Equal(["E138"], Errors("const int x: 3;\ndataset Moves as Move { a: {n: x}, }"));
    }

    [Fact]
    public void Une_entree_qui_n_est_pas_du_mold_est_une_erreur_de_type()
    {
        Assert.Equal(["E003"], Errors("mold Move { int n; }\ndataset Moves of Move { a: {n: 'texte'}, }"));
    }

    [Fact]
    public void Un_nom_inconnu_dans_un_fichier_json_est_une_erreur()
    {
        Assert.Equal(["E138"], Errors(Classes + "\nmold Hero { string nom; Classes.id job; }\ndataset Heros from \"heros.json\" of Hero;",
            ("heros.json", """{"zoe": {"nom": "Zoe", "job": "voleur"}}""")));
    }

    [Fact]
    public void Une_variante_inconnue_dans_un_fichier_json_est_une_erreur()
    {
        Assert.Equal(["E138"], Errors("enum Element { Neutre, Feu }\nmold Move { Element element; }\ndataset Moves from \"moves.json\" of Move;",
            ("moves.json", """{"a": {"element": "Glace"}}""")));
    }

    [Fact]
    public void Un_fichier_de_donnees_introuvable_est_une_erreur()
    {
        Assert.Equal(["E107"], Errors("mold Move { int n; }\ndataset Moves from \"absent.json\" of Move;"));
    }

    #endregion
}
