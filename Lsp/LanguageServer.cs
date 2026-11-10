using System.Reflection;
using System.Text.Json.Nodes;
using Rgl.Diagnostics;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>
/// Le serveur de langage de <c>rgl lsp</c> : l'éditeur lui envoie les documents ouverts et leurs
/// modifications, il renvoie leurs diagnostics à chaque changement, ses propositions
/// d'autocomplétion, et les indications écrites dans le texte (<c>mut</c> grisé devant l'argument
/// d'un paramètre <c>mut</c>). Il échange avec l'éditeur par l'entrée et la sortie standard, un
/// message après l'autre ; il ne compile pas le programme ; les liaisons C utilisent le cache du compilateur.
/// </summary>
internal sealed partial class LanguageServer
{
    /// <summary>Le document est envoyé en entier à chaque modification.</summary>
    private const int FullSynchronization = 1;

    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;

    private readonly JsonRpcChannel _channel;

    /// <summary>Le texte de chaque document ouvert, par son URI.</summary>
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

    /// <summary>La dernière analyse de chaque document ouvert, par son URI.</summary>
    private readonly Dictionary<string, DocumentReport> _reports = new(StringComparer.Ordinal);

    /// <summary>Vrai après la demande <c>shutdown</c> : la notification <c>exit</c> termine alors proprement.</summary>
    private bool _shutdown;
    private bool _manageStandardLibrary;

    /// <summary>Les fichiers du projet, lus pour proposer les noms à importer et décrire ce qu'on survole.</summary>
    private readonly ProjectIndex _index = new();

    /// <summary>Le texte de chaque document ouvert, par son chemin : un fichier importé qui est ouvert est lu tel que l'éditeur le montre.</summary>
    private readonly Dictionary<string, string> _openTexts = new(StringComparer.OrdinalIgnoreCase);

    private LanguageServer(JsonRpcChannel channel) => _channel = channel;

    /// <summary>Sert l'éditeur jusqu'à sa notification <c>exit</c> ou la fin de l'entrée.</summary>
    /// <returns>Le code de sortie : 0 après un arrêt demandé, 1 sinon.</returns>
    public static int Run(Stream input, Stream output) => new LanguageServer(new JsonRpcChannel(input, output))._Serve();

    /// <summary>Le temps sans nouvelle frappe après lequel les diagnostics d'un document modifié sont recalculés.</summary>
    private const int AnalysisDelay = 150;

    /// <summary>Les documents modifiés dont les diagnostics restent à calculer.</summary>
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    /// <summary>
    /// Lit les messages sur un fil à part pour que l'analyse d'un document n'empêche pas de répondre à une demande de complétion ou de survol
    /// déjà arrivée : une modification ne fait que retenir le texte, et ses diagnostics se calculent quand l'éditeur a cessé d'écrire.
    /// </summary>
    private int _Serve()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<JsonObject?>();
        var reader = new Thread(() =>
        {
            try
            {
                while (_channel.Read() is { } incoming) queue.Add(incoming);
            }
            catch (Exception)
            {
                // Une entrée illisible ou fermée : comme la fin de l'entrée.
            }
            finally
            {
                queue.CompleteAdding();
            }
        }) { IsBackground = true };
        reader.Start();

        while (true)
        {
            JsonObject? message;
            if (_dirty.Count == 0)
            {
                if (!queue.TryTake(out message, Timeout.Infinite)) break;
            }
            else if (!queue.TryTake(out message, AnalysisDelay))
            {
                _AnalyzeDirty();
                if (queue.IsCompleted) break;
                continue;
            }

            var method = (string?)message["method"];
            var id = message["id"]?.DeepClone();
            if (method == "exit") return _shutdown ? 0 : 1;

            try
            {
                var result = _Handle(method, message["params"]);
                if (id is not null) _channel.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (Exception exception)
            {
                // Rien n'arrête le serveur : une demande reçoit l'erreur, une notification n'a pas de réponse.
                if (id is not null) _RespondError(id, exception is MissingMethodException ? MethodNotFound : InternalError, exception.Message);
            }
        }

        _AnalyzeDirty();
        return _shutdown ? 0 : 1;
    }

