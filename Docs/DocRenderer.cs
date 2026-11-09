using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Rgl.Docs;

/// <summary>Met en forme une documentation : le Markdown d'une balise, et la fiche complète que l'éditeur montre au survol.</summary>
internal static partial class DocRenderer
{
    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex _LineBreaks();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex _BlankLines();

    /// <summary>Le contenu de <paramref name="element"/> en Markdown : <c>&lt;c&gt;</c> en code, <c>&lt;code&gt;</c> en bloc, <c>&lt;see&gt;</c> en nom de symbole.</summary>
    public static string Markdown(XElement element) => _Tidy(_Children(element));

    /// <summary>
    /// La fiche d'une déclaration : le résumé, les remarques, puis les paramètres, le résultat, les échecs, les exemples et les renvois.
    /// <see langword="null"/> quand il n'y a rien à montrer.
    /// </summary>
    public static string? Hover(DocComment comment)
    {
        var parts = new List<string>();
        if (comment.Summary is { Markdown.Length: > 0 } summary) parts.Add(summary.Markdown);
        if (comment.Remarks is { Markdown.Length: > 0 } remarks) parts.Add(remarks.Markdown);
        if (comment.TypeParams.Count > 0)
            parts.Add("**Paramètres de type**\n" + string.Join('\n', comment.TypeParams.Select(p => _Item(p.Name, p.Text.Markdown))));
        if (comment.Params.Count > 0)
            parts.Add("**Paramètres**\n" + string.Join('\n', comment.Params.Select(p => _Item(p.Name, p.Text.Markdown))));
        if (comment.Returns is { Markdown.Length: > 0 } returns) parts.Add("**Retourne** " + returns.Markdown.ReplaceLineEndings(" "));
        if (comment.Fails.Count > 0)
            parts.Add("**Échecs**\n" + string.Join('\n', comment.Fails.Select(f => _Item(f.Type, f.Text.Markdown))));
        foreach (var example in comment.Examples) parts.Add("**Exemple**\n\n" + example.Markdown);
        foreach (var custom in comment.Custom) parts.Add("**" + custom.Name + "**\n\n" + custom.Text.Markdown);
        if (comment.SeeAlso.Count > 0) parts.Add("**Voir aussi** " + string.Join(", ", comment.SeeAlso.Select(s => $"`{s.Cref}`")));
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>La description d'un paramètre pour l'aide à la saisie ; <see langword="null"/> s'il n'est pas décrit.</summary>
    public static string? ParameterText(DocComment comment, string name) =>
        comment.Params.FirstOrDefault(p => p.Name == name) is { Text.Markdown.Length: > 0 } parameter ? parameter.Text.Markdown : null;

    private static string _Item(string name, string text) => text.Length == 0 ? $"- `{name}`" : $"- `{name}` — {text.ReplaceLineEndings(" ")}";

    private static string _Children(XContainer container) => string.Concat(container.Nodes().Select(_Node));

    private static string _Node(XNode node) => node switch
    {
        XCData cdata => cdata.Value,
        XText text => _LineBreaks().Replace(text.Value, "\n"),
        XElement element => _Element(element),
        _ => "",
    };

    private static string _Element(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "lang":
                return "";
            case "c":
                return "`" + element.Value.Trim() + "`";
            case "code":
                return "\n\n```" + (element.Attribute("language")?.Value ?? "rgl") + "\n" + _Dedent(element.Value) + "\n```\n\n";
            case "see" or "seealso":
                var target = element.Attribute("cref")?.Value ?? element.Attribute("langword")?.Value ?? element.Value;
                return "`" + target.Trim() + "`";
            case "para":
                return "\n\n" + _Children(element).Trim() + "\n\n";
            case "list":
                var ordered = element.Attribute("type")?.Value == "number";
                var items = element.Elements("item").Select((item, i) => (ordered ? $"{i + 1}. " : "- ") + _ListItem(item)).ToList();
                return "\n\n" + string.Join('\n', items) + "\n\n";
            default:
                return _Children(element);
        }
    }

    private static string _ListItem(XElement item)
    {
        var term = item.Element("term");
        var description = item.Element("description");
        if (term is null && description is null) return _Tidy(_Children(item)).ReplaceLineEndings(" ");
        var text = description is null ? "" : _Tidy(_Children(description)).ReplaceLineEndings(" ");
        return term is null ? text : $"**{_Tidy(_Children(term))}**" + (text.Length == 0 ? "" : " — " + text);
    }

    /// <summary>Retire les lignes vides du début et de la fin et le retrait commun : un bloc de code garde sa mise en page.</summary>
    private static string _Dedent(string code)
    {
        var lines = code.ReplaceLineEndings("\n").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..].TrimEnd() : l.TrimEnd()));
    }

    private static string _Tidy(string text) => _BlankLines().Replace(text, "\n\n").Trim();
}
