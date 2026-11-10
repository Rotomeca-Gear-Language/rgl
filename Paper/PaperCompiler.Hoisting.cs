using System.Text;
using System.Text.RegularExpressions;

namespace Rgl.Paper;

internal static partial class PaperCompiler
{
    /// <summary>
    /// Sépare les déclarations de premier niveau d'un texte de blocs <c>@rgl</c> : d'un côté les constantes (<c>const</c>, <c>export const</c>), de l'autre tout le reste. Une constante
    /// s'écrit avant les gears ; quand des blocs de plusieurs fichiers (layout, composants, page) sont mis bout à bout, celles des derniers viendraient après les gears des premiers.
    /// </summary>
    internal static (string Constants, string Others) SplitConstants(string blocks)
    {
        var constants = new StringBuilder();
        var rest = new StringBuilder();
        var start = 0;
        var depth = 0;
        var i = 0;
        while (i < blocks.Length)
        {
            var c = blocks[i];
            if (c == '/' && i + 1 < blocks.Length && blocks[i + 1] == '/')
            {
                var end = blocks.IndexOf('\n', i);
                i = end < 0 ? blocks.Length : end;
            }
            else if (c == '/' && i + 1 < blocks.Length && blocks[i + 1] == '*')
            {
                var end = blocks.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? blocks.Length : end + 2;
            }
            else if (c is '\'' or '"')
            {
                i++;
                while (i < blocks.Length && blocks[i] != c) i += blocks[i] == '\\' ? 2 : 1;
                i++;
            }
            else if (c == '`')
            {
                i++;
                while (i < blocks.Length && blocks[i] != '`')
                {
                    if (blocks[i] == '\\') i += 2;
                    else if (blocks[i] == '{' && ClosingBrace(blocks, i) is var close and >= 0) i = close + 1;
                    else i++;
                }
                i++;
            }
            else
            {
                i++;
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
                var endsStatement = depth == 0 && (c == ';' || (c == '}' && !StartsWithConst(blocks.AsSpan(start, i - start))));
                if (!endsStatement) continue;

                var statement = blocks[start..i];
                if (StartsWithConst(statement)) constants.Append(statement.Trim()).Append('\n');
                else rest.Append(statement);
                start = i;
            }
        }
        rest.Append(blocks, Math.Min(start, blocks.Length), blocks.Length - Math.Min(start, blocks.Length));
        return (constants.ToString(), rest.ToString());
    }

    /// <summary>Les blocs avec leurs constantes en tête, dans l'ordre où elles sont écrites ; le texte inchangé s'il n'y avait rien à remonter.</summary>
    internal static string HoistConstants(string blocks)
    {
        var (constants, rest) = SplitConstants(blocks);
        if (constants.Length == 0) return blocks;
        var hoisted = constants + rest;
        return WithoutSpaces(hoisted) == WithoutSpaces(blocks) ? blocks : hoisted;
    }

    private static string WithoutSpaces(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static bool StartsWithConst(ReadOnlySpan<char> statement) => ConstantStart().IsMatch(statement);

    [GeneratedRegex(@"^(?:\s|//[^\n]*\n?|/\*.*?\*/)*(?:export\s+)?const\b", RegexOptions.Singleline)]
    private static partial Regex ConstantStart();
}
