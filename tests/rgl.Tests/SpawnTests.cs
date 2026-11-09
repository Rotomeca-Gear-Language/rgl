using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary><c>spawn</c> : un processus enfant, son code de sortie, ses sorties et les raisons de son échec.</summary>
public class SpawnTests
{
    private const string Header = "use <Process> only {ProcessResult, SpawnError};\n";

    /// <summary>Compile et exécute un programme du projet ; <c>%ZIG%</c> est le chemin de Zig (un programme qui existe, et dont on connaît la sortie).</summary>
    private static (int Code, string Output) Run(string main)
    {
        using var directory = new TempDirectory();
        main = main.Replace("%ZIG%", ZigBuild.Zig.Replace('\\', '/'));

        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "process.cpp"), cpp);
        var result = ZigBuild.RunWithInput(executable, null);
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    private static string Show(string spawn) => Header + $$"""
        gear Test(): string {
            var r: {{spawn}};
            return match (r) {
                ok p |> `ok {p.exitCode} {p.stdout.length > 0} {p.stderr.length > 0}`;
                err e |> `err {e.kind} {e.message.length > 0}`;
            };
        }
        gear Afficher(string s): void { s -> console; }
        emit Fin(): uint => 0;
        !train |> Test |> Afficher |> Fin;
        """;

    [ZigFact]
    public void Un_processus_donne_son_code_de_sortie_et_ses_sorties()
    {
        Assert.Equal((0, "ok 0 true false"), Run(Show("spawn \"%ZIG%\" \"version\"")));
        Assert.Equal((0, "ok 1 false true"), Run(Show("spawn \"%ZIG%\" \"commande-qui-n-existe-pas\"")));
    }

    [ZigFact]
    public void Un_argument_est_une_variable_string_ou_un_template()
    {
        var (code, output) = Run(Header + """
            gear Test(): string {
                string sous: "version";
                var r: spawn "%ZIG%" sous;
                var s: spawn `%ZIG%` `{sous}`;
                return match (r) {
                    ok p |> `ok {p.exitCode}`;
                    err e |> `err`;
                };
            }
            gear Afficher(string s): void { s -> console; }
            emit Fin(): uint => 0;
            !train |> Test |> Afficher |> Fin;
            """);

        Assert.Equal((0, "ok 0"), (code, output));
    }

    [ZigFact]
    public void Un_executable_introuvable_est_une_SpawnError()
    {
        Assert.Equal((0, "err ExecNotFound true"), Run(Show("spawn \"programme-introuvable-de-rgl\"")));
    }

    [ZigFact]
    public void Les_options_choisissent_les_sorties_l_environnement_et_le_delai()
    {
        // Une sortie jetée ne se retrouve pas dans le résultat.
        Assert.Equal((0, "ok 0 false false"), Run(Show("spawn(\"stdout=null\") \"%ZIG%\" \"version\"")));

        // Une variable d'environnement ajoutée.
        var echo = OperatingSystem.IsWindows() ? "\"cmd\" \"/c\" \"echo\" \"%RGL_X%\"" : "\"sh\" \"-c\" \"echo $RGL_X\"";
        var (code, output) = Run(Header + $$"""
            gear Test(): string {
                var r: spawn('env="RGL_X=ok"') {{echo}};
                return match (r) {
                    ok p |> p.stdout;
                    err e |> `err`;
                };
            }
            gear Afficher(string s): void { s -> console; }
            emit Fin(): uint => 0;
            !train |> Test |> Afficher |> Fin;
            """);
        Assert.Equal((0, "ok\n"), (code, output));

        // Un délai dépassé tue le processus.
        var sleep = OperatingSystem.IsWindows() ? "\"ping\" \"-n\" \"6\" \"127.0.0.1\"" : "\"sleep\" \"5\"";
        Assert.Equal((0, "err Timeout true"), Run(Show($"spawn(\"timeout=300\") {sleep}")));
    }

    /// <summary>Les diagnostics d'un programme d'un seul fichier : erreurs et avertissements.</summary>
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
    [InlineData("gear f(string a): string {\n    var r: spawn \"x\";\n    return a;\n}\ngear g(string s): void {\n    s |> f |> spawn \"x\";\n}\ntrain |> done;", "")]               // pas une étape de pipeline
    [InlineData("gear f(string a): void {\n    f(spawn \"x\");\n}\ntrain |> done;", "E124")]                                                                  // pas un argument
    [InlineData("gear f(): void {\n    var r: spawn \"rgl\" \"compile\";\n}\ntrain |> done;", "E124")]                                                      // pas le compilateur
    [InlineData("gear f(): void {\n    var r: spawn \"C:/bin/RGL.EXE\";\n}\ntrain |> done;", "E124")]
    [InlineData("gear f(): void {\n    var r: spawn(\"vitesse=3\") \"x\";\n}\ntrain |> done;", "E124")]                                                      // option inconnue
    [InlineData("gear f(): void {\n    var r: spawn(\"timeout=vite\") \"x\";\n}\ntrain |> done;", "E124")]
    [InlineData("gear f(): void {\n    var r: spawn(\"stdout=ailleurs\") \"x\";\n}\ntrain |> done;", "E124")]
    [InlineData("gear f(): void {\n    var r: spawn(\"stdin=pipe\") \"x\";\n}\ntrain |> done;", "")]                                                        // pas encore pris en charge
    [InlineData("gear f(int n): void {\n    var r: spawn \"x\" n;\n}\ntrain |> done;", "E003")]                                                                // un argument est une chaîne
    [InlineData("gear pure f(): void {\n    var r: spawn \"x\";\n}\ntrain |> done;", "E066")]                                                                  // pas dans un gear pure
    [InlineData("gear f(): void {\n    spawn \"x\" -j4;\n}\ntrain |> done;", "E030")]                                                                          // un drapeau nu
    public void Un_spawn_mal_utilise_est_signale(string content, string code)
    {
        var (errors, _) = Diagnose(Header + content);

        Assert.NotEmpty(errors);
        if (code.Length > 0) Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void Ignorer_le_resultat_d_un_spawn_avertit()
    {
        var (errors, warnings) = Diagnose(Header + "gear f(): void {\n    spawn \"x\" \"y\";\n}\ntrain |> done;");

        Assert.Empty(errors);
        Assert.Contains(warnings, w => w.StartsWith("W060", StringComparison.Ordinal));

        (errors, warnings) = Diagnose(Header + "gear f(): void {\n    var r: spawn \"x\";\n}\ntrain |> done;");
        Assert.Empty(errors);
        Assert.DoesNotContain(warnings, w => w.StartsWith("W060", StringComparison.Ordinal));
    }
}
