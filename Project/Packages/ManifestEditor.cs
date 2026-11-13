using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rgl.Diagnostics;

namespace Rgl.Project.Packages;

/// <summary>
/// Modifie la clé <c>dependencies</c> d'un <c>rgl.json</c> (de projet ou de paquet) sans toucher aux autres : l'ordre des clés est gardé, le texte est
/// réécrit indenté sur quatre espaces comme celui que crée <c>rgl init</c>, les dépendances triées par adresse.
/// </summary>
internal static class ManifestEditor
{
    private static readonly JsonSerializerOptions _writing = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        NewLine = "\n",
        // Garde les accents lisibles.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Écrit <paramref name="dependencies"/> comme clé <c>dependencies</c> de <paramref name="path"/> ; la clé disparaît quand il n'y en a plus.
    /// </summary>
    /// <returns>Faux (et une erreur) si le fichier ne se lit pas ou ne s'écrit pas.</returns>
    public static bool SetDependencies(string path, IReadOnlyDictionary<string, string> dependencies, DiagnosticBag diagnostics)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            if (root is null)
            {
                diagnostics.Error("rgl.json doit être un objet JSON", path, DiagnosticCodes.InvalidPackage);
                return false;
            }

            if (dependencies.Count == 0) root.Remove("dependencies");
            else
            {
                var sorted = new JsonObject();
                foreach (var (address, version) in dependencies.OrderBy(d => d.Key, StringComparer.Ordinal)) sorted[address] = version;
                root["dependencies"] = sorted;
            }

            File.WriteAllText(path, root.ToJsonString(_writing) + "\n", new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Error($"rgl.json n'a pas pu être modifié : {e.Message}", path, DiagnosticCodes.InvalidPackage);
            return false;
        }
    }
}
