using System.Text.Json.Nodes;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Les demandes de navigation : références, surlignage des occurrences, renommage, pliage, extension de sélection, symboles de l'espace de travail.</summary>
internal sealed partial class LanguageServer
{
    /// <summary>L'adresse d'un fichier : celle du document s'il l'est, sinon celle que donne son chemin.</summary>
    private static string _UriOf(string path, string documentUri, SemanticDocument document) =>
        string.Equals(path, document.Self.Path, StringComparison.OrdinalIgnoreCase) ? documentUri : new Uri(path).AbsoluteUri;

    private (SemanticDocument Document, int Offset, string Uri)? _At(JsonNode parameters)
    {
        var uri = (string)parameters["textDocument"]!["uri"]!;
        var document = _Semantic(uri);
        if (document is null) return null;
        return (document, document.OffsetOf((uint)parameters["position"]!["line"]!, (uint)parameters["position"]!["character"]!), uri);
    }

    /// <summary>Les références : tous les emplois de ce que désigne le mot sous le curseur, dans tout le projet.</summary>
    private JsonNode? _References(JsonNode parameters)
    {
        if (_At(parameters) is not var (document, offset, uri) || ReferenceFinder.Find(document, offset) is not { } found) return new JsonArray();

        var includeDeclaration = (bool?)parameters["context"]?["includeDeclaration"] ?? true;
        return new JsonArray([.. found.Occurrences.Where(o => includeDeclaration || !o.IsDeclaration)
            .Select(o => (JsonNode)new JsonObject { ["uri"] = _UriOf(o.Path, uri, document), ["range"] = _Range(o.Source, o.Span) })]);
    }

    /// <summary>Le surlignage des occurrences du mot sous le curseur, dans le document ; la déclaration est signalée comme une écriture.</summary>
    private JsonNode? _DocumentHighlight(JsonNode parameters)
    {
        if (_At(parameters) is not var (document, offset, _) || ReferenceFinder.Find(document, offset, wholeProject: false) is not { } found) return new JsonArray();

        return new JsonArray([.. found.Occurrences.Select(o => (JsonNode)new JsonObject { ["range"] = _Range(o.Source, o.Span), ["kind"] = o.IsDeclaration ? 3 : 2 })]);
    }

    /// <summary>
    /// Ce qu'on peut renommer sous le curseur : le mot et son texte. Un nom de la std, un mot-clé ou un nom inconnu ne se renomment pas : le message
    /// dit pourquoi.
    /// </summary>
    private JsonNode? _PrepareRename(JsonNode parameters)
    {
        if (_At(parameters) is not var (document, offset, _)) return null;

        var target = document.TargetAt(ReferenceFinder.WordOffset(document, offset));
        if (target is null || target.Token.Kind != Syntax.TokenKind.Identifier) throw new InvalidOperationException("seul un nom se renomme");
        if (target.Symbol is not { } symbol || ReferenceFinder.KeyOf(symbol) is null) throw new InvalidOperationException("ce nom n'est déclaré nulle part : il ne se renomme pas");
        if (ReferenceFinder.WhyNotRenamable(symbol) is { } why) throw new InvalidOperationException(why);

        return new JsonObject { ["range"] = _Range(document.Source, target.Range), ["placeholder"] = target.Token.Text };
    }

    /// <summary>
    /// Le renommage : chaque emploi reçoit le nouveau nom, dans tous les fichiers du projet. Une variable écrite en raccourci dans une valeur de mold
    /// (<c>{x}</c>) garde son champ : elle devient <c>{x: nouveau}</c>.
    /// </summary>
    private JsonNode? _Rename(JsonNode parameters)
    {
        if (_At(parameters) is not var (document, offset, uri)) return null;

        var name = (string)parameters["newName"]!;
        if (ReferenceFinder.WhyInvalidName(name) is { } invalid) throw new InvalidOperationException(invalid);
        if (ReferenceFinder.Find(document, offset) is not { } found) throw new InvalidOperationException("ce nom n'est déclaré nulle part : il ne se renomme pas");
        if (ReferenceFinder.WhyNotRenamable(found.Symbol) is { } why) throw new InvalidOperationException(why);

        var changes = new JsonObject();
        foreach (var group in found.Occurrences.GroupBy(o => o.Path, StringComparer.OrdinalIgnoreCase))
        {
            changes[_UriOf(group.Key, uri, document)] = new JsonArray([.. group.Select(o => (JsonNode)new JsonObject
            {
                ["range"] = _Range(o.Source, o.Span),
                ["newText"] = o.IsShorthand && found.Symbol.Kind != RglSymbolKind.Field ? $"{found.Symbol.Name}: {name}" : name,
            })]);
        }
        return new JsonObject { ["changes"] = changes };
    }

