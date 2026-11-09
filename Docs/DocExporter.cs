using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Rgl.Docs;

/// <summary>
/// Écrit la référence de l'API d'un projet : <c>api.json</c> (pour un site ou un outil) et <c>api.xml</c> (le XML des commentaires, rangé par
/// déclaration). Le format est versionné et ne dépend pas du C++ généré.
/// </summary>
internal static class DocExporter
{
    /// <summary>Version du format écrit ; elle change quand un champ est retiré ou change de sens.</summary>
    public const int FormatVersion = 1;

    /// <summary>La référence en JSON.</summary>
    /// <param name="project">Le nom du projet.</param>
    /// <param name="entries">Les déclarations à écrire, tous fichiers confondus.</param>
    public static string Json(string project, IReadOnlyList<DocEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", FormatVersion);
            json.WriteString("project", project);
            json.WriteStartArray("modules");
            foreach (var module in entries.GroupBy(e => e.Module))
            {
                json.WriteStartObject();
                json.WriteString("name", module.Key);
                json.WriteStartArray("entries");
                foreach (var entry in module) _WriteEntry(json, entry);
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void _WriteEntry(Utf8JsonWriter json, DocEntry entry)
    {
        json.WriteStartObject();
        json.WriteString("id", entry.Id);
        json.WriteString("kind", _Kind(entry.Kind));
        json.WriteString("name", entry.Name);
        if (entry.Container is not null) json.WriteString("container", entry.Container);
        json.WriteString("signature", entry.Signature);
        json.WriteBoolean("public", entry.IsPublic);
        json.WriteNumber("line", entry.NameSpan.StartPosition.Line.Value);
        json.WriteNumber("column", entry.NameSpan.StartPosition.Column.Value);

        if (entry.Comment is { } comment)
        {
            json.WriteStartObject("doc");
            _Text(json, "summary", comment.Summary);
            _Text(json, "remarks", comment.Remarks);
            _Named(json, "typeParams", comment.TypeParams);
            _Named(json, "params", comment.Params);
            _Text(json, "returns", comment.Returns);
            if (comment.Fails.Count > 0)
            {
                json.WriteStartArray("fails");
                foreach (var fails in comment.Fails)
                {
                    json.WriteStartObject();
                    json.WriteString("type", fails.Type);
                    json.WriteString("text", fails.Text.Markdown);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            if (comment.Examples.Count > 0)
            {
                json.WriteStartArray("examples");
                foreach (var example in comment.Examples) json.WriteStringValue(example.Markdown);
                json.WriteEndArray();
            }
            if (comment.SeeAlso.Count > 0)
            {
                json.WriteStartArray("seeAlso");
                foreach (var see in comment.SeeAlso) json.WriteStringValue(see.Cref);
                json.WriteEndArray();
            }
            if (comment.Custom.Count > 0)
            {
                json.WriteStartArray("custom");
                foreach (var custom in comment.Custom)
                {
                    json.WriteStartObject();
                    json.WriteString("name", custom.Name);
                    json.WriteString("text", custom.Text.Markdown);
                    json.WriteString("xml", custom.Xml);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            if (comment.LocalizedXml is { } localized) json.WriteString("localizedXml", localized);
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    private static void _Text(Utf8JsonWriter json, string name, DocSection? section)
    {
        if (section is { Markdown.Length: > 0 }) json.WriteString(name, section.Markdown);
    }

    private static void _Named(Utf8JsonWriter json, string name, IReadOnlyList<DocNamed> items)
    {
        if (items.Count == 0) return;
        json.WriteStartArray(name);
        foreach (var item in items)
        {
            json.WriteStartObject();
            json.WriteString("name", item.Name);
            json.WriteString("text", item.Text.Markdown);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    /// <summary>La référence en XML : un <c>&lt;member&gt;</c> par déclaration, avec les balises du commentaire telles qu'écrites.</summary>
    /// <param name="project">Le nom du projet.</param>
    /// <param name="entries">Les déclarations à écrire.</param>
    public static string Xml(string project, IReadOnlyList<DocEntry> entries)
    {
        var members = new XElement("members");
        foreach (var entry in entries)
        {
            var member = new XElement("member",
                new XAttribute("id", entry.Id), new XAttribute("kind", _Kind(entry.Kind)), new XAttribute("name", entry.Name),
                new XAttribute("signature", entry.Signature), new XAttribute("public", entry.IsPublic ? "true" : "false"));
            if (entry.Container is not null) member.SetAttributeValue("container", entry.Container);

            if (entry.Comment is { } comment)
            {
                if (comment.LocalizedXml is { } localized)
                    member.Add(XElement.Parse("<doc>" + localized + "</doc>").Nodes());
                else
                {
                    _Add(member, "summary", comment.Summary);
                    _Add(member, "remarks", comment.Remarks);
                    foreach (var p in comment.TypeParams) _Add(member, "typeparam", p.Text, ("name", p.Name));
                    foreach (var p in comment.Params) _Add(member, "param", p.Text, ("name", p.Name));
                    _Add(member, "returns", comment.Returns);
                    foreach (var f in comment.Fails) _Add(member, "fails", f.Text, ("type", f.Type));
                    foreach (var e in comment.Examples) _Add(member, "example", e);
                    foreach (var s in comment.SeeAlso) member.Add(new XElement("seealso", new XAttribute("cref", s.Cref)));
                    foreach (var custom in comment.Custom) member.Add(XElement.Parse(custom.Xml));
                }
            }
            members.Add(member);
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("api", new XAttribute("version", FormatVersion), new XAttribute("project", project), members));
        return document.Declaration + "\n" + document.ToString() + "\n";
    }

    private static void _Add(XElement parent, string tag, DocSection? section, (string Name, string Value)? attribute = null)
    {
        if (section is null) return;
        var element = XElement.Parse($"<{tag}>{section.Xml}</{tag}>");
        if (attribute is { } a) element.SetAttributeValue(a.Name, a.Value);
        parent.Add(element);
    }

    private static string _Kind(DocKind kind) => kind switch
    {
        DocKind.Gear => "gear",
        DocKind.Guard => "guard",
        DocKind.Emit => "emit",
        DocKind.Train => "train",
        DocKind.Mold => "mold",
        DocKind.Error => "error",
        DocKind.Enum => "enum",
        DocKind.Ingot => "ingot",
        DocKind.Constant => "constant",
        DocKind.Extension => "extend",
        DocKind.Field => "field",
        _ => "variant",
    };
}
