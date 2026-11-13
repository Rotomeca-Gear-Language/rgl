using System.Text;
using System.Text.Json;
using Rgl.Diagnostics;

namespace Rgl.Project.Packages;

/// <summary>Un paquet figé dans <c>rgl.lock</c>.</summary>
/// <param name="Source">Son adresse.</param>
/// <param name="Version">Sa version exacte.</param>
/// <param name="Name">Son nom court (le <c>name</c> de son manifeste).</param>
/// <param name="Hash">L'empreinte de son contenu (voir <see cref="PackageHash"/>).</param>
/// <param name="Dependencies">Les adresses des paquets qu'il demande, triées ; leurs versions sont celles des autres entrées du lock.</param>
/// <param name="Trusted">Vrai si <c>package-trust.binaries</c> autorisait le paquet : ses fichiers binaires (<c>.dll</c>, <c>.a</c>…) sont alors dans l'arbre dont <paramref name="Hash"/> est l'empreinte.</param>
internal sealed record LockedPackage(string Source, string Version, string Name, string Hash, IReadOnlyList<string> Dependencies, bool Trusted = false);

/// <summary>
/// <c>rgl.lock</c>, à côté du manifeste du projet et à versionner : chaque paquet du programme (dépendances de dépendances comprises) avec sa version
/// exacte et l'empreinte de son contenu. Un tag déplacé ou une archive modifiée ne passe plus. Le fichier est du JSON indenté, trié par adresse, avec
/// des fins de ligne <c>\n</c> : le même lock donne toujours le même texte.
/// </summary>
internal sealed class PackageLock
{
    /// <summary>Nom du fichier, à la racine du projet.</summary>
    public const string FileName = "rgl.lock";

    private const string Format = "rgl-lock";
    private const int FormatVersion = 1;

    private readonly Dictionary<string, LockedPackage> _bySource;

    /// <summary>Un lock qui contient ces paquets, rangés par adresse.</summary>
    public PackageLock(IEnumerable<LockedPackage> packages)
    {
        Packages = [.. packages.OrderBy(p => p.Source, StringComparer.Ordinal)];
        _bySource = Packages.ToDictionary(p => p.Source, StringComparer.Ordinal);
    }

    /// <summary>Les paquets figés, triés par adresse.</summary>
    public IReadOnlyList<LockedPackage> Packages { get; }

    /// <summary>Le paquet d'adresse <paramref name="source"/>, ou <see langword="null"/>.</summary>
    public LockedPackage? this[string source] => _bySource.GetValueOrDefault(source);

    /// <summary>Lit un lock ; <see langword="null"/> si le fichier est illisible (E157), jamais écrasé dans ce cas.</summary>
    public static PackageLock? Read(string path, DiagnosticBag diagnostics)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("format").GetString() != Format
                || root.GetProperty("version").GetInt32() != FormatVersion)
                return _Invalid(path, diagnostics, $"ce n'est pas un lock {Format} version {FormatVersion}");

            var packages = new List<LockedPackage>();
            foreach (var item in root.GetProperty("packages").EnumerateArray())
            {
                var source = item.GetProperty("source").GetString()!;
                if (PackageId.Parse(source, out _) is null || packages.Any(p => p.Source == source))
                    return _Invalid(path, diagnostics, $"« {source} » n'est pas une adresse de paquet, ou est écrit deux fois");
                var version = item.GetProperty("version").GetString()!;
                var hash = item.GetProperty("hash").GetString()!;
                if (!PackageVersion.IsValid(version) || !hash.StartsWith(PackageHash.Prefix, StringComparison.Ordinal))
                    return _Invalid(path, diagnostics, $"l'entrée de « {source} » est invalide");
                packages.Add(new LockedPackage(source, version, item.GetProperty("name").GetString()!, hash,
                    [.. item.GetProperty("dependencies").EnumerateArray().Select(d => d.GetString()!)],
                    item.TryGetProperty("trusted", out var trusted) && trusted.ValueKind == JsonValueKind.True));
            }
            return new PackageLock(packages);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return _Invalid(path, diagnostics, e is JsonException ? "JSON invalide" : "une clé manque ou a le mauvais type");
        }
    }

    private static PackageLock? _Invalid(string path, DiagnosticBag diagnostics, string reason)
    {
        diagnostics.Error($"{FileName} illisible : {reason} (supprime-le pour qu'il soit recréé)", path, DiagnosticCodes.InvalidPackage);
        return null;
    }

    /// <summary>Le texte du fichier : indenté de deux espaces, un retour à la ligne final.</summary>
    public string ToText()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("version", FormatVersion);
            writer.WriteStartArray("packages");
            foreach (var package in Packages)
            {
                writer.WriteStartObject();
                writer.WriteString("source", package.Source);
                writer.WriteString("version", package.Version);
                writer.WriteString("name", package.Name);
                writer.WriteString("hash", package.Hash);
                writer.WriteStartArray("dependencies");
                foreach (var dependency in package.Dependencies.Order(StringComparer.Ordinal)) writer.WriteStringValue(dependency);
                writer.WriteEndArray();
                if (package.Trusted) writer.WriteBoolean("trusted", true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Écrit le lock (UTF-8 sans BOM) sous un nom temporaire puis le renomme : un échec ne laisse jamais un fichier à moitié écrit.</summary>
    public void Write(string path)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, ToText(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
