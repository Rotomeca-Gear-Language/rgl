using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Un littéral de tableau, de dictionnaire ou de tuple prend le type de la base de l'ingot attendu, puis, constant, entre dans l'ingot.</summary>
public class IngotLiteralTests
{
    private static string[] Errors(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        new Binder(diagnostics).Bind(unit);
        return [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "?")];
    }

    [Theory]
    [InlineData("ingot line: [int;3];\nconst line Z: [1, 2, 3];\ntrain |> done;")]
    [InlineData("ingot paire: (int, string);\nconst paire P: (1, \"un\");\ntrain |> done;")]
    [InlineData("enum E { A, B }\ningot trois: [E;3];\nconst trois T: [E.A, E.B, E.A];\ntrain |> done;")]
    public void Un_litteral_constant_entre_dans_un_ingot_de_sa_forme(string content)
    {
        Assert.Empty(Errors(content));
    }

    [Fact]
    public void Un_litteral_de_la_mauvaise_taille_reste_refuse()
    {
        Assert.Contains("E086", Errors("ingot line: [int;3];\nconst line Z: [1, 2];\ntrain |> done;"));
    }

    [Fact]
    public void Un_litteral_non_constant_n_entre_pas_dans_l_ingot_sans_le_dire()
    {
        Assert.Contains("E003", Errors("ingot line: [int;3];\ngear f(int n): line => [n, 2, 3];\ntrain |> done;"));
    }
}
