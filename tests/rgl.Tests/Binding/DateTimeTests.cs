using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;
using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

/// <summary>Tests des dates (slice 5.4) : le type <c>datetime</c>, ses opérations, <c>as</c> et <c>&lt;- clock</c>.</summary>
public class DateTimeTests
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
    [InlineData("datetime a: 0 as datetime;\ndatetime b: a + 2h;\nduration d: b - a;\ndatetime c: 1s + b - 5min;\nbool ok: a < b and b >= a and a eq a and a not eq b;")]
    [InlineData("var a <- clock;\ndatetime b: a + 1h;\nvar ms: b as long;\nvar t: `{a}|{b}`;\nvar s: a as string;")]
    [InlineData("datetime a: 5 as datetime;\nint n: a as int;\ndatetime? maybe: null;\nmaybe <- a;")]
    public void Les_dates_se_lient(string body)
    {
        var (program, diagnostics) = Bind(InGear(body));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("datetime a: 0 as datetime;\nvar b: a + a;", "E040")]               // deux dates ne s'additionnent pas
    [InlineData("datetime a: 0 as datetime;\nvar b: a + 5;", "E040")]               // pas d'entier : une durée
    [InlineData("datetime a: 0 as datetime;\nvar b: 5s - a;", "E040")]              // durée - date n'existe pas
    [InlineData("datetime a: 0 as datetime;\nvar b: a * 2;", "E040")]
    [InlineData("datetime a: 5;", "E003")]                                          // un entier n'est pas une date
    [InlineData("datetime a: 5s;", "E003")]
    [InlineData("var a: 0 as? datetime;", "E085")]
    [InlineData("var a: \"x\" as datetime;", "E085")]
    public void Une_operation_invalide_sur_une_date_est_refusee(string body, string code) =>
        Assert.Contains(code, Errors(InGear(body)));

    [Fact]
    public void L_horloge_n_est_pas_permise_dans_un_gear_pure()
    {
        Assert.Contains("E066", Errors("gear pure f(): uint {\nvar a <- clock;\nreturn 0;\n}\n!train |> $ f();"));
    }

    [ZigFact]
    public void Les_dates_s_ecrivent_en_iso_8601_utc()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear calcul(): uint {\n"
            + "datetime epoque: 0 as datetime;\n"
            + "datetime a: 1700000000000 as datetime;\n"
            + "datetime bissextile: 951782400000 as datetime;\n"
            + "var t: `{epoque}|{a}|{a + 1ms}|{a - 1h}|{epoque - 1ms}|{bissextile}|{bissextile + 24h}|{a - epoque}|{1700000000000 as datetime as long}`;\n"
            + "t -> console;\nreturn 7;\n}\n!train |> $ calcul();");

        Assert.Equal(7, code);
        Assert.Equal("1970-01-01T00:00:00Z|2023-11-14T22:13:20Z|2023-11-14T22:13:20.001Z|2023-11-14T21:13:20Z|1969-12-31T23:59:59.999Z|2000-02-29T00:00:00Z|2000-03-01T00:00:00Z|1700000000000ms|1700000000000", output);
    }

    [ZigFact]
    public void L_horloge_donne_l_instant_present()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear calcul(): uint {\n"
            + "var a <- clock;\nvar b <- clock;\n"
            + "bool recent: a as long > 1700000000000 and b >= a and b - a < 1min;\n"
            + "recent -> console;\nreturn 4;\n}\n!train |> $ calcul();");

        Assert.Equal(4, code);
        Assert.Equal("true", output);
    }

    [ZigFact]
    public void Une_date_hors_de_0_a_9999_s_ecrit_avec_son_signe()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear calcul(): uint {\n"
            + "datetime loin: 253402300800000 as datetime;\n"
            + "datetime avant: -62167219200001 as datetime;\n"
            + "var t: `{loin}|{avant}`;\nt -> console;\nreturn 1;\n}\n!train |> $ calcul();");

        Assert.Equal(1, code);
        Assert.Equal("+010000-01-01T00:00:00Z|-000001-12-31T23:59:59.999Z", output);
    }
}
