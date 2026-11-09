using Rgl.Build;
using Rgl.Build.Toolchain;

namespace Rgl.Lsp;

/// <summary>La même chaîne et le même cache de liaisons C en compilation et dans l’éditeur.</summary>
internal static class NativeEditorModel
{
    public static string CacheDirectory { get; } = Path.Combine(Cache.DefaultRoot(), "native");
    private static readonly Lazy<ICompilerToolchain?> _toolchain = new(() => ZigToolchain.Locate(Cache.DefaultRoot(), Environment.GetEnvironmentVariable("RGL_COMPILER_DIRECTORY")));
    public static ICompilerToolchain? Toolchain => _toolchain.Value;
}
