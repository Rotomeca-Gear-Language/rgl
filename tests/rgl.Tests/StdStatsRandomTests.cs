using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Statistiques sur des tableaux de nombres et compléments de Random.</summary>
public class StdStatsRandomTests
{
    [ZigFact]
    public void Les_statistiques_d_un_tableau()
    {
        var output = StdHarness.Body("""
            const [int] values: [2, 4, 4, 4, 5, 5, 7, 9];
            std.writeLine(values.average() ?? -1.0);
            std.writeLine(values.median() ?? -1.0);
            std.writeLine(values.variance() ?? -1.0);
            std.writeLine(values.standardDeviation() ?? -1.0);
            std.writeLine(values.variance(true) ?? -1.0);
            std.writeLine(values.range() ?? -1.0);
            std.writeLine(values.percentile(0.0) ?? -1.0);
            std.writeLine(values.percentile(25.0) ?? -1.0);
            std.writeLine(values.percentile(100.0) ?? -1.0);
            const [int] odd: [3, 1, 2];
            std.writeLine(odd.median() ?? -1.0);
            const [double] none: [];
            std.writeLine(none.median() eq null);
            std.writeLine(none.standardDeviation() eq null);
            const [int] single: [5];
            std.writeLine(single.variance(true) eq null);
            std.writeLine(single.variance() ?? -1.0);
            std.writeLine(values.length);
            """);

        Assert.Equal("5.0\n4.5\n4.0\n2.0\n4.571428571428571\n7.0\n2.0\n4.0\n9.0\n2.0\ntrue\ntrue\ntrue\n0.0\n8\n", output);
    }

    [ZigFact]
    public void Le_hasard_gaussien_borne_et_d_entropie()
    {
        var output = StdHarness.Main("""
            use <Random> only {Random};

            gear Test(): void {
            mut Random random: Random.seed(42);
            const [double] samples: [0..<4000] ||> (i) => random.nextGaussian();
            const double mean: samples.average() ?? 99.0;
            const double deviation: samples.standardDeviation() ?? 99.0;
            std.writeLine(mean > -0.1 and mean < 0.1);
            std.writeLine(deviation > 0.9 and deviation < 1.1);
            mut Random scaled: Random.seed(7);
            const [double] wide: [0..<2000] ||> (i) => scaled.nextGaussian(100.0, 15.0);
            std.writeLine((wide.average() ?? 0.0) > 98.0 and (wide.average() ?? 0.0) < 102.0);
            mut Random ranged: Random.seed(3);
            const [double] inside: [0..<500] ||> (i) => ranged.nextDouble(-2.5, 4.0);
            std.writeLine((inside.min() ?? 0.0) >= -2.5 and (inside.max() ?? 0.0) < 4.0);
            mut Random first: Random.fromEntropy();
            mut Random second: Random.fromEntropy();
            std.writeLine(first.next() eq second.next());
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\ntrue\ntrue\ntrue\nfalse\n", output);
    }
}
