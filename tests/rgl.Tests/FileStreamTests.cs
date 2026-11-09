using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>Le format <c>file</c> : un flux ouvert, <c>FileStream</c>, ses méthodes et sa fermeture.</summary>
public class FileStreamTests
{
    private const string Header = """
        use <Errors> only {IoError};
        emit Signaler(IoError e): uint {
            `{e.message}|{e.path}` -> console;
            return 7;
        }
        gear Afficher(string s): void { s -> console; }

        """;

    /// <summary>Compile et exécute un programme du projet ; <c>%P%</c> est le chemin du fichier de travail, <c>%D%</c> son dossier.</summary>
    private static (int Code, string Output) Run(string main, string? file = null, string[]? checks = null)
    {
        using var directory = new TempDirectory();
        var work = directory.Path.Replace('\\', '/') + "/travail";
        Directory.CreateDirectory(work);
        if (file is not null) System.IO.File.WriteAllText(work + "/f.txt", file);
        main = main.Replace("%P%", work + "/f.txt").Replace("%D%", work);

        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "file.cpp"), cpp);
        var result = ZigBuild.RunWithInput(executable, null);
        return (result.Code, result.Output.Replace("\r\n", "\n").Replace(work, "%T%"));
    }

    [ZigFact]
    public void Un_flux_lit_ses_lignes_une_a_une_jusqu_a_null()
    {
        var (code, output) = Run(Header + """

            guard Test(string chemin): string {
                var f <- file chemin;
                string? a: f.readLine();
                string? b: f.readLine();
                string? c: f.readLine();
                string? d: f.readLine();
                return `{a ?? '-'}|{b ?? '-'}|{c ?? '-'}|{d ?? '-'}`;
            }
            train '%P%' |> Test !> [Signaler] |> Afficher |> done;
            """, file: "un\r\ndeux\n\nétoile");

        Assert.Equal((0, "un|deux||étoile"), (code, output));

        (code, output) = Run(Header + """

            guard Test(string chemin): string {
                var f <- file chemin;
                string? a: f.readLine();
                string? b: f.readLine();
                string? c: f.readLine();
                return `{a ?? '-'}|{b ?? '-'}|{c ?? '-'}`;
            }
            train '%P%' |> Test !> [Signaler] |> Afficher |> done;
            """, file: "un\ndeux");

        Assert.Equal((0, "un|deux|-"), (code, output));
    }

    [ZigFact]
    public void Un_flux_lit_des_octets_et_la_copie_partage_la_position()
    {
        var (code, output) = Run(Header + """

            guard Test(string chemin): string {
                var f <- file chemin;
                var copie: f;
                bytes a: f.readBytes(3);
                string? ligne: copie.readLine();
                bytes b: f.readBytes(100);
                return `{a.length} {ligne ?? '-'} {b.length}`;
            }
            train '%P%' |> Test !> [Signaler] |> Afficher |> done;
            """, file: "abcdef\nxy");

        Assert.Equal((0, "3 def 2"), (code, output));
    }

    [ZigFact]
    public void Un_flux_ecrit_a_la_suite_puis_ajoute_et_se_ferme_a_la_demande()
    {
        var (code, output) = Run(Header + """

            guard Test(string chemin): string {
                var f <- file<write> chemin;
                f.write("a\n");
                bytes b: "b\n" as bytes;
                f.write(b);
                f.close();
                var g <- file<append> chemin;
                g.write('c');
                g.close();
                string t <- raw chemin;
                return t;
            }
            train '%P%' |> Test !> [Signaler] |> Afficher |> done;
            """);

        Assert.Equal((0, "a\nb\nc"), (code, output));
    }

    [ZigFact]
    public void Un_flux_se_ferme_en_fin_de_portee_et_ses_erreurs_sont_des_IoError()
    {
        // Le flux ouvert par Ecrire se ferme quand Ecrire finit : Relire peut rouvrir le fichier en écriture (sous Windows, un fichier encore ouvert refuserait).
        var (code, output) = Run(Header + """

            guard Ecrire(string chemin): string {
                var f <- file<write> chemin;
                f.write('un');
                return chemin;
            }
            guard Relire(string chemin): string {
                var g <- file<write> chemin;
                g.write('deux');
                g.close();
                string t <- raw chemin;
                return t;
            }
            train '%P%' |> Ecrire !> [Signaler] |> Relire !> [Signaler] |> Afficher |> done;
            """);
        Assert.Equal((0, "deux"), (code, output));

        // Fermé, un flux ne lit plus, ni pour ses copies.
        (code, output) = Run(Header + """

            guard Test(string chemin): string {
                var f <- file chemin;
                var copie: f;
                f.close();
                string? l: copie.readLine();
                return l ?? '-';
            }
            train '%P%' |> Test !> [Signaler] |> Afficher |> done;
            """, file: "x");
        Assert.Equal((7, "le flux est fermé|%T%/f.txt"), (code, output));

        // Un flux en lecture n'écrit pas.
        (code, output) = Run(Header + """

            guard Test(string chemin): string {
                var f <- file chemin;
                f.write('x');
                return '';
            }
            train '%P%' |> Test !> [Signaler] |> Afficher |> done;
            """, file: "x");
        Assert.Equal((7, "le flux n'est pas ouvert pour cette opération|%T%/f.txt"), (code, output));

        // Un fichier absent, un texte qui n'est pas de l'UTF-8.
        (code, output) = Run(Header + """

            guard Test(string chemin): string {
                var f <- file chemin;
                return '';
            }
            train '%D%/absent.txt' |> Test !> [Signaler] |> Afficher |> done;
            """);
        Assert.Equal((7, "fichier introuvable|%T%/absent.txt"), (code, output));
    }

    /// <summary>Les diagnostics d'un programme d'un seul fichier : erreurs, et avertissements.</summary>
    private static (string[] Errors, string[] Warnings) Diagnose(string content)
    {
        using var directory = new TempDirectory();
        var path = directory.File("main.rgl", content);
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(path, content), directory.Path, diagnostics);
        if (modules is not null) new Binder(diagnostics).Bind(modules);
        string[] Of(DiagnosticLevel level) => [.. diagnostics.Items.Where(d => d.Level == level).Select(d => d.Code + " " + d.Message)];
        return (Of(DiagnosticLevel.Error), Of(DiagnosticLevel.Warning));
    }

    [Theory]
    [InlineData("gear f(string p): void {\n    var f <- file p;\n}\ntrain |> done;", "E060")]                                        // ouvrir seulement dans un guard
    [InlineData("guard f(string p): void {\n    var f <- file<lire> p;\n}\ntrain |> done;", "E030")]                              // mode inconnu
    [InlineData("guard f(string p): void {\n    var f <- file p;\n    f.write(5);\n}\ntrain |> done;", "E040")]                    // un nombre n'est pas du texte
    [InlineData("gear f(FileStream f): string {\n    return f.readLine() ?? '-';\n}\ntrain |> done;", "E060")]                    // lire seulement dans un guard
    [InlineData("guard f(FileStream f): void {\n    f.readBytes();\n}\ntrain |> done;", "E058")]
    [InlineData("guard f(string p): void {\n    'x' -> file p;\n}\ntrain |> done;", "")]                                           // écrire vers file : pas encore pris en charge
    public void Un_flux_mal_utilise_est_signale(string content, string code)
    {
        var (errors, _) = Diagnose(content);

        Assert.NotEmpty(errors);
        if (code.Length > 0) Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void Un_flux_const_avertit_et_close_se_fait_ailleurs_que_dans_un_guard()
    {
        var (errors, warnings) = Diagnose("guard f(string p): void {\n    const FileStream f <- file p;\n}\ntrain |> done;");
        Assert.Empty(errors);
        Assert.Contains(warnings, w => w.StartsWith("W016", StringComparison.Ordinal));

        (errors, _) = Diagnose("gear fermer(FileStream f): void {\n    f.close();\n}\ntrain |> done;");
        Assert.Empty(errors);
    }
}
