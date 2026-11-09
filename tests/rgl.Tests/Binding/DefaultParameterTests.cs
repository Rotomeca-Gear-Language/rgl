using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>
/// Tests des paramètres par défaut (slice 4.6A) : valeurs écrites, évaluées dans le gear appelé, et valeurs de l'appelant
/// (<c>__CALLER__</c>, <c>__WHERE__</c>, <c>__FILE__</c>, <c>__LINE__</c>).
/// </summary>
public class DefaultParameterTests
{
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        var program = new Binder(diagnostics).Bind(unit);
        return (program, diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code} {d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];
    }

    private static (int Code, string Output) CompileAndRun(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        return ZigBuild.RunWithInput(executable, null, "");
    }

    private static string InGear(string declarations, string body) =>
        declarations + "\ngear test(): void {\n" + body + "\n}\n!train |> done;";

    [Theory]
    [InlineData("gear f(int a, int b: 2): int => a + b;", "var x: f(1); var y: f(1, 5);")]
    [InlineData("gear f(int a: 1, int b: a + 1): int => a + b;", "var x: f(); var y: f(3); var z: f(3, 4);")]
    [InlineData("gear f(string m, string caller: __CALLER__): string => caller;", "var x: f(\"a\"); var y: f(\"a\", \"b\");")]
    [InlineData("gear f(uint n: __LINE__, string w: __WHERE__, string f: __FILE__): uint => n;", "var x: f();")]
    [InlineData("guard f(int a, int b: 1): int { return a + b; }", "")]
    public void Un_parametre_par_defaut_peut_etre_omis(string declarations, string body)
    {
        var (program, diagnostics) = Bind(InGear(declarations, body));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Theory]
    [InlineData("gear f(int a, int b: 2): int => a + b;", "var x: f();", "E058 3:8")]                         // le premier est obligatoire
    [InlineData("gear f(int a, int b: 2): int => a + b;", "var x: f(1, 2, 3);", "E058 3:8")]                 // trop d'arguments
    [InlineData("gear f(int a: 1, int b): int => a + b;", "", "E120 1:22")]                                 // obligatoire après un défaut
    [InlineData("gear f(mut [int] a: [1]): void { }", "", "E120 1:21")]                                     // mut n'a pas de défaut
    [InlineData("gear f(int a: \"x\"): int => a;", "", "E003 1:15")]                                        // le défaut a le type du paramètre
    [InlineData("gear f(int n: __CALLER__): int => n;", "", "E120 1:15")]                                   // __CALLER__ est un string
    [InlineData("gear f(string n: __LINE__): string => n;", "", "E120 1:18")]                               // __LINE__ est un entier
    [InlineData("gear f(int a, int b: a): int => a; gear g(int a: b, int b: 1): int => a;", "", "E041 1:")]  // lit seulement les paramètres précédents
    [InlineData("gear pure f(int a: 1): int => a; gear impur(): int => 0; gear pure g(int a: impur()): int => a;", "", "E057 1:")] // un défaut pure
    public void Un_parametre_par_defaut_mal_ecrit_est_signale(string declarations, string body, string expected)
    {
        var errors = Errors(InGear(declarations, body));

        Assert.StartsWith(expected, errors[0]);
    }

    [Fact]
    public void Un_train_n_a_pas_de_parametre_par_defaut()
    {
        Assert.StartsWith("E120", Errors("!train(int a: 1) |> done;")[0]);
    }

    [Fact]
    public void Une_lambda_ne_connait_pas_les_defauts()
    {
        var errors = Errors(InGear("gear f(int a, int b: 2): int => a + b;", "(int, int) => int g: f; var x: g(1);"));

        Assert.StartsWith("E058", errors[0]);
    }

    [Fact]
    public void Les_valeurs_par_defaut_s_evaluent_dans_le_gear_et_l_appelant_donne_les_siennes()
    {
        const string program = """
            gear salut(string nom, string suffixe: "!", string fin: `{nom}?`): string => `{nom}{suffixe}{fin}`;
            gear log(string message, string caller: __CALLER__, string where: __WHERE__, uint line: __LINE__, string file: __FILE__): string => `{caller}|{where}|{line}|{file}|{message}`;
            gear relais(string m, string caller: __CALLER__): string => log(m, caller);
            gear mixte(int a, int b: a + 1, string c: __CALLER__, int d: b * 2): string => `{a} {b} {c} {d}`;
            gear nu(int a: 7): int => a;

            gear test(): void {
                `{salut("a")} {salut("a", "?")} {salut("a", "?", "x")}` -> console;
                ` ; {log("m")}` -> console;
                ` ; {relais("r")}` -> console;
                ` ; {mixte(1)} {mixte(1, 5)} {mixte(1, 5, "x")} {mixte(1, 5, "x", 0)}` -> console;
                ` ; {nu()} {nu(3)}` -> console;
                ((int) => string) f: (int a) => log("l");
                ` ; {f(1)}` -> console;
            }

            !train |> test() |> $ 0;
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(0, code);
        Assert.Equal("a!a? a?a? a?x ; test|main.rgl:9:10|9|main.rgl|m ; test|main.rgl:3:61|3|main.rgl|r"
            + " ; 1 2 test 4 1 5 test 10 1 5 x 10 1 5 x 0 ; 7 3 ; lambda|main.rgl:13:37|13|main.rgl|l", output);
    }
}
