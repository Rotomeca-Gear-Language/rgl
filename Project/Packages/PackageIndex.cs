using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rgl.Diagnostics;

namespace Rgl.Project.Packages;

/// <summary>Un paquet de l'index de découverte.</summary>
/// <param name="Name">Son nom court : celui de son manifeste, que <c>rgl add json</c> écrit.</param>
/// <param name="Source">Son adresse (<c>github.com/alice/json</c>).</param>
/// <param name="Description">Ce qu'il fait.</param>
/// <param name="License">Sa licence.</param>
/// <param name="Version">La dernière version connue quand l'index a été construit : indicative, les versions viennent toujours des tags du dépôt.</param>
/// <param name="Keywords">Des mots que <c>rgl search</c> cherche aussi.</param>
internal sealed record IndexPackage(string Name, string Source, string? Description, string? License, string? Version, IReadOnlyList<string> Keywords);

/// <summary>Un script de l'index : un gist, figé à une révision, que <c>rgl run</c> compile puis exécute.</summary>
/// <param name="Name">Son nom, que <c>rgl run</c> écrit.</param>
/// <param name="Gist">L'identifiant du gist.</param>
/// <param name="Revision">La révision vérifiée (40 chiffres hexadécimaux) : un script publié ne change pas sous les pieds de celui qui le lance.</param>
/// <param name="Description">Ce qu'il fait.</param>
internal sealed record IndexScript(string Name, string Gist, string Revision, string? Description);

/// <summary>
/// L'index de découverte : la liste des paquets et des scripts connus, que <c>rgl search</c> lit et que <c>rgl add nom-court</c> et <c>rgl run nom</c> consultent. Un fichier JSON
/// publié par un dépôt d'index (GitHub Pages) : rien d'hébergé par RGL, et rien dont un projet dépende pour compiler — l'index ne sert qu'à trouver un nom.
/// </summary>
internal sealed partial record PackageIndex(IReadOnlyList<IndexPackage> Packages, IReadOnlyList<IndexScript> Scripts)
{
    /// <summary>Le format de fichier.</summary>
    public const string Format = "rgl-index";

    /// <summary>La version du format.</summary>
    public const int FormatVersion = 1;

    /// <summary>Un index sans rien.</summary>
    public static PackageIndex Empty { get; } = new([], []);

    /// <summary>Le paquet de ce nom court, s'il y en a un.</summary>
    public IndexPackage? FindPackage(string name) => Packages.FirstOrDefault(p => p.Name == name);

    /// <summary>Le paquet d'une adresse, s'il est dans l'index.</summary>
    public IndexPackage? FindSource(string source) => Packages.FirstOrDefault(p => string.Equals(p.Source, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>Le script de ce nom, s'il y en a un.</summary>
    public IndexScript? FindScript(string name) => Scripts.FirstOrDefault(s => s.Name == name);

    /// <summary>
    /// Lit un index. Un format, une version, un nom, une adresse, un identifiant de gist ou une révision invalides, un nom écrit deux fois : E162.
    /// </summary>
    /// <param name="text">Le JSON.</param>
    /// <param name="label">D'où il vient, pour les messages.</param>
    /// <param name="diagnostics">Reçoit les erreurs.</param>
    /// <returns>L'index ; <see langword="null"/> si une erreur a été signalée.</returns>
    public static PackageIndex? Parse(string text, string label, DiagnosticBag diagnostics)
    {
        var ok = true;
        void Fail(string message)
        {
            diagnostics.Error($"index {label} : {message}", label, DiagnosticCodes.InvalidIndex);
            ok = false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Fail("un objet JSON est attendu");
                return null;
            }
            if (!root.TryGetProperty("format", out var format) || format.GetString() != Format)
            {
                Fail($"« format » doit valoir « {Format} »");
                return null;
            }
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != FormatVersion)
            {
                Fail($"« version » doit valoir {FormatVersion} : cet index vient d'une autre version de RGL");
                return null;
            }

            var packages = new List<IndexPackage>();
            var scripts = new List<IndexScript>();
            if (root.TryGetProperty("packages", out var packageList) && packageList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in packageList.EnumerateArray())
                {
                    var name = Text(item, "name");
                    var source = Text(item, "source");
                    if (name is null || !IdentifierPattern().IsMatch(name)) { Fail($"un paquet a un nom invalide : « {name} »"); continue; }
                    string? problem = null;
                    var id = source is null ? null : PackageId.Parse(source, out problem);
                    if (id is null) { Fail($"« {name} » : adresse invalide ({problem ?? "absente"})"); continue; }
                    if (packages.Any(p => p.Name == name)) { Fail($"le paquet « {name} » est écrit deux fois"); continue; }
                    var latest = Text(item, "version");
                    if (latest is not null && !PackageVersion.IsValid(latest)) { Fail($"« {name} » : version invalide « {latest} »"); continue; }
                    var keywords = item.TryGetProperty("keywords", out var words) && words.ValueKind == JsonValueKind.Array
                        ? words.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.String).Select(w => w.GetString()!).ToList()
                        : [];
                    packages.Add(new IndexPackage(name, id.Source, Text(item, "description"), Text(item, "license"), latest, keywords));
                }
            }
            if (root.TryGetProperty("scripts", out var scriptList) && scriptList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in scriptList.EnumerateArray())
                {
                    var name = Text(item, "name");
                    var gist = Text(item, "gist");
                    var revision = Text(item, "revision");
                    if (name is null || !IdentifierPattern().IsMatch(name)) { Fail($"un script a un nom invalide : « {name} »"); continue; }
                    if (gist is null || !GistPattern().IsMatch(gist)) { Fail($"« {name} » : identifiant de gist invalide"); continue; }
                    if (revision is null || !RevisionPattern().IsMatch(revision)) { Fail($"« {name} » : la révision s'écrit avec ses 40 chiffres hexadécimaux"); continue; }
                    if (scripts.Any(s => s.Name == name)) { Fail($"le script « {name} » est écrit deux fois"); continue; }
                    scripts.Add(new IndexScript(name, gist, revision.ToLowerInvariant(), Text(item, "description")));
                }
            }
            return ok ? new PackageIndex(packages, scripts) : null;
        }
        catch (JsonException e)
        {
            Fail($"n'est pas du JSON valide : {e.Message}");
            return null;
        }
    }

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static readonly JsonSerializerOptions _writing = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Le texte de l'index : trié par nom, indenté, <c>\n</c> à la fin, le même pour le même contenu.</summary>
    public string ToText()
    {
        var packages = new JsonArray();
        foreach (var package in Packages.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var entry = new JsonObject { ["name"] = package.Name, ["source"] = package.Source };
            if (package.Description is not null) entry["description"] = package.Description;
            if (package.License is not null) entry["license"] = package.License;
            if (package.Version is not null) entry["version"] = package.Version;
            if (package.Keywords.Count > 0) entry["keywords"] = new JsonArray([.. package.Keywords.Select(k => (JsonNode?)JsonValue.Create(k))]);
            packages.Add(entry);
        }
        var scripts = new JsonArray();
        foreach (var script in Scripts.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var entry = new JsonObject { ["name"] = script.Name, ["gist"] = script.Gist, ["revision"] = script.Revision };
            if (script.Description is not null) entry["description"] = script.Description;
            scripts.Add(entry);
        }
        var root = new JsonObject { ["format"] = Format, ["version"] = FormatVersion, ["packages"] = packages, ["scripts"] = scripts };
        return root.ToJsonString(_writing) + "\n";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[0-9a-f]{20,32}$")]
    private static partial Regex GistPattern();

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex RevisionPattern();
}
