using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Docs;

/// <summary>Une ligne d'un commentaire documentaire, sans son <c>///</c> ni l'indentation commune.</summary>
/// <param name="Text">Le texte de la ligne.</param>
/// <param name="Offset">L'indice, dans le source, de son premier caractère.</param>
internal sealed record DocLine(string Text, uint Offset);

/// <summary>Les lignes <c>///</c> qui précèdent une déclaration.</summary>
/// <param name="Span">Du premier <c>///</c> à la fin du dernier.</param>
/// <param name="Lines">Les lignes, indentation commune retirée.</param>
internal sealed record DocBlock(SourceSpan Span, IReadOnlyList<DocLine> Lines);

/// <summary>Retrouve les commentaires <c>///</c> placés juste avant un token.</summary>
internal static class DocExtractor
{
    /// <summary>
    /// Les lignes <c>///</c> qui précèdent <paramref name="token"/> sans ligne vide ni autre commentaire entre elles ni avec lui ;
    /// <see langword="null"/> s'il n'y en a pas. Un <c>///</c> en bout de ligne de code n'est pas documentaire.
    /// </summary>
    public static DocBlock? Before(Token token)
    {
        var found = new List<Trivia>();
        var breaks = 0;
        for (var i = token.Before.Count - 1; i >= 0; --i)
        {
            var trivia = token.Before[i];
            if (trivia.Kind == TriviaKind.Whitespace) continue;
            if (trivia.Kind == TriviaKind.EndOfLine)
            {
                if (++breaks > 1) break;
                continue;
            }
            if (trivia.Kind != TriviaKind.DocComment) break;

            breaks = 0;
            found.Insert(0, trivia);
        }
        if (found.Count == 0) return null;

        var raw = found.Select(t => new DocLine(t.Span.Text[3..], t.Span.Start.Value + 3)).ToList();
        // L'indentation commune n'est pas du contenu : un bloc de code garde son retrait propre.
        var indent = raw.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Select(l => l.Text.Length - l.Text.TrimStart().Length).DefaultIfEmpty(0).Min();
        var lines = raw.Select(l => string.IsNullOrWhiteSpace(l.Text) ? new DocLine("", l.Offset) : new DocLine(l.Text[indent..], l.Offset + (uint)indent)).ToList();

        var span = SourceSpan.FromBounds(token.Span.Source, found[0].Span.Start, found[^1].Span.End);
        return new DocBlock(span, lines);
    }
}

/// <summary>
/// Lit un commentaire documentaire : un texte libre au début devient le résumé, puis les balises XML sont lues avec un
/// parseur XML sans DTD. Rien n'est jamais fatal : un XML invalide donne un diagnostic et le texte brut comme résumé.
/// </summary>
internal static partial class DocParser
{
    /// <summary>Les balises de mise en forme, permises dans un texte.</summary>
    private static readonly HashSet<string> _inlineTags = new(StringComparer.Ordinal)
        { "c", "code", "para", "list", "item", "term", "description" };

    /// <summary>Une ligne du XML reconstitué : son texte, l'indice du source d'où il vient, et si la ligne est ajoutée par le lecteur.</summary>
    private readonly record struct XmlLine(string Text, uint Offset, bool Synthetic);

    /// <summary>Une ligne qui commence par une balise de section (connue ou non) : elle termine le résumé implicite. Les balises de mise en forme n'en font pas partie.</summary>
    [GeneratedRegex(@"^<(?!/?(c|code|para|list|item|term|description))[A-Za-z]")]
    private static partial Regex _BlockTagStart();

    [GeneratedRegex(@"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)")]
    private static partial Regex _BareAmpersand();

    [GeneratedRegex(@"<(?!/?(c|code|para|list|item|term|description|see)\b)")]
    private static partial Regex _PlainLessThan();

