using System.Text.Json.Nodes;

namespace Rgl.Lsp;

/// <summary>Le formatage du code et les actions rapides.</summary>
internal sealed partial class LanguageServer
{
    /// <summary>Le formatage du document entier, ou d'une plage de lignes : un seul changement qui remplace les lignes touchées.</summary>
    private JsonNode? _Format(JsonNode parameters, bool range)
    {
        var document = _Semantic((string)parameters["textDocument"]!["uri"]!);
        if (document is null) return new JsonArray();

        int? first = null, last = null;
        if (range)
        {
            first = (int)parameters["range"]!["start"]!["line"]!;
            last = (int)parameters["range"]!["end"]!["line"]!;
            // Une plage qui finit au début d'une ligne ne la touche pas.
            if ((int)parameters["range"]!["end"]!["character"]! == 0 && last > first) --last;
        }

        if (Formatter.Format(document, first, last) is not { } edit) return new JsonArray();

        var lines = document.Source.Content.Split('\n');
        var endsWithEol = document.Source.Content.EndsWith('\n');
        var lastIndex = endsWithEol ? lines.Length - 2 : lines.Length - 1;
        var wholeToEnd = edit.LastLine >= lastIndex;
        // La fin du dernier changement : le début de la ligne qui suit, ou la fin du texte.
        var end = wholeToEnd
            ? new JsonObject { ["line"] = lines.Length - 1, ["character"] = lines[^1].TrimEnd('\r').Length + (lines[^1].EndsWith('\r') ? 1 : 0) }
            : new JsonObject { ["line"] = edit.LastLine + 1, ["character"] = 0 };
        return new JsonArray(new JsonObject
        {
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = edit.FirstLine, ["character"] = 0 }, ["end"] = end },
            ["newText"] = edit.Text,
        });
    }

    /// <summary>Les actions rapides de la plage : importer un nom, ajouter l'en-tête, retirer les imports inutilisés.</summary>
    private JsonNode? _CodeActions(JsonNode parameters)
    {
        var uri = (string)parameters["textDocument"]!["uri"]!;
        var document = _Semantic(uri);
        if (document is null) return new JsonArray();

        var range = parameters["range"]!;
        var start = document.OffsetOf((uint)range["start"]!["line"]!, (uint)range["start"]!["character"]!);
        var end = document.OffsetOf((uint)range["end"]!["line"]!, (uint)range["end"]!["character"]!);
        var context = parameters["context"];
        var codes = (context?["diagnostics"]?.AsArray() ?? []).Select(d => (string?)d?["code"]).OfType<string>().ToHashSet();
        var only = context?["only"]?.AsArray().Select(o => (string?)o).OfType<string>().ToArray();

        return new JsonArray([.. CodeActionProvider.At(document, start, end, codes, only).Select(a => (JsonNode)new JsonObject
        {
            ["title"] = a.Title,
            ["kind"] = a.Kind,
            ["isPreferred"] = a.IsPreferred,
            ["edit"] = new JsonObject
            {
                ["changes"] = new JsonObject
                {
                    [uri] = new JsonArray([.. a.Edits.Select(e => (JsonNode)new JsonObject
                    {
                        ["range"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = (int)e.StartLine, ["character"] = (int)e.StartCharacter },
                            ["end"] = new JsonObject { ["line"] = (int)e.EndLine, ["character"] = (int)e.EndCharacter },
                        },
                        ["newText"] = e.Text,
                    })]),
                },
            },
        })]);
    }
}
