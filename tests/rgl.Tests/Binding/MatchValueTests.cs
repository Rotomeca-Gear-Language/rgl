using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Tests du match de valeurs (slice 4.7) : <c>match (n) { case 1, 2 |&gt; …; case eval $ &gt; 10 |&gt; …; default |&gt; …; }</c>.</summary>
public class MatchValueTests
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

    private const string Direction = "enum Direction { North, South, East }";

    [Theory]
    [InlineData("", "int n: 3; var s: match (n) { case 1, 2 |> \"petit\"; case eval $ > 10 |> \"grand\"; default |> \"autre\"; };")]
    [InlineData("", "string t: \"a\"; int r: match (t) { case \"a\" |> 1; case \"b\", \"c\" |> 2; default |> 0; };")]
    [InlineData("", "bool b: true; int r: match (b) { case true |> 1; case false |> 0; };")]
    [InlineData(Direction, "Direction d: Direction.North; int r: match (d) { case North |> 1; case South, East |> 2; };")]
    [InlineData(Direction, "Direction d: Direction.North; int r: match (d) { case North |> 1; default |> 0; };")]
    [InlineData("", "int n: 3; match (n) { case 1 |> { `un` -> console; } default |> { `autre` -> console; } }")]
    public void Un_match_de_valeurs_se_lie(string declarations, string body)
    {
        var (program, diagnostics) = Bind(InGear(declarations, body));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Theory]
    [InlineData("int n: 3; int r: match (n) { case 1 |> 1; case 2 |> 2; };", "E082 3:")]                                // sans default, les entiers ne sont pas couverts
    [InlineData("int n: 3; int r: match (n) { case 1 |> 1; case eval $ > 0 |> 2; };", "E082 3:")]                        // une condition ne prouve rien
    [InlineData("bool b: true; int r: match (b) { case true |> 1; };", "E082 3:")]                                        // false manque
    [InlineData("Direction d: Direction.North; int r: match (d) { case North |> 1; case South |> 2; };", "E082 3:")]      // East manque
    [InlineData("int n: 3; int r: match (n) { case 1 |> 1; case 1 |> 2; default |> 0; };", "E052 3:")]                    // même valeur deux fois
    [InlineData("int n: 3; int m: 4; int r: match (n) { case m |> 1; default |> 0; };", "E051 3:")]                       // valeur non constante
    [InlineData("int n: 3; int r: match (n) { case \"a\" |> 1; default |> 0; };", "E040 3:")]                             // valeur d'un autre type
    [InlineData("int n: 3; int r: match (n) { default |> 1; default |> 2; };", "E081 3:")]                                // deux default
    [InlineData("int n: 3; int r: match (n) { case eval $ + 1 |> 1; default |> 0; };", "E040 3:")]                        // la condition est un bool
    [InlineData("int n: 3; int r: match (n) { int i |> 1; default |> 0; };", "E080 3:")]                                  // un bras de type sur un entier
    [InlineData("int | string v: 1; int r: match (v) { case 1 |> 1; default |> 0; };", "E080 3:")]                        // case sur une union
    [InlineData("int n: 3; string s: match (n) { case 1 |> \"a\"; default |> 2; };", "E003 3:")]                          // bras sans type commun
    public void Un_match_de_valeurs_mal_ecrit_est_signale(string body, string expected)
    {
        var errors = Errors(InGear(Direction, body));

        Assert.StartsWith(expected, errors[0]);
    }

    [Fact]
    public void Un_match_de_valeurs_qui_retourne_dans_chaque_bras_termine_le_gear()
    {
        const string program = """
            gear nom(int n): string {
                match (n) {
                    case 1 |> return "un";
                    case 2 |> return "deux";
                    default |> return "autre";
                }
            }
            !train |> done;
            """;

        var (result, diagnostics) = Bind(program);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(result);
    }

    [Fact]
    public void Un_match_de_valeurs_compile_et_s_execute()
    {
        const string program = """
            enum Direction { North, South, East }

            gear taille(int n): string => match (n) { case 1, 2 |> "petit"; case eval $ > 10 |> "grand"; default |> "moyen"; };
            gear cap(Direction d): int => match (d) { case North |> 1; case South, East |> 2; };
            gear oui(bool b): string => match (b) { case true |> "oui"; case false |> "non"; };
            gear nom(string s): int => match (s) { case "a" |> 1; case "b", "c" |> 2; default |> 0; };

            gear test(): void {
                `{taille(1)} {taille(2)} {taille(5)} {taille(50)}` -> console;
                ` ; {cap(Direction.North)} {cap(Direction.East)} {oui(true)} {oui(false)}` -> console;
                ` ; {nom("a")} {nom("c")} {nom("z")}` -> console;
                int n: 7;
                match (n) {
                    case 7 |> { ` ; sept` -> console; }
                    default |> { ` ; autre` -> console; }
                }
            }

            !train |> test() |> $ 0;
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(0, code);
        Assert.Equal("petit petit moyen grand ; 1 2 oui non ; 1 2 0 ; sept", output);
    }
}
