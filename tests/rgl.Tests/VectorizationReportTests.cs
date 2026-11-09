using Rgl.Build;
using Rgl.Diagnostics;

namespace Rgl.Tests;

/// <summary>Les remarques de vectorisation de Clang, rendues en diagnostics RGL (<c>--explain-vectorization</c>).</summary>
public class VectorizationReportTests
{
    private static (DiagnosticBag Bag, string Source) Report(Func<string, string> log)
    {
        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "main.rgl");
        File.WriteAllText(source, "var a: 1;\nvar r: p |S|> damp;\nvar b: 2;\n");
        // Le C++ généré : la ligne 2 du source tient dans les lignes 5 à 8 du C++.
        var generated = $"// entête\nint x;\n#line 1 \"{source.Replace("\\", "\\\\")}\"\nint a;\n#line 2 \"{source.Replace("\\", "\\\\")}\"\nfor (;;) {{\n  body;\n}}\n#line 3 \"{source.Replace("\\", "\\\\")}\"\nint b;\n";
        var bag = new DiagnosticBag();
        VectorizationReport.Report(log(Path.Combine(directory.Path, "vectorization", "main.cpp")), generated, bag);
        return (bag, source);
    }

    [Fact]
    public void Une_boucle_non_vectorisee_est_un_avertissement_a_la_ligne_de_son_instruction_avec_sa_raison()
    {
        var (bag, _) = Report(cpp =>
            $"{cpp}:7:3: remark: loop not vectorized: unsafe dependent memory operations in loop. Use #pragma clang loop distribute(enable) [-Rpass-analysis=loop-vectorize]\n"
            + $"{cpp}:7:3: remark: loop not vectorized [-Rpass-missed=loop-vectorize]\n"
            + "E:\\runtime\\include\\rgl\\standard\\array.hpp:271:1: remark: loop not vectorized: call instruction cannot be vectorized [-Rpass-analysis=loop-vectorize]\n");

        var warning = Assert.Single(bag.Items);
        Assert.Equal((DiagnosticCodes.LoopNotVectorized, DiagnosticLevel.Warning), (warning.Code, warning.Level));
        Assert.Equal(2, (int)warning.Span!.Value.StartPosition.Line);
        Assert.Contains(warning.Help!, h => h.Contains("recouvrir"));
        Assert.DoesNotContain(warning.Help!, h => h == "loop not vectorized");
    }

    [Fact]
    public void Une_boucle_vectorisee_est_une_notice_avec_sa_largeur()
    {
        var (bag, _) = Report(cpp => $"{cpp}:6:3: remark: vectorized loop (vectorization width: 8, interleaved count: 2) [-Rpass=loop-vectorize]\n");

        var notice = Assert.Single(bag.Items);
        Assert.Equal((DiagnosticCodes.LoopVectorized, DiagnosticLevel.Info), (notice.Code, notice.Level));
        Assert.Contains("8 éléments", notice.Message);
    }
}
