using System.Text;
using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Text;

namespace Rgl.Paper;

internal static partial class PaperCompiler
{
    private const int MaxComponentDepth = 24;

    private sealed record ComponentParameter(string Type, string Name, string? Default);

    /// <summary>Un fichier <c>.paper</c> lu comme composant : ses <c>param</c>, ses blocs sans eux, son markup.</summary>
    private sealed record ComponentDefinition(string Name, IReadOnlyList<ComponentParameter> Parameters, string Blocks, string Markup, IReadOnlyList<(int PaperLine, int Lines)> Ranges);

    private sealed class ComponentContext(SourceText source, string projectRoot, DiagnosticBag diagnostics)
    {
        public SourceText Source { get; } = source;
        public string ProjectRoot { get; } = projectRoot;
        public DiagnosticBag Diagnostics { get; } = diagnostics;
        public Dictionary<string, ComponentDefinition?> Definitions { get; } = new(StringComparer.Ordinal);
        public List<ComponentDefinition> Used { get; } = [];
        public StringBuilder Checks { get; } = new();
        public int Counter { get; set; }

        public void Error(string message) => Diagnostics.Error(message, Source.Path);
    }

    private sealed record ComponentTag(string Name, Dictionary<string, string> Attributes, bool SelfClosing, int End);

    /// <summary>
    /// Remplace chaque balise <c>&lt;Nom …&gt;contenu&lt;/Nom&gt;</c> (nom commençant par une majuscule) par le markup du composant <c>Nom.paper</c>. Le composant n'a pas d'état :
    /// ses <c>param</c> sont remplacés, dans ses interpolations, par les valeurs des attributs (<c>titre="x"</c> est un texte, <c>n=@@ count + 1 @@</c> une expression de la page, lue comme
    /// ailleurs dans la page), et le contenu de la balise prend la place de son <c>&lt;slot /&gt;</c>. Chaque valeur est aussi liée à un gear de contrôle du type du <c>param</c>.
    /// Les blocs <c>@rgl</c> du composant (gears, constantes) rejoignent ceux de la page, une seule fois.
    /// </summary>
    private static bool ExpandComponents(SourceText source, string projectRoot, DiagnosticBag diagnostics, StringBuilder blocks, StringBuilder markup,
        List<(int PaperLine, int Lines)> blockRanges, out int count)
    {
        count = 0;
        var context = new ComponentContext(source, projectRoot, diagnostics);
        var expanded = Expand(markup.ToString(), context, 0);
        if (expanded is null) return false;
        if (context.Used.Count == 0) return true;

        var pageGears = GearSignature().Matches(blocks.ToString()).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var front = new StringBuilder();
        List<(int PaperLine, int Lines)> frontRanges = [];
        foreach (var definition in context.Used)
        {
            var clash = GearSignature().Matches(definition.Blocks).Select(m => m.Groups[1].Value).FirstOrDefault(gear => !pageGears.Add(gear));
            if (clash is not null)
            {
                diagnostics.Error($"le composant « {definition.Name} » et la page (ou un autre composant) déclarent tous deux le gear « {clash} » : la page fusionnée n'a qu'une portée, renomme l'un des deux", source.Path);
                return false;
            }
            front.Append(definition.Blocks);
            frontRanges.AddRange(definition.Ranges.Select(range => (0, range.Lines)));
        }

        var checks = context.Checks.ToString();
        blocks.Insert(0, front.ToString()).Append(checks);
        blockRanges.InsertRange(0, frontRanges);
        if (checks.Length > 0) blockRanges.Add((0, checks.Count(c => c == '\n')));
        markup.Clear().Append(expanded);
        count = context.Used.Count;
        return true;
    }

    private static string? Expand(string markup, ComponentContext context, int depth)
    {
        if (depth > MaxComponentDepth)
        {
            context.Error($"des composants s'appellent les uns les autres sur plus de {MaxComponentDepth} niveaux : un composant se contient-il lui-même ?");
            return null;
        }
        var result = new StringBuilder(markup.Length);
        var i = 0;
        while (i < markup.Length)
        {
            if (SkipOpaque(markup, i) is var next && next > i)
            {
                result.Append(markup, i, next - i);
                i = next;
                continue;
            }
            if (markup[i] != '<' || i + 1 >= markup.Length || !char.IsAsciiLetterUpper(markup[i + 1]))
            {
                result.Append(markup[i++]);
                continue;
            }

            var tag = ParseTag(markup, i, context);
            if (tag is null) return null;
            var children = "";
            var end = tag.End;
            if (!tag.SelfClosing)
            {
                var close = FindClosing(markup, tag, context);
                if (close is null) return null;
                children = markup[tag.End..close.Value];
                end = close.Value + $"</{tag.Name}>".Length;
            }
            var instance = Instantiate(tag, children, context);
            if (instance is null) return null;
            var inner = Expand(instance, context, depth + 1);
            if (inner is null) return null;
            result.Append(inner);
            i = end;
        }
        return result.ToString();
    }