    /// <summary>Lit <paramref name="block"/> ; les problèmes de la documentation sont signalés à <paramref name="diagnostics"/> s'il est donné.</summary>
    public static DocComment Parse(DocBlock block, DiagnosticBag? diagnostics = null)
    {
        var lines = block.Lines;
        var first = -1;
        for (var i = 0; i < lines.Count && first < 0; ++i)
            if (lines[i].Text.Trim().Length > 0) first = i;
        if (first < 0) return _Empty(block);

        var xml = new List<XmlLine> { new("<doc>", block.Span.Start.Value, true) };
        var implicitSummary = false;
        var start = first;

        if (!_BlockTagStart().IsMatch(lines[first].Text.Trim()))
        {
            // Un texte libre au début : c'est le résumé, jusqu'à la première balise de section.
            var end = first;
            while (end < lines.Count && !_BlockTagStart().IsMatch(lines[end].Text.Trim())) ++end;
            var last = end;
            while (last > first && lines[last - 1].Text.Trim().Length == 0) --last;

            xml.Add(new("<summary>", lines[first].Offset, true));
            for (var i = first; i < last; ++i) xml.Add(new(_Escape(lines[i].Text), lines[i].Offset, false));
            xml.Add(new("</summary>", lines[last - 1].Offset, true));
            implicitSummary = true;
            start = end;
        }

        for (var i = start; i < lines.Count; ++i) xml.Add(new(lines[i].Text, lines[i].Offset, false));
        xml.Add(new("</doc>", block.Span.End.Value, true));

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(string.Join('\n', xml.Select(x => x.Text))), settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            var at = _Offset(block, xml, exception.LineNumber, exception.LinePosition);
            diagnostics?.Warning(_At(block, at, at), DocCodes.InvalidXml, $"XML invalide dans la documentation : {_Clean(exception.Message)}",
                "ici", ["écris « &lt; » pour un « < » et « &amp; » pour un « & » dans le texte, ou mets le texte entre <![CDATA[ … ]]>"]);
            var plain = string.Join('\n', lines.Select(l => l.Text)).Trim();
            return _Empty(block) with { Summary = new DocSection(System.Security.SecurityElement.Escape(plain) ?? "", plain, block.Span), IsValid = false };
        }