    /// <summary>Calcule et publie les diagnostics des documents modifiés depuis la dernière analyse.</summary>
    private void _AnalyzeDirty()
    {
        foreach (var uri in _dirty.ToArray())
        {
            _dirty.Remove(uri);
            if (_documents.TryGetValue(uri, out var text)) _Update(uri, text);
        }
    }

    /// <summary>Traite une demande ou une notification ; renvoie le résultat d'une demande.</summary>
    /// <exception cref="MissingMethodException">Pour une méthode que le serveur ne connaît pas.</exception>
    private JsonNode? _Handle(string? method, JsonNode? parameters)
    {
        // Les demandes sémantiques ne doivent pas relancer une extraction à chaque frappe.
        if (_manageStandardLibrary && !Project.StdLibrary.IsAvailableCached()
            && (method?.StartsWith("textDocument/", StringComparison.Ordinal) == true
                && method is not ("textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose")
                || method == "rgl/dataset")) return null;

        if (_IgnoredForPaper(method, parameters)) return null;

        switch (method)
        {
            case "initialize":
                _manageStandardLibrary = (bool?)parameters?["initializationOptions"]?["manageStandardLibrary"] ?? false;
                return _Initialize();

            case "rgl/standardLibrary":
                var ready = (bool?)parameters?["restore"] == true
                    ? Project.StdLibrary.Restore() : Project.StdLibrary.IsAvailable();
                return new JsonObject { ["available"] = ready, ["path"] = Project.StdLibrary.CacheDirectory };

            case "shutdown":
                _shutdown = true;
                return null;

            case "textDocument/didOpen":
                _Update((string)parameters!["textDocument"]!["uri"]!, (string)parameters["textDocument"]!["text"]!);
                return null;

            case "textDocument/didChange":
                var changes = parameters!["contentChanges"]!.AsArray();
                var changed = (string)parameters["textDocument"]!["uri"]!;
                _documents[changed] = (string)changes[^1]!["text"]!;
                _openTexts[_FullPath(changed)] = _documents[changed];
                _dirty.Add(changed);
                return null;

            case "workspace/didChangeWatchedFiles":
                foreach (var (uri, text) in _documents.ToArray()) _Update(uri, text);
                return null;

            case "textDocument/didClose":
                var closed = (string)parameters!["textDocument"]!["uri"]!;
                _documents.Remove(closed);
                _dirty.Remove(closed);
                _projections.Remove(closed);
                _reports.Remove(closed);
                _openTexts.Remove(_FullPath(closed));
                _Publish(closed, new JsonArray());
                return null;

            case "textDocument/completion":
                return _Complete(parameters!);

            case "textDocument/inlayHint":
                return _InlayHints(parameters!);

            case "textDocument/hover":
                return _Hover(parameters!);

            case "textDocument/definition":
                return _Definition(parameters!);

            case "textDocument/signatureHelp":
                return _SignatureHelp(parameters!);

            case "textDocument/semanticTokens/full":
                return _SemanticTokens(parameters!);

            case "textDocument/documentSymbol":
                return _DocumentSymbols(parameters!);

            case "textDocument/references":
                return _References(parameters!);

            case "textDocument/documentHighlight":
                return _DocumentHighlight(parameters!);

            case "textDocument/prepareRename":
                return _PrepareRename(parameters!);

            case "textDocument/rename":
                return _Rename(parameters!);

            case "textDocument/foldingRange":
                return _FoldingRanges(parameters!);

            case "textDocument/selectionRange":
                return _SelectionRanges(parameters!);

            case "textDocument/typeDefinition":
                return _TypeDefinition(parameters!);

            case "workspace/symbol":
                return _WorkspaceSymbols(parameters!);

            case "rgl/moldSkeleton":
                return _MoldSkeleton(parameters!);

            case "textDocument/prepareCallHierarchy":
                return _PrepareCallHierarchy(parameters!);

            case "callHierarchy/incomingCalls":
                return _CallHierarchy(parameters!, incoming: true);

            case "callHierarchy/outgoingCalls":
                return _CallHierarchy(parameters!, incoming: false);

            case "textDocument/documentLink":
                return _DocumentLinks(parameters!);

            case "textDocument/formatting":
                return _Format(parameters!, range: false);

            case "textDocument/rangeFormatting":
                return _Format(parameters!, range: true);

            case "textDocument/codeAction":
                return _CodeActions(parameters!);

            case "rgl/dataset":
                return _DatasetEditor(parameters!);

            case null or "initialized" or "$/cancelRequest" or "$/setTrace" or "workspace/didChangeConfiguration":
                return null;

            default:
                throw new MissingMethodException($"méthode inconnue : {method}");
        }
    }

