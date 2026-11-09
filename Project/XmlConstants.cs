using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Rgl.Project;

/// <summary>
/// <c>import xml layout;</c> : le contenu de <c>layout.xml</c> devient une constante <c>XmlNode</c> (celui de la std), par un texte RGL engendré
/// (<c>var layout: new std.XmlNode {name: "a", attributes: ["k": "v"], children: [], text: ""};</c>) que le chargeur analyse comme n'importe quelle
/// déclaration. Même sous-ensemble que la lecture à l'exécution : commentaires et instructions de traitement ignorés, une balise DOCTYPE refusée, les
/// noms gardent leur préfixe d'espace de noms, le texte d'un élément est celui qui y est écrit directement (vide s'il n'y a que des espaces).
/// </summary>
internal static class XmlConstants
{
    /// <summary>
    /// La déclaration <c>var nom: new std.XmlNode {…};</c> qui écrit <paramref name="xml"/>, sur plusieurs lignes ; <see langword="null"/> après avoir
    /// signalé l'erreur à <paramref name="error"/>.
    /// </summary>
    public static string? ToSource(string xml, string name, Action<string> error)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            error($"XML invalide, ligne {exception.LineNumber}, colonne {exception.LinePosition} : {exception.Message}");
            return null;
        }

        var text = new StringBuilder();
        text.Append("var ").Append(name).Append(": ");
        WriteElement(text, document.Root!, 0);
        text.Append(";\n");
        return text.ToString();
    }

    private static string Indent(int depth) => new(' ', depth * 4);

    /// <summary>Le nom d'un élément ou d'un attribut tel qu'il est écrit : son préfixe d'espace de noms compris.</summary>
    private static string Written(XElement scope, XName name)
    {
        if (name.Namespace == XNamespace.None) return name.LocalName;
        if (name.Namespace == XNamespace.Xmlns) return name.LocalName == "xmlns" ? "xmlns" : "xmlns:" + name.LocalName;
        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
    }

    private static void WriteElement(StringBuilder text, XElement element, int depth)
    {
        text.Append("new std.XmlNode {\n");
        text.Append(Indent(depth + 1)).Append("name: ");
        JsonConstants.WriteString(text, Written(element, element.Name));
        text.Append(",\n");

        var attributes = element.Attributes().ToArray();
        text.Append(Indent(depth + 1)).Append("attributes: ");
        if (attributes.Length == 0) text.Append("[:]");
        else
        {
            text.Append("[\n");
            for (var i = 0; i < attributes.Length; ++i)
            {
                text.Append(Indent(depth + 2));
                JsonConstants.WriteString(text, Written(element, attributes[i].Name));
                text.Append(": ");
                JsonConstants.WriteString(text, attributes[i].Value);
                text.Append(i + 1 < attributes.Length ? ",\n" : "\n");
            }
            text.Append(Indent(depth + 1)).Append(']');
        }
        text.Append(",\n");

        var children = element.Elements().ToArray();
        text.Append(Indent(depth + 1)).Append("children: ");
        if (children.Length == 0) text.Append("[]");
        else
        {
            text.Append("[\n");
            for (var i = 0; i < children.Length; ++i)
            {
                text.Append(Indent(depth + 2));
                WriteElement(text, children[i], depth + 2);
                text.Append(i + 1 < children.Length ? ",\n" : "\n");
            }
            text.Append(Indent(depth + 1)).Append(']');
        }
        text.Append(",\n");

        // Le texte écrit directement dans l'élément, CDATA comprise ; des espaces seulement ne sont pas du texte.
        var direct = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        text.Append(Indent(depth + 1)).Append("text: ");
        JsonConstants.WriteString(text, string.IsNullOrWhiteSpace(direct) ? "" : direct);
        text.Append('\n').Append(Indent(depth)).Append('}');
    }
}
