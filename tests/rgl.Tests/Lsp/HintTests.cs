using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Les indications grisées de l'éditeur : type déduit d'un <c>var</c> et points de suspension.</summary>
public class HintTests
{
    private const string Text = "@async gear un(): int => 1;\n"
        + "@async gear deux(): int {\n    var a: await un();\n    int b: 3;\n    var c: 4;\n    return 2;\n}\n"
        + "!train |> done;";

    [Fact]
    public void Un_var_montre_son_type_deduit_et_pas_un_type_ecrit()
    {
        var report = DocumentAnalysis.Analyze("indices.rgl", Text);

        var types = report.Hints.Where(h => h.IsType).Select(h => h.Label).ToArray();
        Assert.Equal([": int ! AsyncException", ": int"], types);
    }

    [Fact]
    public void Un_await_est_signale_comme_point_de_suspension()
    {
        var report = DocumentAnalysis.Analyze("indices.rgl", Text);

        var suspension = Assert.Single(report.Hints, h => !h.IsType);
        Assert.Contains("suspend", suspension.Label);
        Assert.Equal("await un()", Text.Substring((int)suspension.Offset.Start.Value, (int)suspension.Offset.Length.Value));
    }

    [Fact]
    public void Un_document_avec_une_erreur_n_a_aucune_indication()
    {
        Assert.Empty(DocumentAnalysis.Analyze("indices.rgl", "gear f(): int => faux;\n!train |> done;").Hints);
    }
}
