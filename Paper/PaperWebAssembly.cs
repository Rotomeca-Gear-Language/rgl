using Rgl.Build.Toolchain;
using Rgl.Diagnostics;

namespace Rgl.Paper;

/// <summary>Construit le noyau WebAssembly versionné que le runtime navigateur Paper charge.</summary>
internal static class PaperWebAssembly
{
    private const string Source = """
        extern "C" unsigned rgl_paper_abi() { return 1u; }
        """;

    public static bool Build(ICompilerToolchain toolchain, string intermediateDirectory, string outputDirectory, BuildProfile profile, DiagnosticBag diagnostics)
    {
        if (toolchain is not IWebAssemblyToolchain wasm)
        {
            diagnostics.Error($"la chaîne « {toolchain.Name} » ne sait pas produire WebAssembly", outputDirectory);
            return false;
        }
        var source = Path.Combine(intermediateDirectory, "rgl-paper.cpp");
        var output = Path.Combine(outputDirectory, "rgl-paper.wasm");
        File.WriteAllText(source, Source);
        var result = wasm.BuildWebAssembly(source, output, profile);
        if (result.Success) return true;
        diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "le noyau WebAssembly Paper ne compile pas", source,
            Help: result.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8).ToArray()));
        return false;
    }
}
