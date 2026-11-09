using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Les compteurs de mémoire de la mesure : <c>--count-allocations</c> et le bilan que le runtime écrit en s'arrêtant (slice B.4a).</summary>
public class MemoryCountersTests
{
    [Fact]
    public void Le_profil_a_compteurs_a_son_dossier_son_cache_et_l_option_du_runtime()
    {
        var counted = BuildProfile.Release.WithCounters();

        Assert.Equal("release-compteurs", counted.Name);
        Assert.Contains("-DRGL_COUNT_ALLOCATIONS", counted.CompileFlags);
        Assert.NotEqual(BuildProfile.Release.Key, counted.Key);            // ses objets du runtime ne se mêlent pas à ceux de release
        Assert.Equal(ProfileKind.Release, counted.Kind);
    }

    [Fact]
    public void L_option_est_lue_par_la_ligne_de_commande()
    {
        var diagnostics = new Rgl.Diagnostics.DiagnosticBag();

        var line = CommandLineParser.Parse(["compile", "main.rgl", "--release", "--count-allocations"], diagnostics);

        Assert.True(line!.CountAllocations);
        Assert.Empty(diagnostics.Items);
    }

    [ZigFact]
    public void Le_programme_compte_ses_blocs_et_ses_copies_profondes()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.CompileWith(output.Path, "counters_tests", warningsAsErrors: true, ["-DRGL_COUNT_ALLOCATIONS"],
            Path.Combine(ZigBuild.Runtime, "tests", "counters_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);

        Assert.Equal(0, code);
        var report = text.Split('\n').Last(line => line.StartsWith("rgl-compteurs :", StringComparison.Ordinal));
        Assert.Contains("copies_tableaux=1 ", report);
        Assert.Contains("elements_copies=10 ", report);
        Assert.Contains("copies_textes=1 ", report);
        Assert.Contains("octets_textes=61", report);
        Assert.DoesNotContain("allocations=0 ", report);
    }

    [ZigFact]
    public void Sans_l_option_le_programme_n_ecrit_rien()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "counters_tests_plain", warningsAsErrors: true, Path.Combine(ZigBuild.Runtime, "tests", "counters_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);

        Assert.Equal(0, code);
        Assert.DoesNotContain("rgl-compteurs", text);
    }
}
