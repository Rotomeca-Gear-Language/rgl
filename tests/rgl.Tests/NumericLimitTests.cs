using Rgl.Binding;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary><c>int.min</c>, <c>int.max</c>… : les bornes des types numériques.</summary>
public class NumericLimitTests
{
    private static string Run(string body)
    {
        var main = "gear Test(): void {\n" + body + "\n}\nemit Fin(): uint => 0;\n!train |> Test |> Fin;";
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        File.WriteAllText(Path.Combine(directory.Path, "main.rgl"), main);
        var project = RglProject.Load("main.rgl", null, directory.Path, diagnostics)!;
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));
        var result = ZigBuild.RunWithInput(built.Executable!, null, "");
        Assert.Equal(0, result.Code);
        return result.Output.Replace("\r\n", "\n");
    }

    [ZigFact]
    public void Les_bornes_des_entiers_et_des_flottants()
    {
        var output = Run("""
            std.writeLine(int.max);
            std.writeLine(int.min);
            std.writeLine(uint.max);
            std.writeLine(uint.min);
            std.writeLine(long.max);
            std.writeLine(long.min);
            std.writeLine(ulong.max);
            std.writeLine(byte.min);
            std.writeLine(ubyte.max);
            std.writeLine(short.min);
            std.writeLine(ushort.max);
            std.writeLine(double.max > 1.0e300);
            std.writeLine(double.min < -1.0e300);
            std.writeLine(float.max > 1.0e38);
            var plafond: int.max;
            std.writeLine(plafond eq 2147483647);
            ulong cinq: 5;
            std.writeLine(ulong.max > cinq);
            """);

        Assert.Equal("2147483647\n-2147483648\n4294967295\n0\n9223372036854775807\n-9223372036854775808\n18446744073709551615\n-128\n255\n-32768\n65535\ntrue\ntrue\ntrue\ntrue\ntrue\n", output);
    }
}
