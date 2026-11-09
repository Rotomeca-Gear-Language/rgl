using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rgl.Build.Toolchain;

namespace Rgl.Native;

/// <summary>
/// Le cache des liaisons d'un fichier <c>forge: native</c> : le texte RGL engendré, rangé sous une clé qui change dès que quelque chose qui le détermine change.
/// </summary>
/// <remarks>
/// La clé est l'empreinte du contenu de chaque en-tête et de tout ce qu'il inclut (la liste vient de Clang : <c>-MM</c>), de la chaîne de compilation
/// (sa version fixe celle de Clang), de la cible (<c>long</c> n'a pas la même taille partout), du nom du fichier et du compilateur lui-même, dont une
/// nouvelle version peut écrire d'autres liaisons. Un en-tête du système ne figure pas dans la liste : la version de la chaîne de compilation le
/// représente. Le cache ne sert qu'à aller plus vite : s'il manque, est illisible ou ne s'écrit pas, les liaisons sont refaites.
/// </remarks>
internal static partial class NativeCache
{
    /// <summary>Les fichiers que la lecture d'un en-tête suppose ; <see langword="null"/> si Clang ne les donne pas.</summary>
    public static IReadOnlyList<string>? Dependencies(ICompilerToolchain toolchain, string header, IReadOnlyList<string> includeDirectories)
    {
        string? text = null;
        var result = toolchain.ListHeaderDependencies(header, includeDirectories, stream =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            text = reader.ReadToEnd();
        });
        return result.Success && text is not null ? Parse(text) : null;
    }

    /// <summary>
    /// Les fichiers d'une règle de Makefile (<c>cible.o: a.h b.h \</c>…) : la suite des lignes jointes après le premier « : » suivi d'un blanc,
    /// séparés par des blancs, un blanc précédé d'une barre oblique inverse faisant partie du nom.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string makefile)
    {
        var joined = ContinuedLine().Replace(makefile, " ");
        var colon = ColonThenBlank().Match(joined);
        if (!colon.Success) return [];

        List<string> files = [];
        var current = new StringBuilder();
        var rest = joined[(colon.Index + colon.Length)..];
        for (var i = 0; i < rest.Length; ++i)
        {
            if (rest[i] == '\\' && i + 1 < rest.Length && rest[i + 1] == ' ')
            {
                current.Append(' ');
                ++i;
            }
            else if (char.IsWhiteSpace(rest[i]))
            {
                if (current.Length > 0) files.Add(current.ToString());
                current.Clear();
            }
            else current.Append(rest[i]);
        }
        if (current.Length > 0) files.Add(current.ToString());
        return files;
    }

    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex ContinuedLine();

    [GeneratedRegex(@":\s")]
    private static partial Regex ColonThenBlank();

    /// <summary>La clé des liaisons de ces en-têtes ; <see langword="null"/> si un fichier dont elles dépendent ne se lit pas (la lecture signalera alors l'erreur).</summary>
    /// <param name="toolchain">La chaîne de compilation.</param>
    /// <param name="headers">Les chemins complets des en-têtes du fichier <c>forge: native</c>.</param>
    /// <param name="includeDirectories">Les dossiers où les en-têtes cherchent ce qu'ils incluent.</param>
    /// <param name="fileName">Le nom du fichier <c>forge: native</c>, qui figure dans la description des liaisons.</param>
    public static string? Key(ICompilerToolchain toolchain, IReadOnlyList<string> headers, IReadOnlyList<string> includeDirectories, string fileName)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"rgl-native|{toolchain.Name}|{toolchain.Target}|{typeof(NativeCache).Assembly.ManifestModule.ModuleVersionId}|{fileName}|{headers.Count}"));

        foreach (var header in headers)
        {
            if (Dependencies(toolchain, header, includeDirectories) is not { } files) return null;

            // Le premier fichier est l'en-tête lui-même ; chaque contenu est précédé de sa taille, pour qu'on ne confonde pas deux découpages.
            hash.AppendData(Encoding.UTF8.GetBytes($"|{files.Count}"));
            foreach (var file in files)
            {
                byte[] content;
                try
                {
                    content = File.ReadAllBytes(file);
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
                hash.AppendData(Encoding.UTF8.GetBytes($"|{content.Length}|"));
                hash.AppendData(content);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
    }

    /// <summary>Ce que le cache garde des liaisons d'un fichier <c>forge: native</c>.</summary>
    /// <param name="Summary">Le bilan des liaisons.</param>
    /// <param name="OtherDeclarations">Le nombre de déclarations que les liaisons ne connaissent pas encore.</param>
    /// <param name="Unconverted">Ce que les liaisons n'ont pas converti, avec la raison.</param>
    /// <param name="Source">Le texte RGL des liaisons.</param>
    public sealed record Entry(string Summary, int OtherDeclarations, IReadOnlyList<CBindings.Unconverted> Unconverted, string Source);

    /// <summary>Les liaisons rangées sous <paramref name="key"/> ; <see langword="null"/> s'il n'y en a pas ou si le fichier est abîmé.</summary>
    public static Entry? Lookup(string directory, string key)
    {
        try
        {
            var path = Path.Combine(directory, key + ".rgl");
            if (!File.Exists(path)) return null;

            // Première ligne : « // », le nombre de déclarations non lues, une tabulation et le bilan ; puis une ligne « //~ nom, tabulation, raison » par
            // déclaration non convertie ; le reste est le texte des liaisons.
            var lines = File.ReadAllText(path, Encoding.UTF8).Split('\n');
            if (lines.Length < 2 || !lines[0].StartsWith("// ", StringComparison.Ordinal)) return null;

            var head = lines[0][3..].Split('\t', 2);
            if (head.Length != 2 || !int.TryParse(head[0], out var others)) return null;

            List<CBindings.Unconverted> unconverted = [];
            var next = 1;
            for (; next < lines.Length && lines[next].StartsWith("//~ ", StringComparison.Ordinal); ++next)
            {
                var parts = lines[next][4..].Split('\t', 2);
                if (parts.Length != 2) return null;
                unconverted.Add(new CBindings.Unconverted(parts[0], parts[1]));
            }
            return new Entry(head[1], others, unconverted, string.Join('\n', lines[next..]));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Range les liaisons sous <paramref name="key"/> ; un cache qui ne s'écrit pas est ignoré.</summary>
    public static void Store(string directory, string key, string summary, int otherDeclarations, IReadOnlyList<CBindings.Unconverted> unconverted, string source)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Écrit dans un fichier temporaire puis le renomme : deux builds simultanés ne voient jamais un fichier à moitié écrit.
            var path = Path.Combine(directory, key + ".rgl");
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                var header = new StringBuilder("// ").Append(otherDeclarations).Append('\t').Append(summary).Append('\n');
                foreach (var item in unconverted) header.Append("//~ ").Append(item.Name).Append('\t').Append(item.Reason.Replace('\n', ' ')).Append('\n');
                File.WriteAllText(temporary, header + source, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