    /// <summary>Si le texte commence, à cette place, par une interpolation ou un commentaire HTML (où une balise n'en est pas une), la place qui suit ; sinon la place elle-même.</summary>
    private static int SkipOpaque(string text, int position)
    {
        if (string.CompareOrdinal(text, position, "@@", 0, 2) == 0)
        {
            var close = text.IndexOf("@@", position + 2, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + 2;
        }
        if (string.CompareOrdinal(text, position, "<!--", 0, 4) == 0)
        {
            var close = text.IndexOf("-->", position + 4, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + 3;
        }
        return position;
    }

    private static ComponentTag? ParseTag(string text, int start, ComponentContext context)
    {
        var i = start + 1;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
        var name = text[(start + 1)..i];
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);
        while (true)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length)
            {
                context.Error($"la balise du composant « {name} » n'est pas fermée : il manque « > »");
                return null;
            }
            if (text[i] == '>') return new ComponentTag(name, attributes, false, i + 1);
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>') return new ComponentTag(name, attributes, true, i + 2);

            var nameStart = i;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '-')) i++;
            if (i == nameStart)
            {
                context.Error($"attribut inattendu « {text[i]} » dans la balise du composant « {name} »");
                return null;
            }
            var attribute = text[nameStart..i];
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=')
            {
                context.Error($"l'attribut « {attribute} » du composant « {name} » n'a pas de valeur ; écris {attribute}=\"texte\" ou {attribute}=@@ expression @@");
                return null;
            }
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            string value;
            if (i < text.Length && text[i] is '"' or '\'')
            {
                var quote = text[i];
                var close = text.IndexOf(quote, i + 1);
                if (close < 0)
                {
                    context.Error($"la valeur de l'attribut « {attribute} » du composant « {name} » n'est pas fermée");
                    return null;
                }
                value = Quote(text[(i + 1)..close]);
                i = close + 1;
            }
            else if (i + 1 < text.Length && text[i] == '@' && text[i + 1] == '@')
            {
                var close = text.IndexOf("@@", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    context.Error($"l'interpolation de l'attribut « {attribute} » du composant « {name} » n'est pas fermée");
                    return null;
                }
                value = text[(i + 2)..close].Trim();
                if (value.Length == 0)
                {
                    context.Error($"l'attribut « {attribute} » du composant « {name} » a une expression vide");
                    return null;
                }
                i = close + 2;
            }
            else
            {
                context.Error($"la valeur de l'attribut « {attribute} » du composant « {name} » s'écrit entre guillemets ou entre @@ ... @@");
                return null;
            }
            if (!attributes.TryAdd(attribute, value))
            {
                context.Error($"l'attribut « {attribute} » est écrit deux fois sur le composant « {name} »");
                return null;
            }
        }
    }

    /// <summary>La place de la balise fermante qui correspond à cette balise ouvrante, en comptant les balises du même nom qu'elle contient.</summary>
    private static int? FindClosing(string text, ComponentTag open, ComponentContext context)
    {
        var closing = $"</{open.Name}>";
        var depth = 1;
        var i = open.End;
        while (i < text.Length)
        {
            if (SkipOpaque(text, i) is var next && next > i)
            {
                i = next;
                continue;
            }
            if (string.CompareOrdinal(text, i, closing, 0, closing.Length) == 0)
            {
                if (--depth == 0) return i;
                i += closing.Length;
                continue;
            }
            if (text[i] == '<' && string.CompareOrdinal(text, i + 1, open.Name, 0, open.Name.Length) == 0
                && (i + 1 + open.Name.Length >= text.Length || !(char.IsAsciiLetterOrDigit(text[i + 1 + open.Name.Length]) || text[i + 1 + open.Name.Length] == '_')))
            {
                var nested = ParseTag(text, i, context);
                if (nested is null) return null;
                if (!nested.SelfClosing) depth++;
                i = nested.End;
                continue;
            }
            i++;
        }
        context.Error($"la balise du composant « {open.Name} » n'est pas refermée : il manque {closing}");
        return null;
    }

    private static string? Instantiate(ComponentTag tag, string children, ComponentContext context)
    {
        var definition = LoadComponent(tag.Name, context);
        if (definition is null) return null;

        foreach (var attribute in tag.Attributes.Keys)
        {
            if (definition.Parameters.All(p => p.Name != attribute))
            {
                var known = definition.Parameters.Count == 0 ? "ce composant n'a aucun param" : "params : " + string.Join(", ", definition.Parameters.Select(p => p.Name));
                context.Error($"le composant « {tag.Name} » n'a pas de paramètre « {attribute} » ({known})");
                return null;
            }
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
        {
            if (!tag.Attributes.TryGetValue(parameter.Name, out var expression) && (expression = parameter.Default) is null)
            {
                context.Error($"le composant « {tag.Name} » demande le paramètre « {parameter.Name} » : écris {parameter.Name}=\"…\" ou {parameter.Name}=@@ expression @@");
                return null;
            }
            context.Checks.Append("gear __prop_").Append(++context.Counter).Append("(): ").Append(parameter.Type).Append(" => ").Append(expression).Append(";\n");
            values[parameter.Name] = "(" + expression + ")";
        }

        var slots = SlotTag().Matches(definition.Markup).Count;
        if (slots == 0 && children.Trim().Length > 0)
        {
            context.Error($"le composant « {tag.Name} » n'a pas de <slot /> : son contenu n'a nulle part où aller");
            return null;
        }
        var markup = Substitute(definition.Markup, values);
        return SlotTag().Replace(markup, _ => children);
    }

    /// <summary>Remplace, dans les interpolations <c>@@ … @@</c> du markup, les noms des paramètres par leurs valeurs (hors du texte des chaînes).</summary>
    private static string Substitute(string markup, Dictionary<string, string> values)
    {
        if (values.Count == 0) return markup;
        var names = new Regex($@"(?<![.\w])(?:{string.Join('|', values.Keys.Select(Regex.Escape))})\b");
        var result = new StringBuilder(markup.Length);
        var i = 0;
        while (i < markup.Length)
        {
            var open = markup.IndexOf("@@", i, StringComparison.Ordinal);
            var close = open < 0 ? -1 : markup.IndexOf("@@", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                result.Append(markup, i, markup.Length - i);
                break;
            }
            result.Append(markup, i, open + 2 - i);
            RewriteCode(markup, open + 2, close, names, match => values[match.Value], result);
            result.Append("@@");
            i = close + 2;
        }
        return result.ToString();
    }

    private static ComponentDefinition? LoadComponent(string name, ComponentContext context)
    {
        if (context.Definitions.TryGetValue(name, out var known))
        {
            if (known is not null && !context.Used.Contains(known)) context.Used.Add(known);
            return known;
        }
        context.Definitions[name] = null;
        var path = FindPaperFile(name, "composant", context.Source.Path, context.ProjectRoot, context.Diagnostics);
        if (path is null) return null;
        var component = SourceText.FromPath(path, context.Diagnostics);
        if (component is null) return null;
        var blocks = new StringBuilder();
        var markup = new StringBuilder();
        List<(int PaperLine, int Lines)> ranges = [];
        if (!Split(component, blocks, markup, context.Diagnostics, ranges)) return null;

        List<ComponentParameter> parameters = [];
        var blockText = ParamDeclaration().Replace(blocks.ToString(), match =>
        {
            parameters.Add(new ComponentParameter(match.Groups[1].Value.Trim(), match.Groups[2].Value, match.Groups[3].Success ? match.Groups[3].Value.Trim() : null));
            return new string('\n', match.Value.Count(c => c == '\n'));
        });
        string? problem = null;
        if (ParamLeftover().IsMatch(blockText)) problem = "une déclaration param mal formée ; écris : param TYPE nom; ou param TYPE nom: valeur;";
        else if (Regex.IsMatch(blockText, @"\breactive\s")) problem = "un reactive : un composant n'a pas d'état, il reçoit ce qu'il affiche par ses param";
        else if (GearSignature().Matches(blockText).Any(m => m.Groups[1].Value == LoadGear)) problem = $"un gear « {LoadGear} » : un composant n'a pas de chargement";
        else if (LayoutDirective().IsMatch(markup.ToString())) problem = "une directive @layout : seule une page a un layout";
        else if (parameters.GroupBy(p => p.Name).Any(g => g.Count() > 1)) problem = "deux param du même nom";
        else if (SlotTag().Matches(markup.ToString()).Count > 1) problem = "plusieurs <slot /> : il en faut un seul";
        if (problem is not null)
        {
            context.Diagnostics.Error($"le composant « {name} » ne peut pas contenir {problem}", component.Path);
            return null;
        }

        var definition = new ComponentDefinition(name, parameters, blockText, markup.ToString(), ranges);
        context.Definitions[name] = definition;
        context.Used.Add(definition);
        return definition;
    }
}
