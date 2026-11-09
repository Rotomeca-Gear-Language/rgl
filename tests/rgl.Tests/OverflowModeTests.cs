using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le mode de débordement par gear (<c>@checked</c>, <c>@unchecked</c>) et la vérification groupée des parcours (slice B.1).</summary>
public class OverflowModeTests
{
    [ZigFact]
    public void Un_gear_unchecked_roule_dans_un_programme_checked()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "@unchecked\ngear pure enroule(int a, int b): int => a + b;\n"
            + "gear f(): uint {\n`{enroule(2147483647, 1)}` -> console;\nreturn 0;\n}\n!train |> $ f();");

        Assert.Equal((0, "-2147483648"), (code, output));
    }

    [ZigFact]
    public void Un_gear_checked_arrete_dans_un_programme_qui_roule()
    {
        var (code, _) = CppEmitterTests.CompileAndRun(
            "@checked\ngear pure strict(int a, int b): int => a + b;\n"
            + "gear f(int n): uint {\nvar x: strict(2147483647, n);\nreturn 0;\n}\n!train |> $ f(1);");

        Assert.NotEqual(0, code);
    }

    [ZigFact]
    public void Un_debordement_dans_un_parcours_arrete_toujours_le_programme_meme_verifie_en_groupe()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear f(): uint {\nvar xs: [0..<100000] ||> (i) => i;\nvar ys: xs ||> (i) => i * 100000;\n`{ys.length}` -> console;\nreturn 0;\n}\n!train |> $ f();");

        Assert.NotEqual(0, code);
        Assert.DoesNotContain("100000", output);
    }

    [ZigFact]
    public void Un_parcours_sans_debordement_donne_le_meme_resultat_verifie_en_groupe()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(
            "gear f(): uint {\nvar xs: [0..<5] ||> (i) => i;\nvar ys: xs ||> (i) => i * 3 + 7 - 1;\n`{ys}` -> console;\nreturn 0;\n}\n!train |> $ f();");

        Assert.Equal((0, "[6, 9, 12, 15, 18]"), (code, output));
    }

    [Fact]
    public void Un_parcours_de_lambdas_sans_effet_teste_le_debordement_une_fois_apres_la_boucle()
    {
        var cpp = CppEmitterTests.Emit("gear f(): uint {\nvar xs: [0..<5] ||> (i) => i;\nvar ys: xs ||> (i) => i * 3;\n`{ys}` -> console;\nreturn 0;\n}\n!train |> $ f();");

        Assert.Contains("grouped_mul<std::int32_t>", cpp);
        Assert.Contains("check_grouped_overflow(", cpp);
        Assert.Contains(".push_reserved(", cpp);
    }
}