    /// <summary>Les zones repliables du document.</summary>
    private JsonNode? _FoldingRanges(JsonNode parameters)
    {
        var document = _Semantic((string)parameters["textDocument"]!["uri"]!);
        if (document is null) return new JsonArray();

        return new JsonArray([.. FoldingProvider.Of(document).Select(f =>
        {
            var range = new JsonObject { ["startLine"] = f.StartLine, ["endLine"] = f.EndLine };
            if (f.Kind is { } kind) range["kind"] = kind;
            return (JsonNode)range;
        })]);
    }

    /// <summary>L'extension de sélection : pour chaque position, la chaîne des étendues qui la contiennent, de la plus petite à la plus grande.</summary>
    private JsonNode? _SelectionRanges(JsonNode parameters)
    {
        var document = _Semantic((string)parameters["textDocument"]!["uri"]!);
        var results = new JsonArray();
        foreach (var position in parameters["positions"]!.AsArray())
        {
            if (document is null) { results.Add(new JsonObject { ["range"] = new JsonObject() }); continue; }

            var offset = document.OffsetOf((uint)position!["line"]!, (uint)position["character"]!);
            JsonObject? parent = null;
            foreach (var span in SelectionProvider.At(document, offset).Reverse())
            {
                var node = new JsonObject { ["range"] = _Range(document.Source, span) };
                if (parent is not null) node["parent"] = parent;
                parent = node;
            }
            results.Add(parent ?? new JsonObject { ["range"] = new JsonObject { ["start"] = position.DeepClone(), ["end"] = position.DeepClone() } });
        }
        return results;
    }

    /// <summary>La déclaration du type de ce que désigne le mot : celle du mold d'une variable, d'un paramètre ou d'un champ.</summary>
    private JsonNode? _TypeDefinition(JsonNode parameters)
    {
        if (_At(parameters) is not var (document, offset, uri) || document.TargetAt(offset)?.Symbol is not { } symbol) return null;

        var typeText = symbol.IsType ? symbol.Name : document.TypeOfSymbol(symbol);
        if (typeText is null || document.ResolveTypeText(typeText, offset) is not { Module: { } module } type || type.NameSpan.Length.Value == 0) return null;

        return new JsonObject { ["uri"] = _UriOf(module.Path, uri, document), ["range"] = _ModuleRange(module, type.NameSpan) };
    }

    /// <summary>
    /// La plage d'une déclaration d'un autre fichier. Pour une page <c>.paper</c>, le module est celui que le compilateur engendre (ses positions ne sont pas
    /// celles de la page) : on y mène au début du fichier.
    /// </summary>
    private static JsonObject _ModuleRange(ModuleInfo module, SourceSpan span)
    {
        if (!PaperProjection.IsPaper(module.Path)) return _Range(module.Source, span);

        var start = new JsonObject { ["line"] = 0, ["character"] = 0 };
        return new JsonObject { ["start"] = start, ["end"] = start.DeepClone() };
    }

    /// <summary><c>rgl/moldSkeleton</c> (propre à RGL, pour la suggestion grisée de l'extension) : la valeur d'un mold à écrire après <c>Type nom:</c>.</summary>
    private JsonNode? _MoldSkeleton(JsonNode parameters)
    {
        var uri = (string)parameters["textDocument"]!["uri"]!;
        if (PaperProjection.IsPaper(_PathOf(uri)) || _Semantic(uri) is not { } document) return null;

        var offset = document.OffsetOf((uint)parameters["position"]!["line"]!, (uint)parameters["position"]!["character"]!);
        return MoldSkeletonProvider.At(document, offset) is { } skeleton ? new JsonObject { ["snippet"] = skeleton.Snippet } : null;
    }

