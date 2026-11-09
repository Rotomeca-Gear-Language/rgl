using System.Globalization;
using System.Numerics;
using System.Text;

namespace Rgl.Binding;

/// <summary>
/// Calcule la valeur d'un littéral à partir de son texte brut, déjà validé par le lexer
/// (<c>1_000</c> → 1000, <c>"a\n"</c> → <c>a</c> suivi d'un saut de ligne).
/// </summary>
internal static class LiteralDecoder
{
    /// <summary>Valeur d'un littéral entier : décimal, <c>0x</c>, <c>0b</c> ou <c>0o</c>, avec des <c>_</c> séparateurs.</summary>
    /// <param name="text">Le texte du token, par exemple <c>0xFF</c>.</param>
    /// <returns>La valeur, sans limite de taille : c'est au binder de vérifier qu'elle tient dans son type.</returns>
    public static BigInteger Integer(string text)
    {
        var digits = text.Replace("_", "");
        var (numberBase, start) = digits.Length > 1 && digits[0] == '0'
            ? digits[1] switch { 'x' => (16, 2), 'b' => (2, 2), 'o' => (8, 2), _ => (10, 0) }
            : (10, 0);

        var value = BigInteger.Zero;
        for (var i = start; i < digits.Length; ++i)
            value = value * numberBase + Convert.ToInt32(digits[i].ToString(), 16);
        return value;
    }

    /// <summary>Valeur d'un littéral flottant (<c>1.5</c>, <c>1.5e3</c>), avec des <c>_</c> séparateurs.</summary>
    public static double Float(string text) =>
        double.Parse(text.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Contenu d'une chaîne brute <c>'…'</c> : aucun échappement, <c>''</c> pour une apostrophe.</summary>
    /// <param name="text">Le texte du token, apostrophes comprises.</param>
    public static string Raw(string text) => text[1..^1].Replace("''", "'");

    /// <summary>
    /// Contenu d'une chaîne classique <c>"…"</c> (ou <c>m"…"</c>), échappements décodés : ceux du C++
    /// (<c>\n</c>, <c>\t</c>, <c>\r</c>, <c>\0</c>, <c>\a</c>, <c>\b</c>, <c>\f</c>, <c>\v</c>,
    /// <c>\\</c>, <c>\"</c>, <c>\'</c>, <c>\xHH</c>) et <c>\u{XXXX}</c>.
    /// </summary>
    /// <param name="text">Le texte du token, guillemets (et préfixe <c>m</c>) compris.</param>
    /// <param name="unknownEscape">
    /// Appelé pour chaque échappement invalide, avec sa position dans <paramref name="text"/> et sa
    /// longueur ; le décodage continue après lui.
    /// </param>
    public static string Quoted(string text, Action<int, int> unknownEscape) =>
        _Decode(text, text[0] == 'm' ? 2 : 1, text.Length - 1, template: false, unknownEscape);

    /// <summary>
    /// Texte d'un template, sans délimiteurs : mêmes échappements qu'une chaîne classique, plus
    /// <c>\{</c>, <c>\}</c> et <c>\`</c>.
    /// </summary>
    /// <param name="text">Le morceau de texte tel qu'écrit, entre deux interpolations.</param>
    /// <param name="unknownEscape">Appelé pour chaque échappement inconnu, avec sa position dans <paramref name="text"/> et sa longueur.</param>
    public static string Template(string text, Action<int, int> unknownEscape) =>
        _Decode(text, 0, text.Length, template: true, unknownEscape);

    /// <summary>Décode les échappements de <c>text[start..end[</c>.</summary>
    private static string _Decode(string text, int start, int end, bool template, Action<int, int> unknownEscape)
    {
        var result = new StringBuilder();

        for (var i = start; i < end; ++i)
        {
            if (text[i] != '\\')
            {
                result.Append(text[i]);
                continue;
            }

            var escape = i;
            var next = i + 1 < end ? text[i + 1] : '\0';
            ++i;
            switch (next)
            {
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case 'r': result.Append('\r'); break;
                case '0': result.Append('\0'); break;
                case 'a': result.Append('\a'); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'v': result.Append('\v'); break;
                case '\\': result.Append('\\'); break;
                case '"': result.Append('"'); break;
                case '\'': result.Append('\''); break;
                case '{' or '}' or '`' when template: result.Append(next); break;

                case 'x' when i + 2 < end && _IsHex(text, i + 1, 2):
                    result.Append((char)Convert.ToInt32(text.Substring(i + 1, 2), 16));
                    i += 2;
                    break;

                case 'u' when _TryUnicode(text, i + 1, out var rune, out var length):
                    result.Append(rune.ToString());
                    i += length;
                    break;

                default:
                    unknownEscape(escape, Math.Min(2, end - escape));
                    break;
            }
        }

        return result.ToString();
    }

    private static bool _IsHex(string text, int start, int count) =>
        text.AsSpan(start, count).ContainsAnyExcept("0123456789abcdefABCDEF") is false;

    /// <summary><c>{XXXX}</c> à partir de <paramref name="start"/> : un point de code Unicode valide, de 1 à 6 chiffres hexadécimaux.</summary>
    private static bool _TryUnicode(string text, int start, out Rune rune, out int length)
    {
        rune = default;
        length = 0;
        if (start >= text.Length || text[start] != '{') return false;

        var close = text.IndexOf('}', start);
        if (close < 0 || close - start - 1 is < 1 or > 6 || !_IsHex(text, start + 1, close - start - 1)) return false;

        var value = Convert.ToInt32(text.Substring(start + 1, close - start - 1), 16);
        if (!Rune.IsValid(value)) return false;

        rune = new Rune(value);
        length = close - start + 1;
        return true;
    }
}
