using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Compile et exécute un programme du projet qui utilise la std ; les tests de la bibliothèque s'en servent.</summary>
internal static class StdHarness
{
    /// <summary>Le corps d'un gear <c>Test</c> appelé par le train ; renvoie ce que le programme écrit.</summary>
    public static string Body(string body, string? input = null, string[]? files = null, int expectedCode = 0)
        => Main("gear Test(): void {\n" + body + "\n}\nemit Fin(): uint => 0;\n!train |> Test |> Fin;", input, expectedCode);

    /// <summary>Un fichier <c>main.rgl</c> complet.</summary>
    public static string Main(string main, string? input = null, int expectedCode = 0)
    {
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        File.WriteAllText(Path.Combine(directory.Path, "main.rgl"), main);
        var project = RglProject.Load("main.rgl", null, directory.Path, diagnostics)!;
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));
        var result = ZigBuild.RunWithInput(built.Executable!, input, "un argument");
        Assert.Equal(expectedCode, result.Code);
        return result.Output.Replace("\r\n", "\n");
    }
}
