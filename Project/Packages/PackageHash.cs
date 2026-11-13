using System.Security.Cryptography;
using System.Text;

namespace Rgl.Project.Packages;

/// <summary>
/// L'empreinte d'un paquet : un SHA-256 de l'<b>arbre extrait</b> (chemins relatifs triés, séparés par « / », avec la taille et le contenu de chaque
/// fichier), jamais de l'archive : les forges ne garantissent pas qu'une archive soit identique d'un téléchargement à l'autre, alors que son
/// contenu l'est.
/// </summary>
internal static class PackageHash
{
    /// <summary>Préfixe du hash écrit dans le lock.</summary>
    public const string Prefix = "sha256:";

    /// <summary>L'empreinte des fichiers de <paramref name="directory"/>, sous la forme <c>sha256:&lt;64 chiffres hexadécimaux&gt;</c>.</summary>
    public static string OfTree(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var files = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.FullName))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        foreach (var (relative, full) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = File.OpenRead(full);
            hash.AppendData(BitConverter.GetBytes((long)stream.Length));
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return Prefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
