using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Syntax;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>Les fichiers entiers : <c>&lt;- raw chemin</c> et <c>valeur -&gt; raw chemin;</c>, <c>bytes</c>, <c>IoError</c>.</summary>
public class FileIoTests
{
    /// <summary>
    /// Compile un programme du projet (où <c>%P%</c> est le chemin d'un fichier du dossier de travail) avec le
    /// composant <c>File</c>, l'exécute, et renvoie son code, ce qu'il écrit et le dossier, qu'<paramref name="check"/> peut lire.
    /// </summary>
    private static (int Code, string Output) Run(string main, Action<string>? check = null, Action<string>? prepare = null)
    {
        using var directory = new TempDirectory();
        var work = directory.Path.Replace('\\', '/') + "/travail";
        Directory.CreateDirectory(work);
        prepare?.Invoke(work);
        main = main.Replace("%P%", work + "/f.txt").Replace("%D%", work);

        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        var emitted = CppEmitter.Emit(program, "main.rgl");
        System.IO.File.WriteAllText(cpp, emitted.Code);
        var executable = ZigBuild.CompileProgram(directory.Path, "programme", emitted, cpp,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "file.cpp"), Path.Combine(ZigBuild.Runtime, "src", "json.cpp"));
        var result = ZigBuild.RunWithInput(executable, null);
        check?.Invoke(work);
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    [ZigFact]
    public void Un_texte_s_ecrit_et_se_relit_sans_rien_perdre()
    {
        var (code, output) = Run("""
            guard Aller(string chemin): string {
                'été ✓' -> raw chemin;
                string t <- raw chemin;
                return t;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Aller !> [$ 3] |> Afficher |> done;
            """, work => Assert.Equal("été ✓", System.IO.File.ReadAllText(work + "/f.txt")));

        Assert.Equal((0, "été ✓"), (code, output));
    }

    [ZigFact]
    public void La_facade_std_lit_ecrit_et_ajoute_du_texte()
    {
        var (code, output) = Run("""
            use <File> only {readText, writeText, appendText};
            guard Aller(string chemin): string {
                writeText(chemin, 'un');
                appendText(chemin, ' deux');
                return readText(chemin);
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Aller !> [$ 3] |> Afficher |> done;
            """);

        Assert.Equal((0, "un deux"), (code, output));
    }

