using System.Text;
using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Text;

namespace Rgl.Paper;

/// <summary>Compile le markup, les blocs <c>@rgl</c> et les interpolations d'un fichier <c>.paper</c>.</summary>
internal static partial class PaperCompiler
{
    public const string Extension = ".paper";

    /// <summary>La description du texte « virtuel » que l'éditeur fait d'une page (le RGL de la page écrit comme un fichier forge) : il ne se compile pas comme une page.</summary>
    public const string VirtualMarker = "rgl-paper-virtual";

    /// <summary>Vrai pour le texte virtuel d'une page, lu à la place du <c>.paper</c> par l'éditeur.</summary>
    public static bool IsVirtual(string text) => text.StartsWith("forge: gears\ndescription: \"" + VirtualMarker + "\"", StringComparison.Ordinal);

    /// <summary>Produit un module <c>forge: gears</c> dont <c>render()</c> renvoie le HTML initial.</summary>
    public static PaperDocument? Compile(SourceText source, string projectRoot, DiagnosticBag diagnostics)
    {
        var blocks = new StringBuilder();
        var markup = new StringBuilder();
        // Pour chaque bloc : la ligne de la page où il commence et le nombre de lignes qu'il occupe, pour que le débogueur parle de la page.
        List<(int PaperLine, int Lines)> blockRanges = [];
        if (!Split(source, blocks, markup, diagnostics, blockRanges)) return null;
        List<string> layoutLoads = [];
        if (!ApplyLayouts(source, projectRoot, diagnostics, blocks, markup, blockRanges, layoutLoads, out var layoutCount)) return null;
        if (!ExpandComponents(source, projectRoot, diagnostics, blocks, markup, blockRanges, out var componentCount)) return null;
        if (layoutCount + componentCount > 0)
        {
            // Les blocs de plusieurs fichiers sont bout à bout : les constantes de la page ne doivent pas venir après les gears du layout ou des composants.
            var hoisted = HoistConstants(blocks.ToString());
            if (hoisted != blocks.ToString())
            {
                blocks.Clear().Append(hoisted);
                blockRanges.Clear();
                blockRanges.Add((0, hoisted.Count(c => c == '\n')));
            }
        }
        List<PaperReactive> reactives = [];
        var blockSource = ReactiveDeclaration().Replace(blocks.ToString(), match =>
        {
            reactives.Add(new PaperReactive(match.Groups[2].Value.Trim(), match.Groups[3].Value, match.Groups[4].Value.Trim(), match.Groups[1].Success));
            // Les lignes de la déclaration restent (vides) : le reste du bloc garde sa place dans la page.
            return new string('\n', match.Value.Count(c => c == '\n'));
        });
        if (blockSource.Contains("reactive", StringComparison.Ordinal))
        {
            diagnostics.Error("déclaration reactive Paper mal formée ; écris : reactive TYPE nom: expression;", source.Path);
            return null;
        }
        List<PaperReactive> parameters = [];
        blockSource = ParamDeclaration().Replace(blockSource, match =>
        {
            parameters.Add(new PaperReactive(match.Groups[1].Value.Trim(), match.Groups[2].Value, match.Groups[3].Value.Trim(), false, true));
            return new string('\n', match.Value.Count(c => c == '\n'));
        });
        if (ParamLeftover().IsMatch(blockSource))
        {
            diagnostics.Error("déclaration param Paper mal formée ; écris : param TYPE nom; ou param TYPE nom: valeur;", source.Path);
            return null;
        }
        reactives.InsertRange(0, parameters);
        var hasParams = parameters.Count > 0;
        if (reactives.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != reactives.Count)
        {
            diagnostics.Error("deux états reactive portent le même nom dans ce fichier Paper", source.Path);
            return null;
        }
        if (reactives.Any(r => r.Name == PageName))
        {
            diagnostics.Error($"« {PageName} » est le nom de la tête de page : un état reactive ne peut pas le porter", source.Path);
            return null;
        }

        var signatures = GearSignature().Matches(blocks.ToString()).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim(), StringComparer.Ordinal);
        var hasPageLoad = signatures.TryGetValue(LoadGear, out var loadParameters);
        if (hasPageLoad && loadParameters!.Length > 0)
        {
            diagnostics.Error($"le gear « {LoadGear} » s'exécute au chargement de la page et ne prend aucun paramètre", source.Path);
            return null;
        }
        var hasLoad = hasPageLoad || layoutLoads.Count > 0;
        foreach (var gear in signatures.Keys)
        {
            var written = WrittenReactives(blocks.ToString(), gear, reactives);
            if (parameters.FirstOrDefault(p => written.Contains(p.Name)) is { } changed)
            {
                diagnostics.Error($"le gear « {gear} » écrit le paramètre « {changed.Name} » : un param est en lecture seule ; copie sa valeur dans un reactive pour la modifier", source.Path);
                return null;
            }
        }
        var interpolations = string.Join('\n', InterpolationText().Matches(markup.ToString()).Select(m => m.Groups[1].Value));
        var usesPage = hasLoad || PageUse().IsMatch(CodeOnly(blockSource)) || PageUse().IsMatch(CodeOnly(interpolations));
        if (usesPage) reactives.Insert(0, new PaperReactive("PageHead", PageName, "{}", false));
        if (reactives.Count > 0) blockSource = TransformStatefulGears(blockSource, reactives, usesPage);
        List<PaperHandler> handlers = [];
        var markupSource = EventAttribute().Replace(markup.ToString(), match =>
        {
            var eventName = match.Groups[1].Value;
            var gear = match.Groups[2].Value;
            if (!signatures.TryGetValue(gear, out var parameters))
            {
                diagnostics.Error($"handler Paper « {gear} » introuvable dans les blocs @rgl", source.Path);
                return match.Value;
            }
            var receives = parameters.Length > 0;
            if (receives && !Regex.IsMatch(parameters, @"^DomEvent\s+[A-Za-z_][A-Za-z0-9_]*$"))
            {
                diagnostics.Error($"le handler « {gear} » prend zéro paramètre ou un DomEvent", source.Path);
                return match.Value;
            }
            handlers.Add(new PaperHandler(eventName, gear, receives));
            return $"data-rgl-on-{eventName}=\"{gear}\"";
        });
        if (diagnostics.HasErrors) return null;

