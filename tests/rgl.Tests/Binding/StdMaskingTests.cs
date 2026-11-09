using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Types;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Un nom que l'utilisateur déclare masque celui de la std, avec un avertissement (W015) ; le compilateur garde ses types d'erreur standard par identité.</summary>
public class StdMaskingTests
{
    private static (IrProgram? Program, DiagnosticBag Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        return (new Binder(diagnostics).Bind(unit), diagnostics);
    }

    [Theory]
    [InlineData("error CastError { string detail: 'x'; }")]
    [InlineData("error Exception { int code: 1; }")]
    [InlineData("mold LoopError { int tours; }")]
    public void Un_type_d_erreur_standard_peut_etre_masque_avec_un_avertissement(string declaration)
    {
        var (program, diagnostics) = Bind(declaration + "\ntrain |> done;");

        Assert.NotNull(program);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.StdNameMasked);
    }

    [Fact]
    public void Les_erreurs_du_compilateur_restent_celles_de_la_std_meme_si_l_utilisateur_declare_les_siennes()
    {
        var (program, _) = Bind("error CastError { string detail: 'x'; }\nguard lire(string texte): int { return texte as! int; }\n!train |> done;");

        Assert.NotNull(program);
        var guard = program!.Gears.Single(g => g.Symbol.Name == "lire").Symbol;
        var error = Assert.IsType<MoldType>(guard.Errors);
        Assert.Same(program.CastError, error.Symbol);
        Assert.Equal("std.Errors.CastError", error.Symbol.QualifiedName);
    }

    [Fact]
    public void Le_nom_court_et_std_nom_designent_l_erreur_standard()
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), """
            gear f(std.CastError e): string => e.message;
            gear g(Exception e): string => e.message;
            train |> done;
            """);
        var modules = Rgl.Project.SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);

        Assert.True(program is not null, string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
    }
}
