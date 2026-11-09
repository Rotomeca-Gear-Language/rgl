using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>Le format <c>json</c> : <c>&lt;- json&lt;T&gt; chemin</c> et <c>valeur -&gt; json chemin;</c>, avec le chemin de la valeur fautive dans l'erreur.</summary>
public class JsonTests
{
    private const string Types = """
        use <Errors> only {IoError, ParseError};
        enum Direction { Nord, Sud }
        mold Position { int x; int y; }
        mold Config {
            string nom;
            int niveau: 1;
            double ratio;
            bool actif;
            [string] tags;
            Position pos;
            Direction dir;
            string? note;
            [Position] chemin;
            [string:int] scores;
        }
        emit Signaler(IoError | ParseError e): uint {
            match (e) {
                ParseError p |> { `{p.message}|{p.input}` -> console; }
                IoError i |> { `{i.message}` -> console; }
            }
            return 7;
        }
        """;

    /// <summary>Compile et exécute un programme du projet ; <c>%P%</c> est le chemin du fichier de travail, <c>%D%</c> son dossier.</summary>
    private static (int Code, string Output) Run(string main, Action<string>? check = null, string? file = null)
    {
        using var directory = new TempDirectory();
        var work = directory.Path.Replace('\\', '/') + "/travail";
        Directory.CreateDirectory(work);
        if (file is not null) System.IO.File.WriteAllText(work + "/f.json", file);
        main = main.Replace("%P%", work + "/f.json").Replace("%D%", work);

        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "file.cpp"), Path.Combine(ZigBuild.Runtime, "src", "json.cpp"), cpp);
        var result = ZigBuild.RunWithInput(executable, null);
        check?.Invoke(work);
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    private const string Lire = """

        guard Lire(string chemin): string {
            Config c <- json chemin;
            return `{c.nom}|{c.niveau}|{c.ratio}|{c.actif}|{c.note ?? 'aucune'}|{c.chemin.length}|{c.dir}`;
        }
        gear Afficher(string s): void { s -> console; }
        train '%P%' |> Lire !> [Signaler] |> Afficher |> done;
        """;

    [ZigFact]
    public void Une_valeur_s_ecrit_en_JSON_et_se_relit_a_l_identique()
    {
        var (code, output) = Run(Types + """

            guard Aller(string chemin): string {
                Config c: {nom: 'Héros', ratio: 0.5, actif: true, tags: ['a', 'b'], pos: {x: 1, y: 2}, dir: Direction.Sud, chemin: [{x: 3, y: 4}], scores: ['z': 26, 'a': 1]};
                c -> json chemin;
                Config relu <- json chemin;
                return `{relu eq c}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Aller !> [Signaler] |> Afficher |> done;
            """, work =>
        {
            var expected = """
                {
                  "nom": "Héros",
                  "niveau": 1,
                  "ratio": 0.5,
                  "actif": true,
                  "tags": [
                    "a",
                    "b"
                  ],
                  "pos": {
                    "x": 1,
                    "y": 2
                  },
                  "dir": "Sud",
                  "note": null,
                  "chemin": [
                    {
                      "x": 3,
                      "y": 4
                    }
                  ],
                  "scores": {
                    "a": 1,
                    "z": 26
                  }
                }

                """.ReplaceLineEndings("\n");
            Assert.Equal(expected, System.IO.File.ReadAllText(work + "/f.json").ReplaceLineEndings("\n"));
        });

        Assert.Equal((0, "true"), (code, output));
    }

    [ZigFact]
    public void Un_champ_absent_prend_son_defaut_et_un_champ_en_trop_est_ignore()
    {
        var (code, output) = Run(Types + Lire, file: """
            {"nom": "X", "extra": [1, {"b": null}], "ratio": 3, "actif": false, "tags": [], "pos": {"x": 1, "y": 2},
             "dir": "Nord", "chemin": [], "scores": {}}
            """);

        Assert.Equal((0, "X|1|3.0|false|aucune|0|Nord"), (code, output));
    }

    [ZigFact]
    public void Un_JSON_qui_ne_convient_pas_donne_une_ParseError_avec_le_chemin_de_la_valeur()
    {
        var (code, output) = Run(Types + Lire, file: """
            {"nom": "X", "ratio": 1.5, "actif": true, "tags": ["a"], "pos": {"x": 1, "y": 2}, "dir": "Nord",
             "chemin": [{"x": 1, "y": 2}, {"x": 1, "y": "haut"}], "scores": {}}
            """);

        Assert.Equal((7, "$.chemin[1].y : attendu int, reçu une chaîne|$.chemin[1].y"), (code, output));
    }

    [ZigFact]
    public void Un_champ_manquant_sans_defaut_une_variante_inconnue_et_un_JSON_invalide_sont_signales()
    {
        var (code, output) = Run(Types + Lire, file: """{"nom": "X"}""");
        Assert.Equal((7, "$ : champ « ratio » manquant|$"), (code, output));

        (code, output) = Run(Types + Lire, file: """
            {"nom": "X", "ratio": 1, "actif": true, "tags": [], "pos": {"x": 1, "y": 2}, "dir": "Est", "chemin": [], "scores": {}}
            """);
        Assert.Equal((7, "$.dir : variante inconnue : attendu Nord, Sud|$.dir"), (code, output));

        (code, output) = Run(Types + Lire, file: """{"nom": }""");
        Assert.Equal((7, "JSON invalide, ligne 1, colonne 9 : valeur inconnue|$"), (code, output));

        (code, output) = Run(Types + Lire.Replace("%P%", "%D%/absent.json"));
        Assert.Equal((7, "fichier introuvable"), (code, output));
    }

    [ZigFact]
    public void Une_borne_violee_et_un_entier_trop_grand_sont_des_erreurs_de_format()
    {
        const string program = """
            use <Errors> only {IoError, ParseError};
            mold Borne { int<0..100> n; ubyte petit: 0; }
            emit Signaler(IoError | ParseError e): uint {
                match (e) {
                    ParseError p |> { `{p.message}` -> console; }
                    IoError i |> { `{i.message}` -> console; }
                }
                return 7;
            }
            guard Lire(string chemin): string {
                Borne b <- json chemin;
                return `{b.n} {b.petit}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Lire !> [Signaler] |> Afficher |> done;
            """;

        Assert.Equal((0, "50 255"), Run(program, file: """{"n": 50, "petit": 255}"""));
        Assert.Equal((7, "$.n : valeur hors des bornes de int<0..100>"), Run(program, file: """{"n": 150}"""));
        Assert.Equal((7, "$.petit : l'entier ne tient pas dans un ubyte"), Run(program, file: """{"n": 5, "petit": 256}"""));
        Assert.Equal((7, "$.n : attendu int, reçu un nombre"), Run(program, file: """{"n": 5.5}"""));
    }

    [ZigFact]
    public void Un_mold_anonyme_se_lit_avec_json_entre_chevrons()
    {
        var (code, output) = Run("""
            use <Errors> only {IoError, ParseError};
            emit Signaler(IoError | ParseError e): uint { return 7; }
            guard Lire(string chemin): string {
                var c <- json<{int x, string nom, [int] valeurs}> chemin;
                return `{c.x}|{c.nom}|{c.valeurs.length}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Lire !> [Signaler] |> Afficher |> done;
            """, file: """{"nom": "éé😀", "x": 4, "valeurs": [1, 2, 3]}""");

        Assert.Equal((0, "4|éé😀|3"), (code, output));
    }

    [ZigFact]
    public void Les_tableaux_fixes_ou_bornes_les_tuples_les_ingots_et_les_nullables_se_lisent_et_s_ecrivent()
    {
        var (code, output) = Run("""
            use <Errors> only {Exception, IoError, ParseError};
            ingot Id: int;
            mold Divers {
                [int;3] trois;
                [int;1..2] borne;
                (int, string) paire;
                Id id;
                [int?] trous;
                float f;
                long grand;
                mstring m;
            }
            emit Signaler(Exception | IoError | ParseError e): uint {
                match (e) {
                    ParseError p |> { `{p.message}` -> console; }
                    IoError i |> { `{i.message}` -> console; }
                    Exception x |> { `{x.message}` -> console; }
                }
                return 7;
            }
            guard Lire(string chemin): string {
                Divers d <- json chemin;
                d -> json `{chemin}.out`;
                return `{d.trois[2]} {d.borne.length} {d.paire.Item2} {d.id°} {d.trous[1] ?? -1} {d.f} {d.grand} {d.m}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Lire !> [Signaler] |> Afficher |> done;
            """, work =>
        {
            var expected = """
                {
                  "trois": [
                    1,
                    2,
                    3
                  ],
                  "borne": [
                    5
                  ],
                  "paire": [
                    7,
                    "x"
                  ],
                  "id": 9,
                  "trous": [
                    1,
                    null
                  ],
                  "f": 1.5,
                  "grand": -9000000000,
                  "m": "hé"
                }

                """.ReplaceLineEndings("\n");
            Assert.Equal(expected, System.IO.File.ReadAllText(work + "/f.json.out").ReplaceLineEndings("\n"));
        }, file: """{"trois": [1, 2, 3], "borne": [5], "paire": [7, "x"], "id": 9, "trous": [1, null], "f": 1.5, "grand": -9000000000, "m": "hé"}""");

        Assert.Equal((0, "3 1 x 9 -1 1.5 -9000000000 hé"), (code, output));
    }

    [ZigFact]
    public void Une_taille_de_tableau_refusee_est_une_erreur_de_format_et_un_mold_recursif_se_lit()
    {
        const string program = """
            use <Errors> only {Exception, IoError, ParseError};
            mold Arbre { string nom; [Arbre] enfants; }
            mold Trois { [int;3] v; }
            emit Signaler(Exception | IoError | ParseError e): uint {
                match (e) {
                    ParseError p |> { `{p.message}` -> console; }
                    IoError i |> { `{i.message}` -> console; }
                    Exception x |> { `{x.message}` -> console; }
                }
                return 7;
            }
            guard Lire(string chemin): string {
                Arbre a <- json chemin;
                Arbre b <- json chemin;
                return `{a.enfants.length} {a.enfants[1].enfants[0].nom} {a eq b}`;
            }
            guard LireTrois(string chemin): string {
                Trois t <- json chemin;
                return `{t.v[0]}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> LIRE !> [Signaler] |> Afficher |> done;
            """;

        Assert.Equal((0, "2 c true"), Run(program.Replace("LIRE", "Lire"),
            file: """{"nom": "r", "enfants": [{"nom": "a", "enfants": []}, {"nom": "b", "enfants": [{"nom": "c", "enfants": []}]}]}"""));
        Assert.Equal((7, "$.v : le tableau n'a pas une taille permise par [int;3]"), Run(program.Replace("LIRE", "LireTrois"), file: """{"v": [1, 2]}"""));
    }

    /// <summary>Les diagnostics d'un programme d'un seul fichier, sans le compiler en C++.</summary>
    private static string[] Diagnose(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        if (!diagnostics.Items.Any(d => d.Level == DiagnosticLevel.Error)) new Binder(diagnostics).Bind(unit);
        return [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code + " " + d.Message)];
    }

    [Theory]
    [InlineData("guard f(string p): int {\n    var c <- json p;\n    return 1;\n}\ntrain |> done;", "E001")]                      // le type du JSON est inconnu
    [InlineData("mold M { int x; }\ngear f(string p): M {\n    M c <- json p;\n    return c;\n}\ntrain |> done;", "E060")]    // lire seulement dans un guard
    [InlineData("guard f(string p): void {\n    ((int) => int) g: (int x) => x;\n    g -> json p;\n}\ntrain |> done;", "E040")]   // une lambda n'est pas une donnée
    [InlineData("mold M { symbol s; }\nguard f(string p): void {\n    M c <- json p;\n}\ntrain |> done;", "E040")]
    [InlineData("guard f(string p): void {\n    [int:int] c <- json p;\n}\ntrain |> done;", "")]                                  // clés entières : pas encore pris en charge
    [InlineData("guard f(string p): void {\n    int | string c <- json p;\n}\ntrain |> done;", "")]                               // union : pas encore pris en charge
    public void Un_JSON_mal_utilise_est_signale(string content, string code)
    {
        var errors = Diagnose(content);

        Assert.NotEmpty(errors);
        if (code.Length > 0) Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }
}
