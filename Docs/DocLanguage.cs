using System.Globalization;
using System.Xml.Linq;

namespace Rgl.Docs;

/// <summary>Sélectionne une traduction parmi les blocs frères, sans modifier leur XML.</summary>
internal static class DocLanguage
{
    public static XElement? Select(IEnumerable<XElement> elements, CultureInfo? culture = null)
    {
        var languages = elements.Where(e => e.Name == "lang").ToArray();
        culture ??= CultureInfo.CurrentUICulture;
        XElement? Match(string name) => languages.FirstOrDefault(e =>
            string.Equals(e.Attribute("name")?.Value.Trim(), name, StringComparison.OrdinalIgnoreCase));
        for (var current = culture; current != CultureInfo.InvariantCulture; current = current.Parent)
            if (Match(current.Name) is { } match) return match;
        return Match("en") ?? languages.FirstOrDefault(e =>
            e.Attribute("name")?.Value.Trim().StartsWith("en-", StringComparison.OrdinalIgnoreCase) == true)
            ?? languages.FirstOrDefault();
    }

    public static IEnumerable<XNode> Nodes(XContainer container)
    {
        var selected = Select(container.Elements());
        foreach (var node in container.Nodes())
        {
            if (node is XElement { Name.LocalName: "lang" } language)
            {
                if (ReferenceEquals(language, selected))
                    foreach (var child in language.Nodes()) yield return child;
            }
            else yield return node;
        }
    }

    public static IEnumerable<XElement> Descendants(XContainer container)
    {
        return Nodes(container).OfType<XElement>().SelectMany(element => element.DescendantsAndSelf());
    }
}