        return _Read(block, xml, document.Root!, implicitSummary, diagnostics);
    }

    private static DocComment _Empty(DocBlock block) => new(block.Span, null, null, [], [], null, [], [], [], []);

    private static DocComment _Read(DocBlock block, List<XmlLine> xml, XElement root, bool implicitSummary, DiagnosticBag? diagnostics)
    {
        DocSection? summary = null, remarks = null, returns = null;
        var parameters = new List<DocNamed>();
        var typeParameters = new List<DocNamed>();
        var fails = new List<DocFails>();
        var examples = new List<DocSection>();
        var seeAlso = new List<DocReference>();
        var references = new List<DocReference>();
        var custom = new List<DocCustom>();

        SourceSpan Tag(XElement element) => _TagSpan(block, xml, element);
        DocSection Section(XElement element) =>
            new(string.Concat(element.Nodes()), DocRenderer.Markdown(element), implicitSummary && element.Name.LocalName == "summary" && ReferenceEquals(element, root.Elements().First()) ? block.Span : Tag(element));

        void Report(string code, XElement element, string message, string? label = null, IReadOnlyList<string>? help = null)
        {
            if (diagnostics is null) return;
            if (code == DocCodes.UnknownTag) diagnostics.Info(Tag(element), code, message, label, help);
            else diagnostics.Warning(Tag(element), code, message, label, help);
        }

        void Named(XElement element, List<DocNamed> into, string missingCode)
        {
            var tag = element.Name.LocalName;
            if (element.Attribute("name")?.Value.Trim() is not { Length: > 0 } name)
            {
                Report(missingCode, element, $"<{tag}> sans attribut « name »", help: [$"écris <{tag} name=\"nom\">…</{tag}>"]);
                return;
            }
            if (into.Any(n => n.Name == name)) Report(DocCodes.Duplicate, element, $"« {name} » est décrit plusieurs fois");
            else into.Add(new DocNamed(name, Section(element), Tag(element)));
        }

        var nodes = DocLanguage.Nodes(root).ToArray();
        // Le texte libre d'un bloc de langue est lui aussi un résumé implicite.
        var leading = nodes.TakeWhile(n => n is not XElement e || _inlineTags.Contains(e.Name.LocalName) || e.Name.LocalName == "see").ToArray();
        if (root.Elements("lang").Any() && leading.OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            var content = string.Concat(leading.Select(n => n.ToString()));
            var element = XElement.Parse("<summary>" + content + "</summary>");
            summary = new DocSection(content, DocRenderer.Markdown(element), block.Span);
            nodes = nodes.Skip(leading.Length).ToArray();
            implicitSummary = true;
        }

        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                if (text.Value.Trim().Length == 0 || diagnostics is null) continue;
                var info = (IXmlLineInfo)text;
                var at = _Offset(block, xml, info.LineNumber, info.LinePosition);
                diagnostics.Info(_At(block, at, at), DocCodes.UnknownTag, "texte libre après une balise : il est ignoré",
                    "ici", ["le texte libre n'est lu qu'au début du commentaire, où il devient le résumé ; mets celui-ci dans une balise"]);
                continue;
            }
            if (node is not XElement element) continue;

            switch (element.Name.LocalName)
            {
                case "summary":
                    if (summary is not null)
                        Report(DocCodes.Duplicate, element, implicitSummary ? "le résumé est déjà écrit sans balise : ce <summary> est ignoré" : "<summary> écrit plusieurs fois",
                            help: ["garde un seul résumé : le texte libre du début, ou une balise <summary>"]);
                    else summary = Section(element);
                    break;
                case "remarks":
                    if (remarks is not null) Report(DocCodes.Duplicate, element, "<remarks> écrit plusieurs fois");
                    else remarks = Section(element);
                    break;
                case "returns":
                    if (returns is not null) Report(DocCodes.Duplicate, element, "<returns> écrit plusieurs fois");
                    else returns = Section(element);
                    break;
                case "param":
                    Named(element, parameters, DocCodes.UnknownParam);
                    break;
                case "typeparam":
                    Named(element, typeParameters, DocCodes.UnknownTypeParam);
                    break;
                case "fails":
                    if (element.Attribute("type")?.Value.Trim() is { Length: > 0 } type) fails.Add(new DocFails(type, Section(element), Tag(element)));
                    else Report(DocCodes.MissingAttribute, element, "<fails> sans attribut « type »", help: ["écris <fails type=\"NomDeLErreur\">…</fails>"]);
                    break;
                case "example":
                    examples.Add(Section(element));
                    break;
                case "see" or "seealso":
                    if (element.Attribute("cref")?.Value.Trim() is { Length: > 0 } cref) seeAlso.Add(new DocReference(cref, Tag(element)));
                    break;
                case "return":
                    Report(DocCodes.UnknownTag, element, "balise <return> inconnue", "ici", ["la balise du résultat est <returns>"]);
                    break;
                default:
                    custom.Add(new DocCustom(element.Name.LocalName, element.ToString(SaveOptions.DisableFormatting), Section(element)));
                    break;
            }
        }

        // Les références et les balises de mise en forme écrites n'importe où dans les sections.
        foreach (var language in root.Descendants("lang"))
        {
            if (language.Parent != root)
                Report(DocCodes.LanguagePlacement, language, "<lang> doit englober les sections de documentation, à la racine du commentaire",
                    help: ["écris <lang name=\"fr\"><summary>…</summary><param name=\"x\">…</param></lang>"]);
            if (string.IsNullOrWhiteSpace(language.Attribute("name")?.Value))
                Report(DocCodes.MissingAttribute, language, "<lang> sans attribut « name »", help: ["écris <lang name=\"en\">…</lang>"]);
        }

        foreach (var element in DocLanguage.Descendants(root))
        {
            var name = element.Name.LocalName;
            if (name is "see" or "seealso")
            {
                if (element.Attribute("cref")?.Value.Trim() is { Length: > 0 } cref) references.Add(new DocReference(cref, Tag(element)));
                else if (element.Attribute("langword") is null)
                    Report(DocCodes.Cref, element, $"<{name}> sans attribut « cref »", help: [$"écris <{name} cref=\"NomDuSymbole\"/>"]);
            }

        }

        return new DocComment(block.Span, summary, remarks, parameters, typeParameters, returns, fails, examples, seeAlso, references, implicitSummary)
        {
            Custom = custom,
            LocalizedXml = root.Descendants("lang").Any() ? string.Concat(root.Nodes()) : null,
        };
    }

    /// <summary>Protège le texte libre : un « &amp; » ou un « &lt; » qui ne commence pas une balise de mise en forme n'est pas du XML.</summary>
    private static string _Escape(string line) => _PlainLessThan().Replace(_BareAmpersand().Replace(line, "&amp;"), "&lt;");

    private static string _Clean(string message) => Regex.Replace(message, @"\s*Line \d+, position \d+\.?", "").Trim();

    /// <summary>L'indice, dans le source, de la ligne et de la colonne (à partir de 1) du XML reconstitué.</summary>
    private static uint _Offset(DocBlock block, List<XmlLine> xml, int line, int column)
    {
        if (line < 1 || line > xml.Count) return block.Span.Start.Value;
        var entry = xml[line - 1];
        var at = entry.Synthetic ? entry.Offset : entry.Offset + (uint)Math.Clamp(column - 1, 0, entry.Text.Length);
        return Math.Clamp(at, block.Span.Start.Value, block.Span.End.Value);
    }

    private static SourceSpan _At(DocBlock block, uint start, uint end) =>
        SourceSpan.FromBounds(block.Span.Source, RUint.CreateChecked(start), RUint.CreateChecked(Math.Max(start, end)));

    /// <summary>La balise ouvrante d'un élément dans le source, de « &lt; » à « &gt; ».</summary>
    private static SourceSpan _TagSpan(DocBlock block, List<XmlLine> xml, XElement element)
    {
        var info = (IXmlLineInfo)element;
        // LinePosition désigne le nom de la balise : le « < » est un caractère avant.
        var start = _Offset(block, xml, info.LineNumber, info.LinePosition - 1);
        var content = block.Span.Source.Content;
        var limit = block.Span.End.Value;
        var end = start;
        while (end < limit && content[(int)end] is not ('>' or '\n' or '\r')) ++end;
        if (end < limit && content[(int)end] == '>') ++end;
        return _At(block, start, end);
    }
}
