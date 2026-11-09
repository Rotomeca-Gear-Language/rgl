using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;
using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

/// <summary>Tests de <c>soa mold</c> et des parcours de lignes <c>|S|&gt;</c> et <c>||S|&gt;</c> (slice B.1).</summary>
public class SoaRowsTests
{
    private const string Header = "soa mold Particles {\n[float] vx;\n[float] vy;\n}\ngear pure damp(float vx, float vy): float => vx * 0.5 + vy;\n";

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

    private static string InGear(string body) => $"{Header}gear f(): uint {{\n{body}\nreturn 0;\n}}\n!train |> $ f();";

    [Fact]
    public void Un_parcours_de_lignes_se_lie()
    {
        var (program, diagnostics) = Bind(InGear("var p: new Particles {vx: [1.0], vy: [2.0]};\nvar a: p |S|> damp;\nvar b: p ||S|> damp;"));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("soa mold Mauvais {\nfloat vx;\n}\n!train |> done;", "E153")]                 // un champ qui n'est pas un tableau
    [InlineData("soa mold Mauvais {\n[float;3] vx;\n}\n!train |> done;", "E153")]              // un tableau de taille fixe n'est pas une colonne
    [InlineData("soa mold Vide {\n}\n!train |> done;", "E153")]
    public void Un_soa_mold_invalide_est_refuse(string source, string code) => Assert.Contains(code, Errors(source));

    [Theory]
    [InlineData("var t: [1, 2];\nvar a: t |S|> damp;", "E153")]                                      // pas un soa mold
    [InlineData("var p: new Particles {vx: [1.0], vy: [2.0]};\nvar a: p |S|> inconnu;", "E035")]
    public void Un_parcours_de_lignes_invalide_est_refuse(string body, string code) => Assert.Contains(code, Errors(InGear(body)));

    [Fact]
    public void Un_parametre_sans_colonne_de_ce_nom_est_refuse()
    {
        Assert.Contains("E153", Errors("soa mold P {\n[float] a;\n}\ngear pure g(float b): float => b;\ngear f(): uint {\nvar p: new P {a: [1.0]};\nvar r: p |S|> g;\nreturn 0;\n}\n!train |> $ f();"));
    }

    [ZigFact]
    public void Un_parcours_de_lignes_appelle_le_gear_a_chaque_rang()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(Header
            + "gear f(): uint {\n"
            + "var p: new Particles {vx: [1.0, 2.0, 4.0], vy: [10.0, 20.0, 30.0]};\nvar r: p |S|> damp;\n"
            + "var q: new Particles {vx: [1.0, 2.0, 4.0], vy: [10.0]};\nvar s: q ||S|> damp;\n"
            + "`{r} {s}` -> console;\nreturn 0;\n}\n!train |> $ f();");

        Assert.Equal((0, "[10.5, 21.0, 32.0] [10.5]"), (code, output));
    }

    [ZigFact]
    public void Des_colonnes_de_tailles_differentes_arretent_le_programme_avec_la_stricte()
    {
        var (code, _) = CppEmitterTests.CompileAndRun(Header
            + "gear f(): uint {\nvar q: new Particles {vx: [1.0, 2.0], vy: [10.0]};\nvar r: q |S|> damp;\nreturn 0;\n}\n!train |> $ f();");

        Assert.NotEqual(0, code);
    }
}
