using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Rgl.Native;

/// <summary>Une macro C qui vaut une constante : son nom, le type RGL de la constante et sa valeur écrite en RGL.</summary>
/// <param name="Name">Le nom de la macro.</param>
/// <param name="Type">Le type de la constante : <c>int</c>, <c>uint</c>, <c>long</c>, <c>ulong</c>, <c>double</c> ou <c>string</c>.</param>
/// <param name="Value">La valeur telle qu'elle s'écrit en RGL (un littéral).</param>
internal sealed record CMacro(string Name, string Type, string Value);

/// <summary>
/// Lit les macros d'un en-tête C dans le texte que Clang écrit avec <c>-E -dD</c> : le source préprocessé, où chaque <c>#define</c> reste à sa place
/// entre des repères <c>#&#160;12 "fichier"</c> qui disent de quel fichier vient la suite. L'arbre syntaxique ne voit pas les macros.
/// </summary>
/// <remarks>
/// Seules les macros du fichier demandé sont gardées, et parmi elles seulement celles qui valent un entier, un flottant ou un texte écrits en
/// toutes lettres, sans suite d'opérations ni renvoi à une autre macro : leur valeur est alors une constante, qu'aucune évaluation ne change.
/// Les autres (macros à paramètres, expressions, noms de types) sont laissées de côté.
/// </remarks>
internal static partial class CMacroReader
{
    [GeneratedRegex("""^#\s+\d+\s+"((?:[^"\\]|\\.)*)""")]
    private static partial Regex Marker();

    [GeneratedRegex(@"^(0[xX][0-9a-fA-F]+|0[bB][01]+|0[0-7]*|[1-9][0-9]*)([uU]?(?:[lL]{1,2})?|[lL]{1,2}[uU])$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"^(\d+\.\d*|\.\d+|\d+(?=[eE]))([eE][+-]?\d+)?[fFlL]?$")]
    private static partial Regex Float();

    [GeneratedRegex(@"^""(?:[^""\\]|\\[\\""nrt])*""$")]
    private static partial Regex Text();

    /// <summary>Lit le texte préprocessé jusqu'à sa fin.</summary>
    /// <param name="stream">La sortie de <c>-E -dD</c>.</param>
    /// <param name="header">Le chemin complet de l'en-tête dont on veut les macros.</param>
    public static IReadOnlyList<CMacro> Read(Stream stream, string header)
    {
        var target = _Normalize(header);
        var inTarget = false;

        // Une macro redéfinie ou retirée (#undef) ne vaut que sa dernière définition : l'ordre d'apparition est gardé.
        List<string> order = [];
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] != '#') continue;

            if (Marker().Match(line) is { Success: true } marker)
            {
                inTarget = _Normalize(marker.Groups[1].Value.Replace("\\\\", "\\")) == target;
                continue;
            }
            if (!inTarget) continue;

            if (line.StartsWith("#undef ", StringComparison.Ordinal))
            {
                var undefined = line[7..].Trim();
                if (values.Remove(undefined)) order.Remove(undefined);
                continue;
            }
            if (!line.StartsWith("#define ", StringComparison.Ordinal)) continue;

            var rest = line[8..];
            var end = 0;
            while (end < rest.Length && (char.IsAsciiLetterOrDigit(rest[end]) || rest[end] == '_')) ++end;
            if (end == 0) continue;

            var name = rest[..end];
            // Une macro à paramètres s'écrit avec la parenthèse collée au nom : ce n'est pas une constante.
            var function = end < rest.Length && rest[end] == '(';
            if (values.ContainsKey(name)) order.Remove(name);
            order.Add(name);
            values[name] = function ? "(" : rest[end..].Trim();
        }

        List<CMacro> macros = [];
        foreach (var name in order)
            if (_Constant(name, values[name]) is { } macro) macros.Add(macro);
        return macros;
    }

    /// <summary>La constante que la macro vaut, ou <see langword="null"/> si elle vaut autre chose qu'un littéral.</summary>
    internal static CMacro? _Constant(string name, string value)
    {
        value = _Unparenthesized(value);
        if (value.Length == 0) return null;

        if (Text().IsMatch(value)) return new CMacro(name, "string", value);

        var sign = "";
        var digits = value;
        if (digits[0] is '-' or '+')
        {
            sign = digits[0] == '-' ? "-" : "";
            digits = _Unparenthesized(digits[1..].TrimStart());
        }
        if (digits.Length == 0) return null;

        if (Integer().Match(digits) is { Success: true } integer) return _Integer(name, sign, integer.Groups[1].Value, integer.Groups[2].Value);
        if (Float().Match(digits) is { Success: true } number) return _Float(name, sign, number.Groups[0].Value);
        return null;
    }

    private static CMacro? _Integer(string name, string sign, string literal, string suffix)
    {
        BigInteger value;
        if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = BigInteger.Parse("0" + literal[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else if (literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) value = literal[2..].Aggregate(BigInteger.Zero, (total, bit) => total * 2 + (bit - '0'));
        else if (literal.Length > 1 && literal[0] == '0') value = literal[1..].Aggregate(BigInteger.Zero, (total, digit) => total * 8 + (digit - '0'));
        else value = BigInteger.Parse(literal, CultureInfo.InvariantCulture);
        if (sign == "-") value = -value;

        var unsigned = suffix.Contains('u', StringComparison.OrdinalIgnoreCase);
        string? type;
        if (value >= int.MinValue && value <= int.MaxValue && !unsigned) type = "int";
        else if (value >= 0 && value <= uint.MaxValue && (unsigned || literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))) type = "uint";
        else if (value >= long.MinValue && value <= long.MaxValue && !unsigned) type = "long";
        else if (value >= 0 && value <= ulong.MaxValue) type = "ulong";
        else return null;

        return new CMacro(name, type, value.ToString(CultureInfo.InvariantCulture));
    }

    private static CMacro? _Float(string name, string sign, string literal)
    {
        // Le suffixe f ou l ne change rien à la valeur lue : RGL écrit tout flottant en double.
        if (literal[^1] is 'f' or 'F' or 'l' or 'L') literal = literal[..^1];
        if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsInfinity(number)) return null;

        // Un littéral RGL a toujours son point : « 1e3 » s'écrit « 1.0e3 », « .5 » « 0.5 », « 2. » « 2.0 ».
        var exponent = literal.IndexOfAny(['e', 'E']);
        var mantissa = exponent < 0 ? literal : literal[..exponent];
        var tail = exponent < 0 ? "" : literal[exponent..];
        if (!mantissa.Contains('.')) mantissa += ".0";
        if (mantissa.StartsWith('.')) mantissa = "0" + mantissa;
        if (mantissa.EndsWith('.')) mantissa += "0";
        return new CMacro(name, "double", sign + mantissa + tail);
    }

    /// <summary>Le texte sans les parenthèses qui l'enveloppent tout entier : <c>((3))</c> vaut <c>3</c>.</summary>
    private static string _Unparenthesized(string text)
    {
        text = text.Trim();
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')' && _Closes(text)) text = text[1..^1].Trim();
        return text;
    }

    /// <summary>Vrai si la parenthèse ouvrante du début est celle que ferme la dernière : <c>(1)+(2)</c> ne s'enveloppe pas.</summary>
    private static bool _Closes(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; ++i)
        {
            if (text[i] == '(') ++depth;
            else if (text[i] == ')' && --depth == 0) return i == text.Length - 1;
        }
        return false;
    }

    private static string _Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return path.Replace('\\', '/').ToLowerInvariant();
        }
    }
}
