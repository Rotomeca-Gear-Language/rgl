using System.Text.Json.Nodes;
using Rgl.Diagnostics;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Lsp;

/// <summary>
/// Les fichiers <c>.paper</c> : le RGL de leurs blocs et de leurs interpolations est lu comme un fichier forge virtuel (<see cref="PaperProjection"/>), dont
/// les positions sont ramenées à celles de la page. Diagnostics, complétion, survol, aide à la saisie et définition y répondent ; le reste (plan, coloration
/// sémantique, formatage…) n'a pas de sens pour une page HTML et ne répond rien.
/// </summary>
internal sealed partial class LanguageServer
{
    private readonly Dictionary<string, (string Text, PaperProjection Projection)> _projections = new(StringComparer.Ordinal);

    /// <summary>Les demandes qu'un <c>.paper</c> comprend ; les autres reçoivent une réponse vide.</summary>
    private static readonly HashSet<string> _paperRequests =
    [
        "textDocument/completion", "textDocument/hover", "textDocument/definition", "textDocument/signatureHelp",
    ];

    /// <summary>La projection du <c>.paper</c> ouvert à cette adresse ; <see langword="null"/> si ce n'est pas un <c>.paper</c> ou s'il n'est pas ouvert.</summary>
    private PaperProjection? _ProjectionOf(string uri)
    {
        if (!PaperProjection.IsPaper(_PathOf(uri)) || !_documents.TryGetValue(uri, out var text)) return null;
        if (_projections.TryGetValue(uri, out var cached) && cached.Text == text) return cached.Projection;

        var projection = PaperProjection.Build(text);
        _projections[uri] = (text, projection);
        return projection;
    }

    /// <summary>Vrai pour une demande sur un <c>.paper</c> que le serveur ne traite pas.</summary>
    private static bool _IgnoredForPaper(string? method, JsonNode? parameters) =>
        method is not null && parameters?["textDocument"]?["uri"] is { } uri && !_paperRequests.Contains(method)
        && (method.StartsWith("textDocument/", StringComparison.Ordinal)) && method is not ("textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose")
        && PaperProjection.IsPaper(_PathOf((string)uri!));

    /// <summary>Les diagnostics du RGL d'une page : l'analyse du texte virtuel, ramenée aux positions de la page.</summary>
    private DocumentReport _AnalyzePaper(string uri, string text)
    {
        var path = _PathOf(uri);
        var projection = _ProjectionOf(uri)!;
        var report = DocumentAnalysis.Analyze(path, projection.VirtualText, _openTexts);
        var paperSource = new SourceText(path, text);

        var mapped = new List<Diagnostic>();
        foreach (var diagnostic in report.Diagnostics)
        {
            // Un autre fichier : son diagnostic reste tel quel. Un diagnostic sans place, ou dans du code que la projection a ajouté, n'est pas celui de la page.
            if (diagnostic.Span is not { } span) continue;
            if (!string.Equals(_FullPath(span.Source.Path), _FullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                mapped.Add(diagnostic);
                continue;
            }

            if (projection.ToPaper((int)span.Start.Value, (int)span.End.Value) is not { } place) continue;
            // L'état reactive est une constante dans le texte virtuel : lui écrire n'est pas une erreur dans la page.
            if (diagnostic.Code is "E005" or "E013" && projection.Reactives.Any(r => span.Text.Contains(r, StringComparison.Ordinal))) continue;
            mapped.Add(diagnostic with { Span = new SourceSpan(paperSource, RUint.CreateChecked(place.Start), RUint.CreateChecked(place.End - place.Start)) });
        }
        return new DocumentReport(paperSource, mapped, []);
    }

    /// <summary>La place du texte virtuel qui correspond à une position du <c>.paper</c> ; <c>-1</c> hors du RGL de la page. Sans projection, la place ordinaire.</summary>
    private int _OffsetOf(string uri, SemanticDocument document, JsonNode position)
    {
        if (_ProjectionOf(uri) is not { } projection) return document.OffsetOf((uint)position["line"]!, (uint)position["character"]!);
        return _PaperOffset(uri, projection, position);
    }

    private int _PaperOffset(string uri, PaperProjection projection, JsonNode position)
    {
        var (line, character) = ((uint)position["line"]!, (uint)position["character"]!);
        var paper = new SourceText(_PathOf(uri), projection.PaperText);
        var offset = line >= paper.LineStart.Count
            ? paper.Content.Length
            : Math.Min((int)paper.LineStart[(int)line].Value + (int)character, line + 1 < paper.LineStart.Count ? (int)paper.LineStart[(int)line + 1].Value : paper.Content.Length);
        return projection.ToVirtual(offset) ?? -1;
    }

    /// <summary>La plage de la page qui correspond à ce morceau du document ; <see langword="null"/> si elle n'est pas dans le RGL de la page.</summary>
    private JsonObject? _RangeOf(string uri, SemanticDocument document, SourceSpan span)
    {
        if (_ProjectionOf(uri) is not { } projection) return _Range(document.Source, span);
        if (projection.ToPaper((int)span.Start.Value, (int)span.End.Value) is not { } place) return null;

        var paper = new SourceText(_PathOf(uri), projection.PaperText);
        return _Range(paper, new SourceSpan(paper, RUint.CreateChecked(place.Start), RUint.CreateChecked(place.End - place.Start)));
    }

    /// <summary>La complétion dans le RGL d'une page : celle du texte virtuel, sans l'import automatique (il écrirait dans le texte virtuel).</summary>
    private JsonArray _CompletePaper(string uri, PaperProjection projection, JsonNode position)
    {
        var path = _PathOf(uri);
        var offset = _PaperOffset(uri, projection, position);
        if (offset < 0) return [];

        var virtualSource = new SourceText(path, projection.VirtualText);
        var place = _Position(virtualSource, RUint.CreateChecked(offset));
        var project = _index.Open(path, file => _openTexts.GetValueOrDefault(file));
        var items = CompletionProvider.Complete(path, projection.VirtualText, (uint)(int)place["line"]!, (uint)(int)place["character"]!, project);
        return new JsonArray([.. items.Where(i => i.Edits is not { Count: > 0 }).Select(_ToLsp)]);
    }
}
