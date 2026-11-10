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

        // Les blocs des layouts font partie de la page : un changement de layout change la projection.
        var layouts = PaperMarkup.LayoutTexts(_PathOf(uri), text, _ReadOpenText);
        var key = layouts.Count == 0 ? text : text + "\0" + string.Join("\0", layouts);
        if (_projections.TryGetValue(uri, out var cached) && cached.Text == key) return cached.Projection;

        var projection = PaperProjection.Build(text, layouts);
        _projections[uri] = (key, projection);
        return projection;
    }

    /// <summary>Le texte d'un fichier tel qu'il est ouvert dans l'éditeur ; <see langword="null"/> s'il ne l'est pas (le disque fait foi).</summary>
    private string? _ReadOpenText(string fullPath) => _openTexts.GetValueOrDefault(fullPath);

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
            if (diagnostic.Code is "E005" or "E013" or "E102" && projection.Reactives.Any(r => span.Text.Contains(r, StringComparison.Ordinal))) continue;
            mapped.Add(diagnostic with { Span = new SourceSpan(paperSource, RUint.CreateChecked(place.Start), RUint.CreateChecked(place.End - place.Start)) });
        }

        // Le markup : composants et layout, que le texte virtuel ne contient pas.
        foreach (var problem in PaperMarkup.Diagnose(path, text, _ReadOpenText))
        {
            mapped.Add(new Diagnostic(DiagnosticLevel.Error, problem.Message, path, null,
                new SourceSpan(paperSource, RUint.CreateChecked(problem.Start), RUint.CreateChecked(Math.Max(1, problem.End - problem.Start)))));
        }
        return new DocumentReport(paperSource, mapped, []);
    }

    /// <summary>La survol d'une balise de composant ou d'une directive <c>@layout</c> ; <see langword="null"/> ailleurs.</summary>
    private JsonNode? _PaperMarkupHover(string uri, PaperProjection projection, JsonNode position)
    {
        var path = _PathOf(uri);
        var hover = PaperMarkup.HoverAt(path, projection.PaperText, _PaperTextOffset(uri, projection, position), _ReadOpenText);
        if (hover is null) return null;
        var paper = new SourceText(path, projection.PaperText);
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = hover.Markdown },
            ["range"] = _Range(paper, new SourceSpan(paper, RUint.CreateChecked(hover.Start), RUint.CreateChecked(hover.End - hover.Start))),
        };
    }

    /// <summary>La définition d'une balise de composant, d'un de ses attributs ou d'une directive <c>@layout</c> ; <see langword="null"/> ailleurs.</summary>
    private JsonNode? _PaperMarkupDefinition(string uri, PaperProjection projection, JsonNode position)
    {
        var target = PaperMarkup.DefinitionAt(_PathOf(uri), projection.PaperText, _PaperTextOffset(uri, projection, position), _ReadOpenText);
        if (target is null) return null;
        string text;
        try { text = _ReadOpenText(Path.GetFullPath(target.Path)) ?? File.ReadAllText(target.Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        var file = new SourceText(target.Path, text);
        return new JsonObject
        {
            ["uri"] = new Uri(Path.GetFullPath(target.Path)).AbsoluteUri,
            ["range"] = _Range(file, new SourceSpan(file, RUint.CreateChecked(target.Start), RUint.CreateChecked(target.End - target.Start))),
        };
    }

    /// <summary>La place du texte virtuel qui correspond à une position du <c>.paper</c> ; <c>-1</c> hors du RGL de la page. Sans projection, la place ordinaire.</summary>
    private int _OffsetOf(string uri, SemanticDocument document, JsonNode position)
    {
        if (_ProjectionOf(uri) is not { } projection) return document.OffsetOf((uint)position["line"]!, (uint)position["character"]!);
        return _PaperOffset(uri, projection, position);
    }

    private int _PaperOffset(string uri, PaperProjection projection, JsonNode position) =>
        projection.ToVirtual(_PaperTextOffset(uri, projection, position)) ?? -1;

    /// <summary>La place du texte de la page qui correspond à une position du protocole.</summary>
    private int _PaperTextOffset(string uri, PaperProjection projection, JsonNode position)
    {
        var (line, character) = ((uint)position["line"]!, (uint)position["character"]!);
        var paper = new SourceText(_PathOf(uri), projection.PaperText);
        return line >= paper.LineStart.Count
            ? paper.Content.Length
            : Math.Min((int)paper.LineStart[(int)line].Value + (int)character, line + 1 < paper.LineStart.Count ? (int)paper.LineStart[(int)line + 1].Value : paper.Content.Length);
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
        var textOffset = _PaperTextOffset(uri, projection, position);
        var offset = projection.ToVirtual(textOffset) ?? -1;
        if (offset < 0)
        {
            // Hors du RGL : le markup (composants, layout).
            var markup = PaperMarkup.Complete(path, projection.PaperText, textOffset, _ReadOpenText);
            return markup is null ? [] : new JsonArray([.. markup.Select(_ToLsp)]);
        }

        var virtualSource = new SourceText(path, projection.VirtualText);
        var place = _Position(virtualSource, RUint.CreateChecked(offset));
        var project = _index.Open(path, file => _openTexts.GetValueOrDefault(file));
        var items = CompletionProvider.Complete(path, projection.VirtualText, (uint)(int)place["line"]!, (uint)(int)place["character"]!, project);
        return new JsonArray([.. items.Where(i => i.Edits is not { Count: > 0 }).Select(_ToLsp)]);
    }
}
