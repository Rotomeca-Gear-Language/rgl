using Rgl.Cli;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary><c>compile --explain-optimizations</c> : ce que le compilateur optimise, ou refuse d'optimiser, et pourquoi (slice B.4e).</summary>
public class OptimizationReportTests
{
    private const string Source = """
        gear pure f(int i): int => (i * 7 + 3) % 1000;
        gear pure g(int x): int => x * 2 + 1;

        gear pure somme(long acc, int n): long {
            if (n eq 0) { return acc; }
            return somme(acc + n, n - 1);
        }

        gear pure fib(int n): long {
            if (n < 2) { return n; }
            return fib(n - 1) + fib(n - 2);
        }

        emit fin(): uint {
            [int] a: [0..<10] ||> f;
            [int] b: a ||> g;
            [int] d: [0..<3] ||> f;
            [int] e: d ||> g;
            long t: d.length + e.length + b.length;
            [int] none: [];
            const [int] all: [1, 2, 3] |+> none with ([int] acc, int v) => [...acc, v];
            return (t + somme(0, 5) + fib(5) + all.length) as uint;
        }

        !train |> fin;
        """;

    private static (int Exit, string Out, string Error) Run()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "rgl.json"), "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\"}");
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), Source);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliRunner.Run(["compile", "-d", ".", "--explain-optimizations"], new CliContext(output, error, project.Path, cache.Path));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Le_rapport_dit_ce_qui_est_optimise()
    {
        var (exit, output, error) = Run();

        Assert.Equal(CliRunner.Success, exit);
        Assert.Contains("Analyse des optimisations terminée", output);
        Assert.Contains("I004", error);
        Assert.Contains("deux parcours fondus en une boucle : « a » n'est plus construit", error);
        Assert.Contains("récursion terminale écrite en boucle", error);
        Assert.Contains("l'accumulateur est reçu par valeur et déplacé", error);
        Assert.Contains("reprend le bloc du tableau étalé", error);
    }

    [Fact]
    public void Le_rapport_dit_ce_qui_est_refuse_et_pourquoi()
    {
        var (_, _, error) = Run();

        Assert.Contains("W070", error);
        Assert.Contains("les parcours de « d » et de « e » restent deux boucles", error);
        Assert.Contains("« d » est relue plus loin", error);
        Assert.Contains("la récursion reste une récursion", error);
        Assert.Contains("aucun appel récursif n'est un « return f(…); » direct", error);
    }
}
