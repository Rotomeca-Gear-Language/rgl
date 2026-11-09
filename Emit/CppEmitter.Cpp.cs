using System.Text;
using System.Text.RegularExpressions;
using Rgl.Text;

namespace Rgl.Emit;

internal sealed partial class CppEmitter
{
    /// <summary>Les inclusions restent globales, même sous #if ; chaque bloc possède son namespace.</summary>
    private static string _ScopedCpp(SourceSpan source, string ns)
    {
        var text = source.Text;
        var output = new StringBuilder("namespace " + ns + " {\n");
        var path = source.Source.Path.Replace('\\', '/').Replace("\"", "\\\"");
        var line = source.Source.OriginalLine(source.Source[source.Start].Line).Value;
        // #line compte les lignes depuis 1.
        output.Append("#line ").Append(line).Append(" \"").Append(path).Append("\"\n");
        var position = 0;
        var copied = 0;
        while (position < text.Length)
        {
            var opaque = Syntax.CppBlockReader.OpaqueEnd(text, position, text.Length);
            if (opaque > position) { position = opaque; continue; }
            if (text[position] != '#') { position++; continue; }
            var lineStart = text.LastIndexOf('\n', position) + 1;
            if (!string.IsNullOrWhiteSpace(text[lineStart..position])) { position++; continue; }
            var end = text.IndexOf('\n', position);
            if (end < 0) end = text.Length;
            while (end < text.Length && text[..end].TrimEnd('\r').EndsWith('\\'))
            {
                var next = text.IndexOf('\n', end + 1);
                end = next < 0 ? text.Length : next;
            }
            var directive = text[position..end];
            if (Regex.IsMatch(directive, @"^#\s*include\b"))
            {
                output.Append(text[copied..position]).Append("\n}\n");
                if (Regex.IsMatch(directive, @"^#\s*include\s*<rgl::standard>\s*$")) directive = "#include \"rgl/standard.hpp\"";
                output.Append(directive).Append("\nnamespace ").Append(ns).Append(" {\n");
                var currentLine = line + text[..end].Count(c => c == '\n');
                output.Append("#line ").Append(currentLine).Append(" \"").Append(path).Append("\"\n");
                copied = end;
            }
            position = end;
        }
        return output.Append(text[copied..]).Append("\n}\n").ToString();
    }
}