    #region Hiérarchie d'appels et liens

    private static JsonNode _ToLsp(CallItem item) => new JsonObject
    {
        ["name"] = item.Name,
        ["kind"] = 12,
        ["detail"] = item.Detail,
        ["uri"] = new Uri(item.Path).AbsoluteUri,
        ["range"] = _Range(item.Source, item.Range),
        ["selectionRange"] = _Range(item.Source, item.Selection),
        ["data"] = new JsonObject { ["path"] = item.Path, ["offset"] = (int)item.Selection.Start.Value },
    };

    /// <summary>Le document d'un fichier du projet : tel que l'éditeur le montre s'il est ouvert, sinon lu sur le disque.</summary>
    private SemanticDocument? _DocumentAtPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (_documents.Keys.FirstOrDefault(u => string.Equals(_FullPath(u), full, StringComparison.OrdinalIgnoreCase)) is { } uri) return _Semantic(uri);
        if (!File.Exists(full)) return null;
        return SemanticDocument.Create(full, File.ReadAllText(full), _index.Open(full, file => _openTexts.GetValueOrDefault(file)));
    }

    private JsonNode? _PrepareCallHierarchy(JsonNode parameters)
    {
        if (_At(parameters) is not var (document, offset, _) || CallHierarchyProvider.Prepare(document, offset) is not { } item) return null;
        return new JsonArray(_ToLsp(item));
    }

    private JsonNode? _CallHierarchy(JsonNode parameters, bool incoming)
    {
        var data = parameters["item"]?["data"];
        if (data is null || _DocumentAtPath((string)data["path"]!) is not { } document) return new JsonArray();
        if (CallHierarchyProvider.Prepare(document, (int)data["offset"]!) is not { } item) return new JsonArray();

        var edges = incoming ? CallHierarchyProvider.Incoming(document, item) : CallHierarchyProvider.Outgoing(document, item);
        return new JsonArray([.. edges.Select(e => (JsonNode)new JsonObject
        {
            [incoming ? "from" : "to"] = _ToLsp(e.Item),
            ["fromRanges"] = new JsonArray([.. e.Calls.Select(c => (JsonNode)_Range(e.Source, c))]),
        })]);
    }

    /// <summary>Les liens des imports du document.</summary>
    private JsonNode? _DocumentLinks(JsonNode parameters)
    {
        var document = _Semantic((string)parameters["textDocument"]!["uri"]!);
        if (document is null) return new JsonArray();

        return new JsonArray([.. DocumentLinkProvider.Of(document).Select(l => (JsonNode)new JsonObject
        {
            ["range"] = _Range(document.Source, l.Range),
            ["target"] = new Uri(l.Target).AbsoluteUri,
            ["tooltip"] = l.Tooltip,
        })]);
    }

    #endregion

    /// <summary>Aller au symbole dans l'espace de travail : les déclarations de premier niveau des projets des documents ouverts.</summary>
    private JsonNode? _WorkspaceSymbols(JsonNode parameters)
    {
        var modules = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in _documents.Keys)
        {
            if (_Semantic(uri) is not { } document) continue;
            modules.TryAdd(document.Self.Path, document.Self);
            foreach (var module in document.Project.Modules.Where(m => !PaperProjection.IsPaper(m.Path))) modules.TryAdd(module.Path, module);
        }

        var results = WorkspaceSymbolProvider.Search(modules.Values, (string?)parameters["query"] ?? "");
        return new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
        {
            ["name"] = r.Symbol.Name,
            ["kind"] = r.Kind,
            ["containerName"] = r.Module.Name,
            ["location"] = new JsonObject { ["uri"] = new Uri(r.Module.Path).AbsoluteUri, ["range"] = _Range(r.Module.Source, r.Symbol.NameSpan) },
        })]);
    }
}
