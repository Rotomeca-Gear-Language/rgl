using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;
using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

/// <summary>Tests des durées (slice 5.4) : <c>100ms</c>, <c>1.5s</c>, le type <c>duration</c> et ses opérations.</summary>
public class DurationTests
{
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        return (new Binder(diagnostics).Bind(unit), diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code)];
    }

    private static string InGear(string body) => $"gear f(): uint {{\n{body}\nreturn 0;\n}}\n!train |> $ f();";

    [Theory]
    [InlineData("duration d: 100ms;\nduration e: 1.5s + 2min + 1h;\nbool b: d < e and d eq 100ms and -d < d;\nduration f: d * 3 + 2 * d - d / 4;\nlong n: e / 1s;")]
    [InlineData("duration d: 1s + 5;\nduration e: 5 + d - 2;\nint n: d as int;\nlong m: 7 as duration as long;\nduration f: n as duration;\nuint u: 3ms as uint;")]
    [InlineData("var a: 5 as string;\nvar b: 2.5 as string;\nvar c: 3ms as string;\nvar d: true as string;\nvar e: [1, 2] as string;")]
    [InlineData("var d: 250ms;\nduration e: d;\nconst duration LIMITE: 5s;\nvar t: `{d} {LIMITE}`;")]
    public void Les_durees_se_lient(string body)
    {
        var (program, diagnostics) = Bind(InGear(body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("var d: 1ms + 1.5;", "E040")]                  // un flottant n'est pas des millisecondes
    [InlineData("var d: 1ms as? string;", "E085")]
    [InlineData("var d: 5 as? duration;", "E085")]
    [InlineData("var d: \"x\" as duration;", "E085")]
    [InlineData("var d: 1ms * 2ms;", "E040")]                  // deux durées ne se multiplient pas
    [InlineData("var d: 2 / 1s;", "E040")]                     // un entier ne se divise pas par une durée
    [InlineData("var d: 1s / 0;", "E047")]                     // division par zéro connue
    [InlineData("var d: 1s / 0ms;", "E047")]
    [InlineData("duration d: 5;", "E003")]                     // un entier n'est pas une durée
    [InlineData("var d: 1.0005s;", "E126")]                    // pas un nombre entier de millisecondes
    [InlineData("var d: 9223372036854775808ms;", "E126")]      // hors des 64 bits
    [InlineData("var d: 9999999999999999h;", "E126")]
    public void Une_operation_ou_un_litteral_invalide_est_refuse(string body, string code) =>
        Assert.Contains(code, Errors(InGear(body)));

    [ZigFact]
    public void Les_durees_s_executent_et_s_ecrivent_en_millisecondes()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear calcul(): uint {\n"
            + "duration a: 1.5s;\nduration b: 2min + 250ms;\n"
            + "var t: `{a}|{b}|{b - a}|{a * 3}|{b / 2}|{b / 1s}|{-a}|{a < b}|{a eq 1500ms}|{a + 5}|{10 - a}|{a as int}|{2000 as duration}|{12 as string}`;\n"
            + "t -> console;\nreturn 7;\n}\n!train |> $ calcul();");

        Assert.Equal(7, code);
        Assert.Equal("1500ms|120250ms|118750ms|4500ms|60125ms|120|-1500ms|true|true|1505ms|-1490ms|1500|2000ms|12", output);
    }

    [ZigFact]
    public void Un_debordement_de_duree_arrete_le_programme()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear grande(duration d): duration => d * 4000000;\n"
            + "gear calcul(): uint {\nduration d: grande(1000000h);\nreturn 1;\n}\n!train |> $ calcul();");

        Assert.NotEqual(0, code);
        Assert.NotEqual(1, code);
    }
}
