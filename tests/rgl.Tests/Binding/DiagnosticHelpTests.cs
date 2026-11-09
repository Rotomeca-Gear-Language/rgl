using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Les aides des diagnostics qui devinent l'erreur de l'écrivain.</summary>
public class DiagnosticHelpTests
{
    [Fact]
    public void Un_gear_sans_parentheses_dans_un_bras_de_match_dit_d_ecrire_l_appel()
    {
        var diagnostics = new DiagnosticBag();
        var content = "gear etom(int r): int? => null;\ngear f(int | string v): int? => match (v) { int i |> etom; string s |> null; };\ntrain |> done;";
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));

        new Binder(diagnostics).Bind(unit);

        var error = Assert.Single(diagnostics.Items, d => d.Level == DiagnosticLevel.Error);
        Assert.Contains(error.Help!, h => h.Contains("etom(…)", StringComparison.Ordinal) && h.Contains("est un gear", StringComparison.Ordinal));
    }
}