        List<PaperFragment> fragments = [];
        List<string> pieces = [];
        var text = markupSource;
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf("@@", position, StringComparison.Ordinal);
            if (open < 0)
            {
                if (position < text.Length) pieces.Add(Quote(text[position..]));
                break;
            }
            if (open > position) pieces.Add(Quote(text[position..open]));
            var close = text.IndexOf("@@", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                diagnostics.Error("interpolation Paper non fermée : « @@ » attend un autre « @@ »", source.Path);
                return null;
            }

            var written = text[(open + 2)..close].Trim();
            if (written.Length == 0)
            {
                diagnostics.Error("une interpolation Paper contient une expression", source.Path);
                return null;
            }
            var id = fragments.Count;
            var raw = UnwrapHtml(written, out var inner);
            string? imported = null;
            if (TryImport(inner, source.Path, projectRoot, diagnostics, out var content, out imported))
                inner = Quote(content!);
            else if (ImportStart().IsMatch(inner)) return null;

            var dependencies = reactives.Where(reactive => !reactive.IsParam && Regex.IsMatch(written, $@"(?<![.\w]){Regex.Escape(reactive.Name)}\b"))
                .Select(reactive => reactive.Name).ToArray();
            // Dans <title>, un commentaire est traité comme du texte brut par HTML et apparaîtrait
            // dans l'onglet du navigateur. Le titre est rendu correctement, mais reste statique.
            var trackable = !InsideElement(text, open, "title");
            fragments.Add(new PaperFragment(id, written, raw, imported, content, dependencies, trackable));
            if (trackable) pieces.Add(Quote($"<!--rgl:f{id}-->"));
            pieces.Add($"__paper_fragment_{id}()");
            if (trackable) pieces.Add(Quote($"<!--rgl:/f{id}-->"));
            position = close + 2;
        }
        if (text.Length == 0) pieces.Add("\"\"");
        var component = Path.GetFileNameWithoutExtension(source.Path);
        var socketPath = $"/__rgl/paper/{component}";
        if (handlers.Count > 0)
            pieces.Add(Quote($"<script src=\"/rgl-paper.js\"></script><script>RglPaper.connect((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '{socketPath}'{(hasParams ? " + location.search" : "")});</script>"));

        if (handlers.Count > 0 && reactives.Count > 0)
        {
            handlers = [.. handlers.Select(handler =>
            {
                var written = WrittenReactives(blocks.ToString(), handler.Gear, reactives);
                var invalidates = fragments.Where(fragment => fragment.Trackable && fragment.Dependencies.Any(written.Contains)).Select(fragment => fragment.Id).ToArray();
                return handler with { Invalidates = invalidates };
            })];
        }

        var propsParameter = hasParams ? "Props props" : "";
        var propsArgument = hasParams ? "props" : "";
        List<int> fragmentLines = [];
        var generated = new StringBuilder("forge: gears\ndescription: \"Page Paper engendrée\"\n\nuse <Http> only {HttpRequest, HttpResponse, HttpServer};\nuse <Paper> only {html, escapeHtml, escapeJson, DomEvent");
        if (usesPage) generated.Append(", PageHead, headHtml, takeHead, settle");
        generated.Append("};\n");
        if (handlers.Count > 0) generated.Append("use <File> only {readText};\nuse <Path> only {executableDirectory, join};\n");
        generated.Append('\n');
        generated.Append("export const uint fragmentCount: ").Append(fragments.Count).Append(";\n");
        if (hasParams)
        {
            generated.Append("export mold Props {\n");
            foreach (var parameter in parameters)
            {
                generated.Append("    ").Append(parameter.Type).Append(' ').Append(parameter.Name);
                if (parameter.Initializer.Length > 0) generated.Append(": ").Append(parameter.Initializer);
                generated.Append(";\n");
            }
            generated.Append("}\n");
        }
        if (reactives.Count > 0)
        {
            generated.Append("export mold State {\n");
            foreach (var reactive in reactives)
            {
                generated.Append("    ").Append(reactive.Type).Append(' ').Append(reactive.Name);
                if (reactive.Initializer.Length > 0) generated.Append(": ").Append(reactive.Initializer);
                generated.Append(";\n");
            }
            generated.Append("}\n");
        }
        var blockStartLine = Lines(generated);
        generated.Append(blockSource);
        if (blockSource.Length > 0 && blockSource[^1] != '\n') generated.Append('\n');
        if (reactives.Count > 0)
        {
            var initialState = hasParams ? "{" + string.Join(", ", parameters.Select(p => $"{p.Name}: props.{p.Name}")) + "}" : "{}";
            if (hasLoad) generated.Append("export gear createState(").Append(propsParameter).Append("): State {\n    mut State state: ").Append(initialState).Append(";\n    mut PageHead page: state.page;\n")
                .AppendJoin("", layoutLoads.Append(hasPageLoad ? LoadGear : "").Where(name => name.Length > 0).Select(name => $"    {name}(state, page);\n"))
                .Append("    state.page <- page;\n    return state;\n}\n");
            else generated.Append("export gear createState(").Append(propsParameter).Append("): State => ").Append(initialState).Append(";\n");
            if (usesPage) generated.Append("export gear createSettledState(").Append(propsParameter).Append("): State {\n    mut State state: createState(").Append(propsArgument).Append(");\n    mut PageHead page: state.page;\n    settle(page);\n    state.page <- page;\n    return state;\n}\n");
        }
        foreach (var fragment in fragments)
        {
            fragmentLines.Add(Lines(generated));
            var expression = reactives.Count == 0 ? fragment.Expression : RewriteState(fragment.Expression, reactives);
            generated.Append("gear __paper_fragment_").Append(fragment.Id).Append('(')
                .Append(reactives.Count == 0 ? "" : usesPage ? "mut State state, PageHead page" : "mut State state").Append("): string => ");
            if (fragment.ImportedFile is not null)
            {
                var literal = Quote(fragment.ImportedContent!);
                if (fragment.RawHtml) generated.Append(literal).Append(";\n");
                else generated.Append("escapeHtml(").Append(literal).Append(");\n");
            }
            else if (fragment.RawHtml) generated.Append('(').Append(expression).Append(")°;\n");
            else generated.Append("escapeHtml(`{").Append(expression).Append("}`);\n");
        }
        var stateParameter = reactives.Count == 0 ? "" : "mut State state";
        var stateArgument = reactives.Count == 0 ? "" : usesPage ? "state, page" : "state";
        if (reactives.Count > 0)
            for (var i = 0; i < pieces.Count; i++)
                if (pieces[i].StartsWith("__paper_fragment_", StringComparison.Ordinal)) pieces[i] = pieces[i][..^1] + stateArgument + ")";
        var body = pieces.Count == 0 ? "\"\"" : string.Join(" + ", pieces);
        if (usesPage)
            generated.Append("\nexport gear renderWithState(mut State state): html {\n    mut PageHead page: state.page;\n    const string body: ").Append(body)
                .Append(";\n    const string head: headHtml(page);\n    state.page <- page;\n    return html(body + head);\n}\n");
        else
            generated.Append("\nexport gear ").Append(reactives.Count == 0 ? "render" : "renderWithState").Append('(').Append(stateParameter).Append("): html => html(")
                .Append(body).Append(");\n");
        if (reactives.Count > 0)
            generated.Append("export gear render(").Append(propsParameter).Append("): html { mut State state: createState(").Append(propsArgument).Append("); return renderWithState(state); }\n");
        generated.Append("export gear renderFragment(").Append(reactives.Count == 0 ? "" : "mut State state, ").Append("uint id): string {\n");
        if (usesPage) generated.Append("    const PageHead page: state.page;\n");
        generated.Append("    switch (id) {\n");
        foreach (var fragment in fragments)
            generated.Append("        case ").Append(fragment.Id).Append(": return __paper_fragment_").Append(fragment.Id).Append('(').Append(stateArgument).Append(");\n");
        generated.Append("        default: return '';\n    }\n}\n");
        if (handlers.Count > 0)
        {
            string HandlerCall(PaperHandler h) => h.Gear + "(" + (reactives.Count == 0 ? "" : stateArgument)
                + (reactives.Count > 0 && h.ReceivesEvent ? ", " : "") + (h.ReceivesEvent ? "event" : "") + ")";
            generated.Append("export gear dispatch(").Append(reactives.Count == 0 ? "" : "mut State state, ")
                .Append("string handler, DomEvent event): [uint] {\n");
            if (usesPage) generated.Append("    mut PageHead page: state.page;\n    [uint] result: [];\n");
            generated.Append("    switch (handler) {\n");
            foreach (var handler in handlers.DistinctBy(h => h.Gear))
            {
                generated.Append("        case '").Append(handler.Gear).Append("': ").Append(HandlerCall(handler)).Append(usesPage ? "; result <- [" : "; return [")
                    .AppendJoin(", ", handler.Invalidates).Append(usesPage ? "]; break;\n" : "];\n");
            }
            generated.Append(usesPage ? "        default: break;\n    }\n    state.page <- page;\n    return result;\n}\n" : "        default: return [];\n    }\n}\n");
            generated.Append("export gear handleMessage(").Append(reactives.Count == 0 ? "" : "mut State state, ").Append("string message): string {\n")
                .Append("    const [string] parts: message.split(\"\\n\");\n")
                .Append("    const string handler: parts.length > 0 ? parts[0] : '';\n")
                .Append("    const DomEvent event: {type: parts.length > 1 ? parts[1] : '', targetId: parts.length > 2 ? parts[2] : '', value: parts.length > 3 ? parts[3] : '', key: parts.length > 4 ? parts[4] : ''};\n");
            if (usesPage) generated.Append("    mut PageHead page: state.page;\n    string result: '{\"type\":\"fragments\",\"items\":[]}';\n");
            generated.Append("    switch (handler) {\n");
            foreach (var handler in handlers.DistinctBy(h => h.Gear))
            {
                generated.Append("        case '").Append(handler.Gear).Append("': ").Append(HandlerCall(handler))
                    .Append(usesPage ? "; result <- " : "; return ").Append("'{\"type\":\"fragments\",\"items\":['");
                for (var i = 0; i < handler.Invalidates.Count; i++)
                {
                    if (i > 0) generated.Append(" + ','");
                    var id = handler.Invalidates[i];
                    generated.Append(" + '{\"id\":").Append(id).Append(",\"html\":\"' + escapeJson(__paper_fragment_").Append(id).Append('(')
                        .Append(reactives.Count == 0 ? "" : stateArgument).Append(")) + '\"}'");
                }
                generated.Append(usesPage ? " + ']' + takeHead(page) + '}'; break;\n" : " + ']}';\n");
            }
            generated.Append(usesPage ? "        default: break;\n    }\n    state.page <- page;\n    return result;\n}\n"
                : "        default: return '{\"type\":\"fragments\",\"items\":[]}';\n    }\n}\n");
        }
        var kind = handlers.Count > 0 ? "guard" : "gear";
        var rendered = hasParams ? "render(provider(request))" : "render()";
        var created = (usesPage ? "createSettledState" : "createState") + (hasParams ? "(provider(request))" : "()");
        if (hasParams) generated.Append(kind).Append(" _mountPage(HttpServer server, string path, (HttpRequest) => Props provider): HttpServer {\n");
        else generated.Append("export ").Append(kind).Append(" mount(HttpServer server, string path): HttpServer {\n");
        generated.Append("    mut HttpServer configured: server;\n");
        if (handlers.Count > 0)
        {
            generated.Append("    const string runtime: readText(join(executableDirectory(), 'rgl-paper.js'));\n");
        }
        generated.Append("    configured.get(path, (HttpRequest request) => HttpResponse.html(").Append(rendered).Append("°, 200));\n");
        if (handlers.Count > 0)
        {
            generated.Append("    configured.get('/rgl-paper.js', (HttpRequest request) => { const HttpResponse response: {status: 200, contentType: 'application/javascript; charset=utf-8', body: runtime}; return response; });\n")
                .Append("    configured.webSocketCircuit('").Append(socketPath).Append("', (HttpRequest request) => {\n");
            if (reactives.Count > 0)
                generated.Append("        mut [State] circuit: [").Append(created).Append("];\n        return (string message) => (circuit ||> (mut State state) => handleMessage(state, message))[0];\n");
            else generated.Append("        return (string message) => handleMessage(message);\n");
            generated.Append("    });\n");
        }
        generated.Append("    return configured;\n}\n");
        if (hasParams)
        {
            generated.Append("export ").Append(kind).Append(" mount(HttpServer server, string path, Props props): HttpServer {\n")
                .Append("    return _mountPage(server, path, (HttpRequest request) => props);\n}\n");
            generated.Append("export ").Append(kind).Append(" mountWith(HttpServer server, string path, (HttpRequest) => Props provider): HttpServer {\n")
                .Append("    return _mountPage(server, path, provider);\n}\n");
            if (parameters.All(p => p.Initializer.Length > 0))
                generated.Append("export ").Append(kind).Append(" mount(HttpServer server, string path): HttpServer {\n")
                    .Append("    return _mountPage(server, path, (HttpRequest request) => new Props {});\n}\n");
        }

        // Ligne engendrée → ligne de la page : les blocs (verbatim, ligne pour ligne) et les interpolations ; le reste (render, dispatch, mount…) n'en a pas.
        var map = new uint[Lines(generated)];
        var cumulative = 0;
        foreach (var (paperLine, count) in blockRanges)
        {
            for (var k = 0; k < count; k++)
            {
                var index = blockStartLine - 1 + cumulative + k;
                if (paperLine > 0 && index < map.Length) map[index] = (uint)(paperLine + k);
            }
            cumulative += count;
        }
        var expressions = PaperRegions.Find(source.Content).Where(r => !r.IsBlock).ToArray();
        // Avec un layout ou un composant, les interpolations ne sont plus dans l'ordre de la page : elles ne sont rattachées à aucune ligne.
        for (var i = 0; layoutCount + componentCount == 0 && i < fragments.Count && i < expressions.Length && i < fragmentLines.Count; i++)
        {
            var paperLine = LineOf(source.Content, expressions[i].Start);
            for (var k = 0; k < CountLines(source.Content, expressions[i].Start, expressions[i].End); k++)
            {
                var index = fragmentLines[i] - 1 + k;
                if (index < map.Length) map[index] = (uint)(paperLine + k);
            }
        }
        return new PaperDocument(new SourceText(source.Path, generated.ToString()) { LineMap = map }, fragments, reactives, handlers);
    }

    /// <summary>Indique si une position se trouve après l'ouverture non refermée de cet élément.</summary>
    private static bool InsideElement(string text, int position, string name)
    {
        var before = text.AsSpan(0, position);
        var opening = before.LastIndexOf($"<{name}", StringComparison.OrdinalIgnoreCase);
        var closing = before.LastIndexOf($"</{name}", StringComparison.OrdinalIgnoreCase);
        return opening > closing;
    }

    /// <summary>Le numéro de la ligne (à partir de 1) où se trouve le caractère <paramref name="offset"/>.</summary>
    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    /// <summary>Le nombre de lignes que couvre <c>text[from..to]</c> : une de plus que ses fins de ligne.</summary>
    private static int CountLines(string text, int from, int to)
    {
        var lines = 1;
        for (var i = from; i < to && i < text.Length; i++)
        {
            if (text[i] == '\n') lines++;
        }
        return lines;
    }

    /// <summary>Le numéro de la ligne (à partir de 1) où le texte engendré se trouve en ce moment.</summary>
    private static int Lines(StringBuilder text)
    {
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lines++;
        }
        return lines;
    }

    private static bool Split(SourceText source, StringBuilder blocks, StringBuilder markup, DiagnosticBag diagnostics, List<(int PaperLine, int Lines)> blockRanges)
    {
        var text = source.Content;
        for (var i = 0; i < text.Length;)
        {
            if (!text.AsSpan(i).StartsWith("@rgl", StringComparison.Ordinal) || (i > 0 && IsName(text[i - 1])))
            {
                markup.Append(text[i++]);
                continue;
            }
            var cursor = i + 4;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length || text[cursor] != '{')
            {
                diagnostics.Error("« @rgl » attend un bloc entre accolades", source.Path);
                return false;
            }
            var end = ClosingBrace(text, cursor);
            if (end < 0)
            {
                diagnostics.Error("bloc « @rgl » non fermé", source.Path);
                return false;
            }
            blockRanges.Add((LineOf(text, cursor + 1), CountLines(text, cursor + 1, end)));
            blocks.Append(text, cursor + 1, end - cursor - 1).Append('\n');
            i = end + 1;
            if (i < text.Length && text[i] == '\r') i++;
            if (i < text.Length && text[i] == '\n') i++;
        }
        return true;
    }

    private static int ClosingBrace(string text, int open)
    {
        var depth = 0;
        char quote = '\0';
        var escaped = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool UnwrapHtml(string written, out string inner)
    {
        if (written.StartsWith("html(", StringComparison.Ordinal) && written.EndsWith(')'))
        {
            inner = written[5..^1].Trim();
            return true;
        }
        inner = written;
        return false;
    }

    private static bool TryImport(string expression, string paperPath, string projectRoot, DiagnosticBag diagnostics, out string? content, out string? path)
    {
        content = null;
        path = null;
        var match = ImportFile().Match(expression);
        if (!match.Success) return false;
        var outside = match.Groups[1].Success;
        var name = match.Groups[3].Value;
        var paperDirectory = Path.GetDirectoryName(Path.GetFullPath(paperPath))!;
        var box = AncestorBox(paperDirectory, projectRoot);
        var root = outside || box is null ? projectRoot : box;
        var candidates = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) == name && (box is not null && !outside || !InsideBox(file, projectRoot)))
            .Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
        {
            diagnostics.Error($"fichier « {name} » introuvable pour l'expression import file", paperPath, DiagnosticCodes.ImportNotFound);
            return true;
        }
        path = candidates[0];
        var imported = SourceText.FromPath(path, diagnostics);
        if (imported is null) return true;
        content = imported.Content;
        return true;
    }

    private static string? AncestorBox(string directory, string root)
    {
        for (var current = new DirectoryInfo(directory); current is not null && current.FullName.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); current = current.Parent)
            if (current.Name.EndsWith(".box", StringComparison.OrdinalIgnoreCase)) return current.FullName;
        return null;
    }

    private static bool InsideBox(string path, string root) => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.EndsWith(".box", StringComparison.OrdinalIgnoreCase));

    private static bool IsName(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Le nom implicite de la tête de page (titre, scripts, variables d'environnement).</summary>
    public const string PageName = "page";

    /// <summary>Le gear sans paramètre exécuté quand l'état d'une page est créé.</summary>
    public const string LoadGear = "load";

    /// <summary>Remplace chaque nom d'état par son champ (<c>count</c> devient <c>state.count</c>), sauf dans le texte des chaînes : un mot comme « page » écrit entre guillemets reste intact, et seules les interpolations <c>{…}</c> d'un gabarit sont réécrites.</summary>
    private static string RewriteState(string expression, IReadOnlyList<PaperReactive> reactives)
    {
        var fields = reactives.Where(r => r.Name != PageName).ToArray();
        if (fields.Length == 0) return expression;
        var names = string.Join('|', fields.Select(r => Regex.Escape(r.Name)));
        var name = new Regex($@"(?<![.\w])(?:{names})\b");
        var result = new StringBuilder(expression.Length + 16);
        RewriteCode(expression, 0, expression.Length, name, match => "state." + match.Value, result);
        return result.ToString();
    }

    /// <summary>Le code sans le texte des chaînes : ce qui est entre guillemets ou entre apostrophes disparaît, et d'un gabarit ne restent que ses interpolations <c>{…}</c>.</summary>
    private static string CodeOnly(string text)
    {
        var result = new StringBuilder(text.Length);
        void Scan(int from, int to)
        {
            var i = from;
            while (i < to)
            {
                var c = text[i];
                if (c is '\'' or '"')
                {
                    i++;
                    while (i < to && text[i] != c) i += text[i] == '\\' ? 2 : 1;
                    i++;
                }
                else if (c == '`')
                {
                    i++;
                    while (i < to && text[i] != '`')
                    {
                        if (text[i] == '\\') i += 2;
                        else if (text[i] == '{')
                        {
                            var close = ClosingBrace(text, i);
                            if (close < 0 || close >= to) close = to - 1;
                            result.Append(' ');
                            Scan(i + 1, close);
                            i = close + 1;
                        }
                        else i++;
                    }
                    i++;
                }
                else result.Append(text[i++]);
            }
        }
        Scan(0, text.Length);
        return result.ToString();
    }

    /// <summary>Réécrit chaque correspondance de <paramref name="name"/> par <paramref name="map"/>, hors du texte des chaînes (voir <see cref="RewriteState"/>).</summary>
    private static void RewriteCode(string text, int from, int to, Regex name, MatchEvaluator map, StringBuilder result)
    {
        var plain = from;
        void Flush(int end)
        {
            result.Append(name.Replace(text[plain..end], map));
            plain = end;
        }
        var i = from;
        while (i < to)
        {
            var c = text[i];
            if (c is '\'' or '"')
            {
                Flush(i);
                var end = i + 1;
                while (end < to && text[end] != c) end += text[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, to);
                result.Append(text, i, end - i);
                plain = i = end;
            }
            else if (c == '`')
            {
                Flush(i);
                result.Append('`');
                var end = i + 1;
                while (end < to && text[end] != '`')
                {
                    if (text[end] == '\\') { result.Append(text, end, Math.Min(2, to - end)); end += 2; }
                    else if (text[end] == '{')
                    {
                        var close = ClosingBrace(text, end);
                        if (close < 0 || close >= to) close = to - 1;
                        result.Append('{');
                        RewriteCode(text, end + 1, close, name, map, result);
                        result.Append('}');
                        end = close + 1;
                    }
                    else result.Append(text[end++]);
                }
                if (end < to) result.Append('`');
                plain = i = Math.Min(end + 1, to);
            }
            else i++;
        }
        Flush(to);
    }

    private static string TransformStatefulGears(string source, IReadOnlyList<PaperReactive> reactives, bool usesPage)
    {
        source = RewriteState(source, reactives);
        var implicitParameters = usesPage ? "mut State state, mut PageHead page" : "mut State state";
        return GearHeader().Replace(source, match =>
        {
            var parameters = match.Groups[1].Value.Trim();
            return match.Value[..(match.Groups[1].Index - match.Index)] + (parameters.Length == 0 ? implicitParameters : implicitParameters + ", " + parameters) + ")";
        });
    }

    private static HashSet<string> WrittenReactives(string source, string gear, IReadOnlyList<PaperReactive> reactives)
    {
        var header = Regex.Match(source, $@"\bgear(?:\s+pure)?\s+{Regex.Escape(gear)}\s*\([^)]*\)\s*:[^{{=;]+\{{");
        if (!header.Success) return [];
        var open = source.IndexOf('{', header.Index + header.Length - 1);
        var close = ClosingBrace(source, open);
        if (close < 0) return [];
        var body = source[(open + 1)..close];
        return reactives.Where(reactive => Regex.IsMatch(body,
                $@"(?:(?<![.\w]){Regex.Escape(reactive.Name)}(?:\.[A-Za-z_]\w*|\[[^\]]*\])*\s*(?:<-|\+<-|-<-|\*<-|/<-|<-\|)|\b(?:increment|decrement)\s+{Regex.Escape(reactive.Name)}\b|(?<![.\w]){Regex.Escape(reactive.Name)}\.[A-Za-z_]\w*\s*\()"))
            .Select(reactive => reactive.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Vrai si le projet contient au moins une source Paper, sans parcourir sa sortie.</summary>
    public static bool ProjectUsesPaper(string root) => Directory.EnumerateFiles(root, "*" + Extension, new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    }).Any(path => !Path.GetRelativePath(root, path).Replace('\\', '/').StartsWith("out/", StringComparison.OrdinalIgnoreCase));

    private static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value) result.Append(c switch
        {
            '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
            _ when char.IsControl(c) => $"\\u{{{(int)c:X}}}", _ => c.ToString(),
        });
        return result.Append('"').ToString();
    }

    [GeneratedRegex("""^import\s+(?:(\[\.\.\])\s+)?file\s+(['"])([^'"]+)\2$""")]
    private static partial Regex ImportFile();

    [GeneratedRegex(@"^import\b")]
    private static partial Regex ImportStart();

    [GeneratedRegex(@"\breactive\s+(?:(mut)\s+)?(.+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([^;]+);", RegexOptions.Singleline)]
    private static partial Regex ReactiveDeclaration();

    [GeneratedRegex(@"\bgear(?:\s+pure)?\s+[A-Za-z_][A-Za-z0-9_]*\s*\(([^)]*)\)")]
    private static partial Regex GearHeader();

    [GeneratedRegex(@"\bgear(?:\s+pure)?\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)")]
    private static partial Regex GearSignature();

    [GeneratedRegex(@"(?:^|(?<=[;{}]))[ \t]*param\s+(.+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*([^;]+))?;", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex ParamDeclaration();

    [GeneratedRegex(@"(?:^|(?<=[;{}]))[ \t]*param\b", RegexOptions.Multiline)]
    private static partial Regex ParamLeftover();

    [GeneratedRegex(@"@@(.*?)@@", RegexOptions.Singleline)]
    private static partial Regex InterpolationText();

    [GeneratedRegex(@"(?<![.\w'""`])page\b")]
    private static partial Regex PageUse();

    [GeneratedRegex("@(click|input|change|submit|keydown|keyup)\\s*=\\s*\"([A-Za-z_][A-Za-z0-9_]*)\"")]
    private static partial Regex EventAttribute();
}