    /// <summary>
    /// Ce que le serveur sait faire : documents synchronisés en entier, autocomplétion déclenchée aussi
    /// par <c>.</c>, indications dans le texte.
    /// </summary>
    private static JsonObject _Initialize()
    {
        var version = typeof(LanguageServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["experimental"] = new JsonObject { ["standardLibraryRecovery"] = true },
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = FullSynchronization },
                ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".", "<", "/"), ["resolveProvider"] = false },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ","), ["retriggerCharacters"] = new JsonArray(",") },
                ["inlayHintProvider"] = true,
                ["documentSymbolProvider"] = true,
                ["referencesProvider"] = true,
                ["documentHighlightProvider"] = true,
                ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                ["foldingRangeProvider"] = true,
                ["selectionRangeProvider"] = true,
                ["typeDefinitionProvider"] = true,
                ["workspaceSymbolProvider"] = true,
                ["callHierarchyProvider"] = true,
                ["documentLinkProvider"] = new JsonObject { ["resolveProvider"] = false },
                ["documentFormattingProvider"] = true,
                ["documentRangeFormattingProvider"] = true,
                ["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix", "source.organizeImports") },
                ["semanticTokensProvider"] = new JsonObject
                {
                    ["legend"] = new JsonObject
                    {
                        ["tokenTypes"] = new JsonArray([.. SemanticTokensProvider.Types.Select(t => (JsonNode)t)]),
                        ["tokenModifiers"] = new JsonArray([.. SemanticTokensProvider.Modifiers.Select(t => (JsonNode)t)]),
                    },
                    ["full"] = true,
                },
            },
            // La date de compilation distingue deux builds de même version : l'éditeur l'affiche, pour savoir quel serveur répond.
            ["serverInfo"] = new JsonObject { ["name"] = "rgl", ["version"] = version.Split('+')[0] + " · build " + File.GetLastWriteTime(typeof(LanguageServer).Assembly.Location).ToString("yyyy-MM-dd HH:mm") },
        };
    }

    /// <summary>Les fichiers autres que le document, où l'analyse d'un document a signalé quelque chose : à vider quand ce n'est plus vrai.</summary>
    private readonly Dictionary<string, HashSet<string>> _otherPublications = new(StringComparer.Ordinal);

    /// <summary>
    /// Retient le nouveau texte d'un document et publie ses diagnostics. Ceux qui visent un autre fichier (un fichier importé, la std) sont
    /// publiés sur ce fichier, avec ses propres positions : ils ne se mélangent pas à ceux du document, dont les positions ne leur
    /// conviennent pas. Une analyse qui échoue se voit : un diagnostic d'erreur interne, jamais un silence.
    /// </summary>
    private void _Update(string uri, string text)
    {
        _documents[uri] = text;
        _openTexts[_FullPath(uri)] = text;

        DocumentReport report;
        try
        {
            report = _manageStandardLibrary && !Project.StdLibrary.IsAvailableCached()
                ? new DocumentReport(new SourceText(_PathOf(uri), text),
                    [new Diagnostic(DiagnosticLevel.Warning, "Bibliothèque standard RGL absente ou incomplète. Restaurez-la depuis l’extension pour reprendre l’analyse.", Code: "RGL001")], [])
                : PaperProjection.IsPaper(_PathOf(uri)) ? _AnalyzePaper(uri, text) : DocumentAnalysis.Analyze(_PathOf(uri), text, _openTexts);
        }
        catch (Exception exception)
        {
            var failed = new SourceText(_PathOf(uri), text);
            report = new DocumentReport(failed, [new Diagnostic(DiagnosticLevel.Error, $"erreur interne de l'analyse de {_PathOf(uri)} : {exception.Message} ({exception.GetType().Name})", Code: "RGL000")], []);
        }
        _reports[uri] = report;

        var own = new JsonArray();
        var others = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var diagnostic in report.Diagnostics)
        {
            if (diagnostic.Span is { } span && !string.Equals(_FullPath(span.Source.Path), _FullPath(report.Source.Path), StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(span.Source.Path))
            {
                var otherUri = new Uri(span.Source.Path).AbsoluteUri;
                if (!others.TryGetValue(otherUri, out var list)) others[otherUri] = list = [];
                list.Add(_ToLsp(diagnostic, span.Source));
            }
            else own.Add(_ToLsp(diagnostic, diagnostic.Span?.Source ?? report.Source));
        }
        _Publish(uri, own);

        foreach (var stale in _otherPublications.GetValueOrDefault(uri) ?? [])
        {
            if (!others.ContainsKey(stale)) _Publish(stale, new JsonArray());
        }
        foreach (var (otherUri, list) in others) _Publish(otherUri, list);
        _otherPublications[uri] = [.. others.Keys];
    }

    /// <summary>
    /// Les indications de la plage demandée : <c>mut</c> devant chaque argument passé à un paramètre
    /// <c>mut</c>, que l'éditeur affiche grisé (genre « paramètre »).
    /// </summary>
    private JsonArray _InlayHints(JsonNode parameters)
    {
        if (!_reports.TryGetValue((string)parameters["textDocument"]!["uri"]!, out var report)) return [];

        var (first, last) = ((int)parameters["range"]!["start"]!["line"]!, (int)parameters["range"]!["end"]!["line"]!);
        var hints = new JsonArray();
        foreach (var span in report.MutArguments)
        {
            var position = _Position(report.Source, span.Start);
            var line = (int)position["line"]!;
            if (line < first || line > last) continue;
            hints.Add(new JsonObject { ["position"] = position, ["label"] = "mut", ["kind"] = 2, ["paddingRight"] = true });
        }
        // Le type déduit d'un var s'écrit après son nom (genre « type »), l'indication de suspension devant l'expression.
        foreach (var hint in report.Hints)
        {
            var position = _Position(report.Source, hint.IsType ? hint.Offset.End : hint.Offset.Start);
            var line = (int)position["line"]!;
            if (line < first || line > last) continue;
            hints.Add(hint.IsType
                ? new JsonObject { ["position"] = position, ["label"] = hint.Label, ["kind"] = 1 }
                : new JsonObject { ["position"] = position, ["label"] = hint.Label, ["paddingRight"] = true });
        }
        return hints;
    }


    private void _Publish(string uri, JsonArray diagnostics) =>
        _channel.Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics },
        });

    /// <summary>Le chemin complet d'un document, pour retrouver son texte parmi les documents ouverts.</summary>
    private static string _FullPath(string uri)
    {
        var path = _PathOf(uri);
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
    }

    /// <summary>Le document ouvert lu pour l'éditeur, avec les fichiers de son projet ; <see langword="null"/> s'il n'est pas ouvert.</summary>
    private SemanticDocument? _Semantic(string uri)
    {
        if (!_documents.TryGetValue(uri, out var text)) return null;

        var path = _PathOf(uri);
        var project = _index.Open(path, file => _openTexts.GetValueOrDefault(file));
        return SemanticDocument.Create(path, _ProjectionOf(uri)?.VirtualText ?? text, project);
    }

    private static JsonObject _Range(SourceText source, SourceSpan span) =>
        new() { ["start"] = _Position(source, span.Start), ["end"] = _Position(source, span.End) };

    /// <summary>Les propositions d'autocomplétion à la position demandée.</summary>
    private JsonArray _Complete(JsonNode parameters)
    {
        var uri = (string)parameters["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var text)) return [];

        var position = parameters["position"]!;
        if (_ProjectionOf(uri) is { } projection) return _CompletePaper(uri, projection, position);
        var path = _PathOf(uri);
        var project = _index.Open(path, file => _openTexts.GetValueOrDefault(file));
        var items = CompletionProvider.Complete(path, text, (uint)position["line"]!, (uint)position["character"]!, project);
        return new JsonArray([.. items.Select(_ToLsp)]);
    }

    private static JsonNode _ToLsp(CompletionItem item)
    {
        var result = new JsonObject { ["label"] = item.Label, ["kind"] = (int)item.Kind, ["detail"] = item.Detail };
        if (item.Documentation is { } documentation) result["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
        if (item.SortText is { } sort) result["sortText"] = sort;
        if (item.InsertText is { } insert)
        {
            result["insertText"] = insert;
            result["insertTextFormat"] = 2;
        }
        if (item.Deprecated) result["tags"] = new JsonArray(1);
        if (item.Command is { } command) result["command"] = new JsonObject { ["title"] = "Suggérer", ["command"] = command };
        if (item.Source is { } source) result["labelDetails"] = new JsonObject { ["description"] = source };
        if (item.Edits is { Count: > 0 } edits)
        {
            result["additionalTextEdits"] = new JsonArray([.. edits.Select(e => (JsonNode)new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = (int)e.Line, ["character"] = (int)e.Character },
                    ["end"] = new JsonObject { ["line"] = (int)e.Line, ["character"] = (int)e.Character },
                },
                ["newText"] = e.Text,
            })]);
        }
        return result;
    }

    /// <summary>Le survol : ce que désigne le mot sous le curseur.</summary>
    private JsonNode? _Hover(JsonNode parameters)
    {
        var uri = (string)parameters["textDocument"]!["uri"]!;
        var document = _Semantic(uri);
        if (document is null) return null;

        if (_ProjectionOf(uri) is { } paper && _PaperMarkupHover(uri, paper, parameters["position"]!) is { } markup) return markup;
        var offset = _OffsetOf(uri, document, parameters["position"]!);
        if (offset < 0 || HoverProvider.At(document, offset) is not { } hover) return null;
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = hover.Markdown },
            ["range"] = _RangeOf(uri, document, hover.Range),
        };
    }

    /// <summary>L'accès à la définition : l'endroit où se déclare ce que désigne le mot sous le curseur.</summary>
    private JsonNode? _Definition(JsonNode parameters)
    {
        var uri = (string)parameters["textDocument"]!["uri"]!;
        var document = _Semantic(uri);
        if (document is null) return null;

        if (_ProjectionOf(uri) is { } paper && _PaperMarkupDefinition(uri, paper, parameters["position"]!) is { } markup) return markup;
        var offset = _OffsetOf(uri, document, parameters["position"]!);
        if (offset < 0 || document.TargetAt(offset)?.Symbol is not { Module: { } module } symbol) return null;

        var span = symbol.Kind == RglSymbolKind.Module ? new SourceSpan(module.Source, default, default) : symbol.NameSpan;
        var target = module == document.Self ? uri : new Uri(module.Path).AbsoluteUri;
        var range = module == document.Self ? _RangeOf(uri, document, span) : _ModuleRange(module, span);
        return range is null ? null : new JsonObject { ["uri"] = target, ["range"] = range };
    }

    /// <summary>Le plan du document : ses déclarations, avec leurs champs, variantes et gears.</summary>
    private JsonNode? _DocumentSymbols(JsonNode parameters)
    {
        var document = _Semantic((string)parameters["textDocument"]!["uri"]!);
        if (document is null) return new JsonArray();

        JsonNode Convert(DocumentSymbolEntry entry)
        {
            var result = new JsonObject
            {
                ["name"] = entry.Name,
                ["kind"] = entry.Kind,
                ["range"] = _Range(document.Source, entry.Range),
                // Le nom doit être dans la déclaration, sinon l'éditeur refuse l'élément.
                ["selectionRange"] = _Range(document.Source, entry.Selection.Start.Value >= entry.Range.Start.Value && entry.Selection.End.Value <= entry.Range.End.Value ? entry.Selection : entry.Range),
                ["children"] = new JsonArray([.. entry.Children.Select(Convert)]),
            };
            if (entry.Detail is { } detail) result["detail"] = detail;
            return result;
        }
        return new JsonArray([.. DocumentSymbolProvider.Of(document).Select(Convert)]);
    }

    /// <summary>
    /// <c>rgl/dataset</c> (requête propre à RGL, pour l'éditeur de dataset de l'extension) : le dataset déclaré à la ligne donnée, avec ses colonnes, ses entrées et
    /// l'endroit à remplacer ; <c>null</c> s'il n'y en a pas.
    /// </summary>
    private JsonNode? _DatasetEditor(JsonNode parameters)
    {
        var document = _Semantic((string)parameters["textDocument"]!["uri"]!);
        if (document is null || DatasetEditorModel.At(document, (uint)(int)parameters["line"]!) is not { } model) return null;

        JsonNode Field(DatasetField field) => new JsonObject
        {
            ["name"] = field.Name,
            ["type"] = field.Type,
            ["required"] = field.Required,
            ["choices"] = new JsonArray([.. field.Choices.Select(c => (JsonNode)new JsonObject { ["label"] = c.Label, ["inline"] = c.Inline, ["json"] = c.Json })]),
        };

        JsonNode Row(DatasetRow row)
        {
            var values = new JsonObject();
            foreach (var (name, value) in row.Values) values[name] = value;
            return new JsonObject { ["name"] = row.Name, ["values"] = values, ["raw"] = row.Raw };
        }

        return new JsonObject
        {
            ["name"] = model.Name,
            ["mold"] = model.Mold,
            ["createsMold"] = model.CreatesMold,
            ["source"] = model.Source,
            ["body"] = model.Body is { } body ? _Range(document.Source, body) : null,
            ["fields"] = new JsonArray([.. model.Fields.Select(Field)]),
            ["rows"] = new JsonArray([.. model.Rows.Select(Row)]),
        };
    }

    /// <summary>La coloration sémantique du document : la sorte de chaque nom, retrouvée comme pour le survol.</summary>
    private JsonNode? _SemanticTokens(JsonNode parameters)
    {
        var document = _Semantic((string)parameters["textDocument"]!["uri"]!);
        if (document is null) return new JsonObject { ["data"] = new JsonArray() };

        return new JsonObject { ["data"] = new JsonArray([.. SemanticTokensProvider.Encode(document).Select(n => (JsonNode)n)]) };
    }

    /// <summary>L'aide à la saisie d'un appel : la signature du gear et le paramètre en cours.</summary>
    private JsonNode? _SignatureHelp(JsonNode parameters)
    {
        var uri = (string)parameters["textDocument"]!["uri"]!;
        var document = _Semantic(uri);
        if (document is null) return null;

        var offset = _OffsetOf(uri, document, parameters["position"]!);
        if (offset < 0 || SignatureHelpProvider.At(document, offset) is not { } signature) return null;

        JsonObject Node(SignatureResult item)
        {
            var node = new JsonObject
            {
                ["label"] = item.Label,
                ["parameters"] = new JsonArray([.. item.Parameters.Select((p, i) =>
                {
                    var parameter = new JsonObject { ["label"] = new JsonArray(p.Start, p.End) };
                    if (item.ParameterDocumentation?[i] is { } text) parameter["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = text };
                    return (JsonNode)parameter;
                })]),
            };
            if (item.Documentation is { } documentation) node["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
            return node;
        }
        var all = new[] { signature }.Concat(signature.Alternatives ?? []).ToArray();
        return new JsonObject { ["signatures"] = new JsonArray([.. all.Select(s => (JsonNode)Node(s))]), ["activeSignature"] = 0, ["activeParameter"] = signature.Active };
    }

    /// <summary>
    /// Un diagnostic dans la forme de l'éditeur : sa gravité, son code, le passage souligné, et son
    /// message suivi de son étiquette et de ses aides. Sans position, il se place au début du fichier.
    /// </summary>
    private static JsonObject _ToLsp(Diagnostic diagnostic, SourceText source)
    {
        var lines = new List<string> { diagnostic.Message };
        if (diagnostic.Label is { } label) lines.Add($"→ {label}");
        lines.AddRange((diagnostic.Help ?? []).Select(h => $"aide : {h}"));

        // Sans position (une erreur de projet), le diagnostic souligne la première ligne : une plage vide ne se verrait pas.
        var (start, end) = diagnostic.Span is { } span ? _VisibleRange(source, span) : _FirstLine(source);
        var result = new JsonObject
        {
            ["range"] = new JsonObject { ["start"] = start, ["end"] = end },
            ["severity"] = diagnostic.Level switch
            {
                DiagnosticLevel.Error => 1,
                DiagnosticLevel.Warning => 2,
                _ => 3,
            },
            ["code"] = diagnostic.Code,
            ["source"] = "rgl",
            ["message"] = string.Join('\n', lines),
        };
        // Un emploi obsolète est barré (étiquette « obsolète » du protocole).
        if (diagnostic.Code == DiagnosticCodes.DeprecatedUse) result["tags"] = new JsonArray(2);
        return result;
    }

    private static (JsonObject Start, JsonObject End) _FirstLine(SourceText source)
    {
        var length = source.Content.IndexOfAny(['\r', '\n']);
        if (length < 0) length = source.Content.Length;
        return (_Position(source, default), _Position(source, Rotomeca.Utils.Types.RUint.CreateChecked((uint)Math.Max(1, Math.Min(length, source.Content.Length)))));
    }

    /// <summary>
    /// Le passage à souligner : celui du diagnostic, mais jamais vide. Un « ; » manquant se signale en un point, entre deux caractères, que l'éditeur
    /// ne dessine pas (surtout en fin de ligne, après des espaces) : on souligne alors le dernier caractère écrit avant ce point.
    /// </summary>
    private static (JsonObject Start, JsonObject End) _VisibleRange(SourceText source, SourceSpan span)
    {
        var (from, to) = ((int)span.Start.Value, (int)span.End.Value);
        if (to > from) return (_Position(source, span.Start), _Position(source, span.End));

        var text = source.Content;
        var lineStart = from > 0 ? text.LastIndexOf('\n', Math.Min(from, text.Length) - 1) + 1 : 0;
        var at = Math.Min(from, text.Length);
        while (at > lineStart && char.IsWhiteSpace(text[at - 1])) --at;
        if (at > lineStart) (from, to) = (at - 1, at);
        else if (from < text.Length && text[from] is not ('\r' or '\n')) to = from + 1;
        return (_Position(source, Rotomeca.Utils.Types.RUint.CreateChecked((uint)from)), _Position(source, Rotomeca.Utils.Types.RUint.CreateChecked((uint)to)));
    }

    /// <summary>La position d'un indice du texte pour l'éditeur : ligne et colonne en unités UTF-16, à partir de 0.</summary>
    private static JsonObject _Position(SourceText source, Rotomeca.Utils.Types.RUint index)
    {
        var line = (int)source[index].Line.Value - 1;
        return new JsonObject { ["line"] = line, ["character"] = (int)(index.Value - source.LineStart[line].Value) };
    }

    /// <summary>
    /// Le chemin d'un document : celui du fichier pour une URI <c>file:</c>, l'URI elle-même sinon. L'éditeur écrit <c>file:///e%3A/dossier/f.rgl</c> : le
    /// <c>:</c> du lecteur est encodé, et <c>Uri.LocalPath</c> rend alors <c>/e:/dossier/f.rgl</c>, qui n'est le chemin d'aucun fichier. Le chemin est
    /// donc décodé ici : le lecteur n'a plus son <c>/</c>, et sous Windows les <c>/</c> deviennent des <c>\</c>.
    /// </summary>
    internal static string PathOf(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return uri;

        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        else if (parsed.Host.Length > 0) path = "//" + parsed.Host + path;
        if (!OperatingSystem.IsWindows() && path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') path = "/" + path;
        return OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;
    }

    private static string _PathOf(string uri) => PathOf(uri);

    private void _RespondError(JsonNode id, int code, string message) =>
        _channel.Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });
}
