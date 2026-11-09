namespace Rgl.Tests;

/// <summary>Dossier temporaire supprimé à la fin du test.</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Chemin complet du dossier.</summary>
    public string Path { get; } = Directory.CreateTempSubdirectory("rgl-tests-").FullName;

    /// <summary>Crée un fichier dans le dossier, ainsi que ses dossiers parents.</summary>
    /// <param name="relativePath">Chemin du fichier, relatif au dossier temporaire.</param>
    /// <param name="content">Contenu du fichier.</param>
    /// <returns>Chemin complet du fichier créé.</returns>
    public string File(string relativePath, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Supprime le dossier et son contenu.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Un fichier encore verrouillé ne doit pas faire échouer le test.
        }
    }
}
