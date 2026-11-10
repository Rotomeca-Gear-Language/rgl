using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Paper;
using PaperComponentInfo = Rgl.Paper.PaperCompiler.PaperComponentInfo;

namespace Rgl.Lsp;

/// <summary>
/// Le markup d'une page <c>.paper</c>, que la projection RGL ne voit pas : les balises de composants (<c>&lt;Carte titre="x" /&gt;</c>) et la directive <c>@layout Nom;</c>.
/// Il se cherche les fichiers <c>.paper</c> comme le compilateur (par leur nom, dans le projet ou la box de la page) et répond aux mêmes demandes que le reste de l'éditeur :
/// diagnostics, complétion, survol et définition. Toutes les places sont des places du texte de la page.
/// </summary>
internal static partial class PaperMarkup
{
    /// <summary>Un problème à signaler sur <c>[Start, End)</c>.</summary>
    internal sealed record Problem(int Start, int End, string Message);

    /// <summary>Un survol sur <c>[Start, End)</c>.</summary>
    internal sealed record HoverText(int Start, int End, string Markdown);

    /// <summary>L'endroit d'un autre fichier où mène une définition.</summary>
    internal sealed record Target(string Path, int Start, int End);

    private sealed class Lookup(string pagePath, Func<string, string?> read)
    {
        private readonly Dictionary<string, string?> _paths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Text, PaperComponentInfo Info)?> _infos = new(StringComparer.OrdinalIgnoreCase);

        public string PagePath { get; } = pagePath;

        public string? Find(string name, string label)
        {
            if (_paths.TryGetValue(label + ":" + name, out var known)) return known;
            var found = PaperCompiler.FindPaperFile(name, label, PagePath, ProjectIndex.RootOf(PagePath), new DiagnosticBag());
            _paths[label + ":" + name] = found;
            return found;
        }

        public (string Text, PaperComponentInfo Info)? Read(string path)
        {
            if (_infos.TryGetValue(path, out var known)) return known;
            (string, PaperComponentInfo)? result = null;
            try
            {
                var text = read(Path.GetFullPath(path)) ?? File.ReadAllText(path);
                result = (text, PaperCompiler.ReadComponentInterface(text));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            _infos[path] = result;
            return result;
        }
    }

    /// <summary>Les composants et layouts que la page peut nommer : les <c>.paper</c> de son projet (ou de sa box), hors dossiers cachés, <c>out</c> et boxes voisines, sauf la page elle-même.</summary>
    internal static IReadOnlyList<(string Name, string Path)> Files(string pagePath)
    {
        var root = ProjectIndex.RootOf(pagePath);
        var scope = root;
        for (var current = Path.GetDirectoryName(Path.GetFullPath(pagePath)); current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
        {
            if (current.EndsWith(".box", StringComparison.OrdinalIgnoreCase))
            {
                scope = current;
                break;
            }
        }

        List<(string Name, string Path)> result = [];
        var self = Path.GetFullPath(pagePath);
        void Walk(string directory)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory, "*.paper").Order(StringComparer.Ordinal))
                    if (Path.GetExtension(file).Equals(".paper", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase)
                        && result.All(r => r.Name != Path.GetFileNameWithoutExtension(file)))
                        result.Add((Path.GetFileNameWithoutExtension(file), file));
                foreach (var child in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') || name.EndsWith(".box", StringComparison.OrdinalIgnoreCase) || name is "out" or "node_modules") continue;
                    Walk(child);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Walk(scope);
        return result;
    }

