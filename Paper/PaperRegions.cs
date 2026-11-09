namespace Rgl.Paper;

/// <summary>Un morceau de RGL dans une page : un bloc <c>@rgl { … }</c> (son intérieur) ou une interpolation <c>@@ … @@</c> (son intérieur).</summary>
internal readonly record struct PaperRegion(int Start, int End, bool IsBlock);

/// <summary>Trouve le RGL d'un <c>.paper</c> comme le fait <see cref="PaperCompiler"/> : les blocs d'abord, puis les interpolations dans le reste.</summary>
internal static class PaperRegions
{
    public static IReadOnlyList<PaperRegion> Find(string text)
    {
        var regions = new List<PaperRegion>();
        var markup = new List<(int Start, int End)>();
        var markupStart = 0;
        for (var i = 0; i < text.Length;)
        {
            if (!text.AsSpan(i).StartsWith("@rgl", StringComparison.Ordinal) || i > 0 && _IsName(text[i - 1]))
            {
                i++;
                continue;
            }

            var cursor = i + 4;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length || text[cursor] != '{')
            {
                i += 4;
                continue;
            }

            var close = _ClosingBrace(text, cursor);
            // Un bloc non fermé : tout ce qui suit est du RGL en cours d'écriture.
            var end = close < 0 ? text.Length : close;
            markup.Add((markupStart, i));
            regions.Add(new PaperRegion(cursor + 1, end, IsBlock: true));
            if (close < 0)
            {
                markupStart = text.Length;
                break;
            }
            i = close + 1;
            markupStart = i;
        }
        if (markupStart < text.Length) markup.Add((markupStart, text.Length));

        foreach (var (start, end) in markup)
        {
            for (var position = start; position < end;)
            {
                var open = text.IndexOf("@@", position, end - position, StringComparison.Ordinal);
                if (open < 0) break;
                var close = text.IndexOf("@@", open + 2, end - open - 2, StringComparison.Ordinal);
                // Une interpolation non fermée : le RGL va jusqu'à la fin de la ligne.
                if (close < 0)
                {
                    var lineEnd = text.IndexOf('\n', open + 2);
                    regions.Add(new PaperRegion(open + 2, lineEnd < 0 || lineEnd > end ? end : lineEnd, IsBlock: false));
                    break;
                }
                regions.Add(new PaperRegion(open + 2, close, IsBlock: false));
                position = close + 2;
            }
        }
        return [.. regions.OrderBy(r => r.Start)];
    }

    private static bool _IsName(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>L'accolade qui ferme celle de <paramref name="open"/> ; les textes entre guillemets, apostrophes ou accents graves sont sautés.</summary>
    private static int _ClosingBrace(string text, int open)
    {
        var depth = 0;
        var quote = '\0';
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
            if (c is '\'' or '"' or '`')
            {
                quote = c;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