    [ZigFact]
    public void Un_BOM_n_est_pas_du_texte_mais_reste_dans_les_octets()
    {
        var (code, output) = Run("""
            guard Lire(string chemin): string {
                string t <- raw chemin;
                bytes b <- raw chemin;
                return `{t}|{b.length}|{b[0]}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Lire !> [$ 3] |> Afficher |> done;
            """, prepare: work => System.IO.File.WriteAllBytes(work + "/f.txt", [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b']));

        Assert.Equal((0, "ab|5|239"), (code, output));
    }

    [ZigFact]
    public void Des_bytes_s_ecrivent_et_se_relisent_octet_pour_octet()
    {
        var (code, output) = Run("""
            guard Octets(string chemin): string {
                bytes b: [104, 105, 0, 255, 13, 10];
                b -> raw chemin;
                bytes lus <- raw chemin;
                return `{lus.length} {lus[3]} {lus eq b}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Octets !> [$ 3] |> Afficher |> done;
            """, work => Assert.Equal(new byte[] { 104, 105, 0, 255, 13, 10 }, System.IO.File.ReadAllBytes(work + "/f.txt")));

        Assert.Equal((0, "6 255 true"), (code, output));
    }

    [ZigFact]
    public void Un_texte_et_des_octets_se_convertissent_en_UTF8()
    {
        var (code, output) = Run("""
            use <Errors> only {CastError};
            guard Convertir(string s): string {
                bytes b: s as bytes;
                string t: b as! string;
                bytes mauvais: [195, 40];
                string? n: mauvais as? string;
                return `{b.length} {t} {n ?? 'rien'}`;
            }
            guard Refuser(string s): string {
                bytes mauvais: [195, 40];
                string t: mauvais as! string;
                return t;
            }
            emit Signaler(CastError e): uint {
                `{e.message}|{e.target}` -> console;
                return 7;
            }
            gear Afficher(string s): string {
                `{s}|` -> console;
                return s;
            }
            train 'été' |> Convertir !> [$ 3] |> Afficher |> Refuser !> [Signaler] |> done;
            """);

        Assert.Equal((7, "5 été rien|les octets ne sont pas de l'UTF-8 valide|string"), (code, output));
    }

    [ZigFact]
    public void Custom_convertit_les_bytes_par_un_gear_en_lecture_et_en_ecriture()
    {
        var (code, output) = Run("""
            mold Config { string nom; int niveau; }
            gear LireConfig(bytes b): Config {
                string t: (b as? string) ?? '';
                [string] parts: t.split(';');
                return {nom: parts[0], niveau: parts[1].toInt() ?? 0};
            }
            gear VersOctets(Config c): bytes => `{c.nom};{c.niveau}` as bytes;
            guard Aller(string chemin): string {
                Config c: {nom: 'Héros', niveau: 7};
                c -> custom<Config, VersOctets> chemin;
                Config relu <- custom<Config, LireConfig> chemin;
                return `{relu.nom}|{relu.niveau}`;
            }
            gear Afficher(string s): void { s -> console; }
            train '%P%' |> Aller !> [$ 3] |> Afficher |> done;
            """, work => Assert.Equal("Héros;7", System.IO.File.ReadAllText(work + "/f.txt")));

        Assert.Equal((0, "Héros|7"), (code, output));
    }

    [ZigFact]
    public void Un_train_de_custom_qui_est_un_guard_fait_echouer_le_guard_qui_lit()
    {
        var (code, _) = Run("""
            guard Lire(bytes b): string {
                string t: b as! string;
                if (t eq '') { fail 'vide'; }
                return t;
            }
            guard Aller(string chemin): string {
                string s <- custom<string, Lire> chemin;
                return s;
            }
            train '%P%' |> Aller |> done;
            """, prepare: work => System.IO.File.WriteAllText(work + "/f.txt", ""));

        Assert.Equal(1, code);
    }

    [ZigFact]
    public void Un_fichier_absent_fait_echouer_le_guard_avec_une_IoError()
    {
        var (code, output) = Run("""
            use <Errors> only {IoError};
            guard Lire(string chemin): string {
                string t <- raw chemin;
                return t;
            }
            emit Signaler(IoError e): uint {
                `{e.message}|{e.code}|{e.path}|{e.where}` -> console;
                return 7;
            }
            train '%D%/absent.txt' |> Lire !> [Signaler] |> done;
            """);

        Assert.Equal(7, code);
        Assert.StartsWith("fichier introuvable|5|", output);
        Assert.EndsWith("/travail/absent.txt|main.rgl:3:17", output);
    }

    [ZigFact]
    public void Un_fichier_qui_n_est_pas_de_l_UTF8_ne_se_lit_qu_en_bytes()
    {
        var (code, output) = Run("""
            use <Errors> only {IoError};
            guard Mauvais(string chemin): string {
                bytes b: [195, 40];
                b -> raw chemin;
                bytes lus <- raw chemin;
                string t <- raw chemin;
                return t;
            }
            emit Signaler(IoError e): uint {
                e.message -> console;
                return 7;
            }
            train '%P%' |> Mauvais !> [Signaler] |> done;
            """);

        Assert.Equal((7, "le fichier n'est pas de l'UTF-8 valide"), (code, output));
    }

    [ZigFact]
    public void Un_guard_sans_rail_qui_echoue_fait_echouer_le_train()
    {
        var (code, _) = Run("""
            guard Lire(string chemin): string {
                string t <- raw chemin;
                return t;
            }
            train '%D%/absent.txt' |> Lire |> done;
            """);

        Assert.Equal(1, code);
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
    [InlineData("gear f(string p): string {\n    string t <- raw p;\n    return t;\n}\ntrain |> done;", "E060")]       // lire échoue : seulement dans un guard
    [InlineData("gear f(string p): void {\n    'x' -> raw p;\n}\ntrain |> done;", "E060")]
    [InlineData("guard f(string p): void {\n    5 -> raw p;\n}\ntrain |> done;", "E040")]                                    // un nombre ne s'écrit pas tel quel
    [InlineData("guard f(): string {\n    string t <- raw 5;\n    return t;\n}\ntrain |> done;", "E003")]                    // le chemin est une string
    [InlineData("guard f(string p): void {\n    'x' -> raw;\n}\ntrain |> done;", "E030")]                                     // chemin manquant
    [InlineData("gear f(): string {\n    bytes b: [1];\n    return b as string;\n}\ntrain |> done;", "E085")]                       // as ne lit pas des octets en texte
    [InlineData("gear f(string s): bytes {\n    return s as? bytes;\n}\ntrain |> done;", "E085")]                            // un texte devient toujours des octets
    [InlineData("gear f(bytes b): string {\n    return b as! string;\n}\ntrain |> done;", "E060")]                            // as! seulement dans un guard
    [InlineData("gear lire(string s): int => 0;\nguard f(string p): int {\n    int n <- custom<int, lire> p;\n    return n;\n}\ntrain |> done;", "E003")]   // le train reçoit des bytes
    [InlineData("guard f(string p): int {\n    int n <- custom<int, inconnu> p;\n    return n;\n}\ntrain |> done;", "E035")]
    [InlineData("guard f(string p): int {\n    int n <- custom p;\n    return n;\n}\ntrain |> done;", "E030")]                        // types manquants
    [InlineData("gear lire(bytes b): string => \"\";\nguard f(string p): int {\n    int n <- custom<int, lire> p;\n    return n;\n}\ntrain |> done;", "E003")]   // le train ne renvoie pas un int
    [InlineData("gear ecrire(int n): string => \"\";\nguard f(string p): void {\n    1 -> custom<int, ecrire> p;\n}\ntrain |> done;", "E003")]   // le train d'écriture renvoie des bytes
    [InlineData("guard f(string p): string {\n    string t <- xml p;\n    return t;\n}\ntrain |> done;", "")]               // pas encore pris en charge
    public void Un_fichier_mal_utilise_est_signale(string content, string code)
    {
        var errors = Diagnose(content);

        Assert.NotEmpty(errors);
        if (code.Length > 0) Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_reste_une_variable_quand_aucun_chemin_ne_le_suit()
    {
        Assert.Empty(Diagnose("gear f(): int {\n    int raw: 1;\n    int x: 0;\n    x <- raw;\n    return x;\n}\ntrain |> done;"));
    }
}