    /// <summary>Les textes des layouts d'une page, du plus proche au plus extérieur (la chaîne des <c>@layout</c>).</summary>
    internal static IReadOnlyList<string> LayoutTexts(string pagePath, string pageText, Func<string, string?> read)
    {
        List<string> texts = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(pagePath) };
        var currentPath = pagePath;
        var currentText = pageText;
        for (var depth = 0; depth < 8; depth++)
        {
            if (PaperCompiler.LayoutDirectives(currentText).FirstOrDefault().Name is not { } name) break;
            var file = PaperCompiler.FindPaperFile(name, "layout", currentPath, ProjectIndex.RootOf(currentPath), new DiagnosticBag());
            if (file is null || !visited.Add(Path.GetFullPath(file))) break;
            try { currentText = read(Path.GetFullPath(file)) ?? File.ReadAllText(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { break; }
            currentPath = file;
            texts.Add(currentText);
        }
        return texts;
    }

    public static IReadOnlyList<Problem> Diagnose(string pagePath, string pageText, Func<string, string?> read)
    {
        var lookup = new Lookup(pagePath, read);
        List<Problem> problems = [];
        foreach (var (name, start, end) in PaperCompiler.LayoutDirectives(pageText))
        {
            if (lookup.Find(name, "layout") is not { } file)
                problems.Add(new Problem(start, end, $"layout « {name} » introuvable : aucun fichier {name}.paper dans le projet"));
            else if (lookup.Read(file) is { Info.HasSlot: false })
                problems.Add(new Problem(start, end, $"le layout « {name} » n'a pas de <slot /> : il n'y a nulle part où mettre la page"));
        }

        foreach (var tag in PaperCompiler.ScanComponentTags(pageText))
        {
            if (lookup.Find(tag.Name, "composant") is not { } file)
            {
                problems.Add(new Problem(tag.NameStart, tag.NameEnd, $"composant « {tag.Name} » introuvable : aucun fichier {tag.Name}.paper dans le projet"));
                continue;
            }
            if (lookup.Read(file) is not { } component) continue;
            var parameters = component.Info.Parameters;
            foreach (var (attribute, span) in tag.Attributes)
            {
                if (parameters.All(p => p.Name != attribute))
                {
                    var known = parameters.Count == 0 ? "ce composant n'a aucun param" : "params : " + string.Join(", ", parameters.Select(p => p.Name));
                    problems.Add(new Problem(span.Start, span.End, $"le composant « {tag.Name} » n'a pas de paramètre « {attribute} » ({known})"));
                }
            }
            foreach (var parameter in parameters.Where(p => p.Default is null && !tag.Attributes.ContainsKey(p.Name)))
                problems.Add(new Problem(tag.NameStart, tag.NameEnd, $"le composant « {tag.Name} » demande le paramètre « {parameter.Name} » : écris {parameter.Name}=\"…\" ou {parameter.Name}=@@ expression @@"));
            if (tag.HasContent && !component.Info.HasSlot)
                problems.Add(new Problem(tag.NameStart, tag.NameEnd, $"le composant « {tag.Name} » n'a pas de <slot /> : son contenu n'a nulle part où aller"));
        }
        return problems;
    }

    /// <summary>
    /// Les propositions à cette place de la page, hors du RGL : les composants après <c>&lt;</c>, les <c>param</c> d'un composant dans sa balise, les layouts après
    /// <c>@layout</c> ; <see langword="null"/> si rien ne s'applique ici.
    /// </summary>
    public static IReadOnlyList<CompletionItem>? Complete(string pagePath, string pageText, int offset, Func<string, string?> read)
    {
        var masked = PaperCompiler.MaskBlocks(pageText);
        if (offset < 0 || offset > masked.Length) return null;
        var lookup = new Lookup(pagePath, read);
        var lineStart = offset == 0 ? 0 : masked.LastIndexOf('\n', offset - 1) + 1;
        var linePrefix = masked[lineStart..offset];

        if (LayoutPrefix().IsMatch(linePrefix))
        {
            return [.. Files(pagePath).Where(f => lookup.Read(f.Path) is { Info.HasSlot: true })
                .Select(f => new CompletionItem(f.Name, CompletionKind.Module, "layout", $"Layout `{f.Name}.paper`.", InsertText: f.Name + ";"))];
        }
        if (DirectivePrefix().IsMatch(linePrefix))
        {
            return
            [
                new CompletionItem("layout", CompletionKind.Keyword, "@layout Nom;", "Enveloppe la page dans un layout : le markup de la page prend la place de son `<slot />`.", InsertText: "layout ${1:Main};"),
                new CompletionItem("rgl", CompletionKind.Keyword, "@rgl { … }", "Un bloc de RGL : gears, constantes, `reactive` et `param`.", InsertText: "rgl {\n    $0\n}"),
            ];
        }

        var open = masked.LastIndexOf('<', Math.Max(0, offset - 1));
        if (offset == 0 || open < 0 || open >= offset) return null;
        var inside = masked[(open + 1)..offset];
        if (inside.Contains('>') || inside.Contains('<') || (inside.Length > 0 && !char.IsAsciiLetterUpper(inside[0]))) return null;

        var nameLength = inside.TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c == '_').Count();
        if (nameLength == inside.Length) return [.. Files(pagePath).Select(f => _ComponentItem(f, lookup)).OfType<CompletionItem>()];

        // Dans la balise, après le nom : les param qui n'ont pas encore de valeur, sauf à l'intérieur d'une valeur.
        var rest = inside[nameLength..];
        if (rest.Count(c => c == '"') % 2 == 1 || rest.Count(c => c == '\'') % 2 == 1 || Regex.Matches(rest, "@@").Count % 2 == 1) return [];
        if (lookup.Find(inside[..nameLength], "composant") is not { } file || lookup.Read(file) is not { } component) return [];
        var used = AttributeName().Matches(rest).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return [.. component.Info.Parameters.Where(p => !used.Contains(p.Name)).Select(p => new CompletionItem(p.Name, CompletionKind.Field,
            p.Type + (p.Default is null ? "" : " = " + p.Default), $"Paramètre du composant `{inside[..nameLength]}`.", SortText: p.Default is null ? "0_" + p.Name : "1_" + p.Name,
            InsertText: p.Name + "=" + _ValueSnippet(p.Type, 1)))];
    }

