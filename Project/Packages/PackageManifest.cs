using System.Text.Json;
using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Syntax;

namespace Rgl.Project.Packages;

/// <summary>
/// Le <c>rgl.json</c> d'un paquet : <c>name</c> (l'identifiant qui sert à <c>import package</c>), <c>version</c> (<c>1.2.0</c>, la même que le tag
/// Git <c>v1.2.0</c>), <c>description</c> et <c>license</c> (facultatives) et ses propres <c>dependencies</c>. Les autres clés sont ignorées : un
/// paquet peut aussi être un projet.
/// </summary>
/// <param name="Name">Nom court du paquet, un identifiant RGL.</param>
/// <param name="Version">Version exacte.</param>
/// <param name="Description">Ce que fait le paquet, s'il le dit.</param>
/// <param name="License">Sa licence, s'il la donne.</param>
/// <param name="Dependencies">Les paquets qu'il demande : adresse → version exacte, triées par adresse.</param>
internal sealed partial record PackageManifest(string Name, string Version, string? Description, string? License, IReadOnlyDictionary<string, string> Dependencies)
{
    /// <summary>Le fichier qui dit ce que le paquet rend visible : à la racine du dépôt.</summary>
    public const string IndexFile = "index.forge";

    /// <summary>Lit le <c>rgl.json</c> d'un paquet ; <see langword="null"/> si une erreur (E157) a été signalée.</summary>
    /// <param name="path">Chemin du manifeste.</param>
    /// <param name="diagnostics">Reçoit les erreurs.</param>
    /// <param name="label">Le paquet, pour que les messages le nomment (<c>github.com/alice/json 1.2.0</c>).</param>
    public static PackageManifest? Load(string path, DiagnosticBag diagnostics, string? label = null)
    {
        var prefix = label is null ? "" : $"paquet « {label} » : ";
        if (!File.Exists(path))
        {
            diagnostics.Error($"{prefix}pas de {ProjectManifest.FileName} à la racine du dépôt", path, DiagnosticCodes.InvalidPackage);
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            var line = e.LineNumber is { } n ? $" (ligne {n + 1})" : "";
            diagnostics.Error($"{prefix}JSON invalide{line}", path, DiagnosticCodes.InvalidPackage);
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Error($"{prefix}le manifeste doit être un objet JSON", path, DiagnosticCodes.InvalidPackage);
                return null;
            }

            var ok = true;
            var name = _ReadText(root, "name", required: true, prefix, path, diagnostics, ref ok);
            var version = _ReadText(root, "version", required: true, prefix, path, diagnostics, ref ok);
            var description = _ReadText(root, "description", required: false, prefix, path, diagnostics, ref ok);
            var license = _ReadText(root, "license", required: false, prefix, path, diagnostics, ref ok);

            if (name is not null && _NameProblem(name) is { } problem)
            {
                diagnostics.Error($"{prefix}le nom « {name} » ne convient pas : {problem}", path, DiagnosticCodes.InvalidPackage);
                ok = false;
            }
            if (version is not null && !PackageVersion.IsValid(version))
            {
                diagnostics.Error($"{prefix}la version « {version} » s'écrit avec trois nombres, par exemple « 1.2.0 »", path, DiagnosticCodes.InvalidPackage);
                ok = false;
            }

            var (dependenciesOk, dependencies) = ReadDependencies(root, path, diagnostics, prefix);
            if (!ok || !dependenciesOk || name is null || version is null) return null;
            return new PackageManifest(name, version, description, license, dependencies);
        }
    }

    /// <summary>
    /// La clé <c>dependencies</c> d'un manifeste (de projet ou de paquet) : un objet qui associe l'adresse d'un paquet à sa version exacte,
    /// <c>{"github.com/alice/json": "1.2.0"}</c>. Absente, aucune dépendance. Les erreurs sont E157.
    /// </summary>
    /// <returns>Si tout est valide, et les dépendances triées par adresse.</returns>
    public static (bool Ok, IReadOnlyDictionary<string, string> Dependencies) ReadDependencies(JsonElement root, string path, DiagnosticBag diagnostics, string prefix = "")
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("dependencies", out var element)) return (true, result);

        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Error($"{prefix}la clé « dependencies » est un objet : l'adresse d'un paquet et sa version, par exemple {{\"github.com/alice/json\": \"1.2.0\"}}", path, DiagnosticCodes.InvalidPackage);
            return (false, result);
        }

        var ok = true;
        foreach (var property in element.EnumerateObject())
        {
            var id = PackageId.Parse(property.Name, out var error);
            if (id is null)
            {
                diagnostics.Error($"{prefix}dependencies : {error}", path, DiagnosticCodes.InvalidPackage);
                ok = false;
                continue;
            }

            var version = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (version is null || !PackageVersion.IsValid(version))
            {
                diagnostics.Error($"{prefix}dependencies : la version de « {property.Name} » s'écrit exactement, avec trois nombres (« 1.2.0 »), sans intervalle ni préfixe", path, DiagnosticCodes.InvalidPackage);
                ok = false;
                continue;
            }

            if (!result.TryAdd(id.Source, version))
            {
                diagnostics.Error($"{prefix}dependencies : « {id.Source} » est écrit deux fois", path, DiagnosticCodes.InvalidPackage);
                ok = false;
            }
        }
        return (ok, result);
    }

    /// <summary>Pourquoi un nom ne peut pas être celui d'un paquet ; <see langword="null"/> s'il convient.</summary>
    private static string? _NameProblem(string name)
    {
        if (!IdentifierPattern().IsMatch(name)) return "un nom de paquet est un identifiant (lettres, chiffres et « _ », sans commencer par un chiffre)";
        if (name == "std") return "« std » est le nom de la bibliothèque standard";
        if (SyntaxFacts.IsReserved(name) || !SyntaxFacts.GetKeywordKind(name).IsEmpty) return "c'est un mot réservé ou un mot-clé du langage";
        return null;
    }

    private static string? _ReadText(JsonElement root, string key, bool required, string prefix, string path, DiagnosticBag diagnostics, ref bool ok)
    {
        if (!root.TryGetProperty(key, out var value))
        {
            if (required)
            {
                diagnostics.Error($"{prefix}clé « {key} » absente", path, DiagnosticCodes.InvalidPackage);
                ok = false;
            }
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
        {
            diagnostics.Error($"{prefix}la clé « {key} » doit être une chaîne non vide", path, DiagnosticCodes.InvalidPackage);
            ok = false;
            return null;
        }
        return text;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
