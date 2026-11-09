using System.Reflection;
using Rgl.Diagnostics;

namespace Rgl.Paper;

/// <summary>Installe le petit client Paper qui applique les patchs reçus par WebSocket.</summary>
internal static class PaperBrowserRuntime
{
    private const string Resource = "Rgl.Paper.paper.js";

    public static bool CopyTo(string outputDirectory, DiagnosticBag diagnostics)
    {
        try
        {
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
                ?? throw new InvalidOperationException($"ressource embarquée absente : {Resource}");
            Directory.CreateDirectory(outputDirectory);
            using var target = File.Create(Path.Combine(outputDirectory, "rgl-paper.js"));
            source.CopyTo(target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            diagnostics.Error($"impossible d'écrire le runtime navigateur Paper : {error.Message}", outputDirectory);
            return false;
        }
    }
}
