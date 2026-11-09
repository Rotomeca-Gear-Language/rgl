namespace Rgl.Build.Toolchain;

/// <summary>Capacité optionnelle d'une chaîne à produire le module WebAssembly du runtime Paper.</summary>
internal interface IWebAssemblyToolchain
{
    /// <summary>Compile une unité autonome sans libc vers <c>wasm32-freestanding</c>.</summary>
    ToolchainResult BuildWebAssembly(string source, string output, BuildProfile profile);
}
