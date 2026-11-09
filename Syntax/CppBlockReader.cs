using Rgl.Diagnostics;
using Rgl.Text;

namespace Rgl.Syntax;

/// <summary>Lit un bloc étranger sans appliquer les règles lexicales RGL à son contenu.</summary>
internal static class CppBlockReader
{
    public static int? BodyStart(string text, int position, int end)
    {
        while (position < end)
        {
            if (char.IsWhiteSpace(text[position])) { position++; continue; }
            if (position + 1 < end && text[position..(position + 2)] == "/*")
            {
                var close = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (close < 0 || close >= end) return null;
                position = close + 2; continue;
            }
            if (position + 1 < end && text[position..(position + 2)] == "//")
            {
                while (position < end && text[position] != '\n') position++;
                continue;
            }
            return text[position] == '{' ? position : null;
        }
        return null;
    }

    public static IEnumerable<(SourceSpan Span, string Path)> Includes(SourceSpan code)
    {
        var text = code.Text;
        for (var position = 0; position < text.Length;)
        {
            var opaque = OpaqueEnd(text, position, text.Length);
            if (opaque > position) { position = opaque; continue; }
            if (text[position] != '#') { position++; continue; }
            var end = text.IndexOf('\n', position);
            if (end < 0) end = text.Length;
            var match = System.Text.RegularExpressions.Regex.Match(text[position..end], "^#\\s*include\\s*[\"<]([^\">]+)[\">]");
            if (match.Success)
                yield return (new SourceSpan(code.Source, code.Start + (uint)position, (uint)(end - position)), match.Groups[1].Value);
            position = end;
        }
    }

    public static int OpaqueEnd(string text, int position, int end)
    {
        if (text[position] == '/' && position + 1 < end && text[position + 1] == '*')
        {
            var close = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
            return close < 0 || close >= end ? end : close + 2;
        }
        if (text[position] == '/' && position + 1 < end && text[position + 1] == '/')
        {
            var next = text.IndexOf('\n', position + 2);
            while (next > 0 && text[..next].TrimEnd('\r').EndsWith('\\')) next = text.IndexOf('\n', next + 1);
            return next < 0 || next >= end ? end : next + 1;
        }
        if (text[position] == 'R' && position + 1 < end && text[position + 1] == '"')
        {
            var paren = text.IndexOf('(', position + 2);
            if (paren >= 0 && paren < end && paren - position <= 18)
            {
                var terminator = ")" + text[(position + 2)..paren] + "\"";
                var close = text.IndexOf(terminator, paren + 1, StringComparison.Ordinal);
                return close < 0 || close + terminator.Length > end ? end : close + terminator.Length;
            }
        }
        if (text[position] is '"' or '\'')
        {
            var quote = text[position++];
            while (position < end)
            {
                var c = text[position++];
                if (c == '\\' && position < end) position++;
                else if (c == quote) break;
            }
            return position;
        }
        return position;
    }

    public static (SourceSpan Body, int End) Read(SourceText source, int open, int end, DiagnosticBag? diagnostics = null)
    {
        var text = source.Content;
        var depth = 1;
        var position = open + 1;
        while (position < end)
        {
            var opaque = OpaqueEnd(text, position, end);
            if (opaque > position) { position = opaque; continue; }
            var c = text[position];
            position++;
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (depth == 0) return (SourceSpan.FromBounds(source, (uint)(open + 1), (uint)(position - 1)), position);
        }
        diagnostics?.Error(SourceSpan.FromBounds(source, (uint)open, (uint)end), DiagnosticCodes.InvalidCppBlock,
            "bloc cpp non fermé : accolade, chaîne ou commentaire C++ incomplet", "bloc C++ incomplet");
        return (SourceSpan.FromBounds(source, (uint)(open + 1), (uint)end), end);
    }
}