    private static CompletionItem? _ComponentItem((string Name, string Path) file, Lookup lookup)
    {
        if (lookup.Read(file.Path) is not { } component) return null;
        var required = component.Info.Parameters.Where(p => p.Default is null).Select((p, i) => p.Name + "=" + _ValueSnippet(p.Type, i + 1)).ToArray();
        var attributes = required.Length == 0 ? "" : " " + string.Join(" ", required);
        var insert = component.Info.HasSlot ? $"{file.Name}{attributes}>$0</{file.Name}>" : $"{file.Name}{attributes} />$0";
        return new CompletionItem(file.Name, CompletionKind.Class, _Signature(file.Name, component.Info), $"Composant `{file.Name}.paper`.", InsertText: insert);
    }

    private static string _ValueSnippet(string type, int index) => type == "string" ? $"\"${index}\"" : $"@@ ${index} @@";

    private static string _Signature(string name, PaperComponentInfo info) =>
        name + "(" + string.Join(", ", info.Parameters.Select(p => p.Name + ": " + p.Type + (p.Default is null ? "" : " = " + p.Default))) + ")";

    public static HoverText? HoverAt(string pagePath, string pageText, int offset, Func<string, string?> read)
    {
        var lookup = new Lookup(pagePath, read);
        foreach (var (name, start, end) in PaperCompiler.LayoutDirectives(pageText))
        {
            if (offset < start || offset > end) continue;
            return lookup.Find(name, "layout") is { } file
                ? new HoverText(start, end, $"**{name}** — layout\n\n`{Path.GetRelativePath(ProjectIndex.RootOf(pagePath), file).Replace('\\', '/')}`\n\nLe markup de la page prend la place de son `<slot />`.")
                : null;
        }

        foreach (var tag in PaperCompiler.ScanComponentTags(pageText))
        {
            if (lookup.Find(tag.Name, "composant") is not { } file || lookup.Read(file) is not { } component) continue;
            if (offset >= tag.NameStart && offset <= tag.NameEnd)
            {
                var content = component.Info.HasSlot ? "\n\nLe contenu de la balise prend la place de son `<slot />`." : "";
                return new HoverText(tag.NameStart, tag.NameEnd, $"```paper\n<{_Signature(tag.Name, component.Info)}>\n```\nComposant `{Path.GetRelativePath(ProjectIndex.RootOf(pagePath), file).Replace('\\', '/')}`.{content}");
            }
            foreach (var (attribute, span) in tag.Attributes)
            {
                if (offset < span.Start || offset > span.End || component.Info.Parameters.FirstOrDefault(p => p.Name == attribute) is not { } parameter) continue;
                return new HoverText(span.Start, span.End, $"```rgl\nparam {parameter.Type} {parameter.Name}{(parameter.Default is null ? "" : ": " + parameter.Default)}\n```\nParamètre du composant `{tag.Name}`" + (parameter.Default is null ? " (obligatoire)." : "."));
            }
        }
        return null;
    }

    public static Target? DefinitionAt(string pagePath, string pageText, int offset, Func<string, string?> read)
    {
        var lookup = new Lookup(pagePath, read);
        foreach (var (name, start, end) in PaperCompiler.LayoutDirectives(pageText))
        {
            if (offset >= start && offset <= end) return lookup.Find(name, "layout") is { } file ? new Target(file, 0, 0) : null;
        }

        foreach (var tag in PaperCompiler.ScanComponentTags(pageText))
        {
            if (lookup.Find(tag.Name, "composant") is not { } file || lookup.Read(file) is not { } component) continue;
            if (offset >= tag.NameStart && offset <= tag.NameEnd) return new Target(file, 0, 0);
            foreach (var (attribute, span) in tag.Attributes)
            {
                if (offset >= span.Start && offset <= span.End && component.Info.Parameters.FirstOrDefault(p => p.Name == attribute) is { } parameter)
                    return new Target(file, parameter.NameStart, parameter.NameEnd);
            }
        }
        return null;
    }

    [GeneratedRegex(@"^[ \t]*@layout[ \t]+[A-Za-z_]*\w*$")]
    private static partial Regex LayoutPrefix();

    [GeneratedRegex(@"^[ \t]*@[a-z]*$")]
    private static partial Regex DirectivePrefix();

    [GeneratedRegex(@"([A-Za-z_][\w-]*)\s*=")]
    private static partial Regex AttributeName();
}
