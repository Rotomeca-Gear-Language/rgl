using System.Text;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Le texte qui remplace les lignes <paramref name="FirstLine"/> à <paramref name="LastLine"/> (à partir de 0, fin de la dernière ligne comprise).</summary>
internal sealed record FormatEdit(int FirstLine, int LastLine, string Text);

/// <summary>
/// Le formatage du code : il ne change que l'espace en début et en fin de ligne et les lignes vides, jamais ce qui est écrit. L'indentation vient des
/// accolades, crochets et parenthèses ouverts (une ligne qui en ouvre plusieurs n'ajoute qu'un niveau), des sections d'un <c>switch</c> (le corps
/// d'un <c>case</c> est un niveau plus loin que son étiquette) et des lignes qui continuent une expression (un opérateur ou un pipeline en début de
/// ligne, un opérateur en fin de ligne). Les lignes à l'intérieur d'un texte, d'un bloc C++ ou d'un gabarit sont laissées telles quelles ; celles d'un
/// commentaire de bloc suivent le décalage de sa première ligne. Plus d'une ligne vide de suite devient une ; le fichier finit par une fin de ligne.
/// Le résultat est vérifié : si les jetons du texte formaté diffèrent de ceux d'origine, rien n'est changé.
/// </summary>
internal static class Formatter
{
    /// <summary>Un niveau d'indentation : quatre espaces, pour tous. Le formateur n'a aucun réglage (ni largeur, ni tabulations) : une seule mise en forme pour tout le monde.</summary>
    public const string IndentUnit = "    ";

    /// <summary>
    /// Le texte d'un fichier entier, formaté : le même texte s'il n'y a rien à changer, ou si le texte formaté n'aurait pas les mêmes jetons. C'est la mise en
    /// forme officielle : l'éditeur, <c>rgl format</c> et tout outil qui écrit du RGL (precasts engendrés) passent par elle.
    /// </summary>
    public static string FormatText(string path, string content)
    {
        var document = SemanticDocument.Create(path, content, new ProjectContext("", "", []));
        return Format(document) is { } edit ? edit.Text : content;
    }

    /// <summary>Les opérateurs qui, en début de ligne, continuent l'expression de la ligne d'avant.</summary>
    private static readonly HashSet<string> _leading = new(StringComparer.Ordinal)
    {
        "|>", "||>", "|+>", "|!|>", "|S|>", "||S|>", "!>", "->", "?", "??", "?°", ".", "?.", "and", "or", "xor", "eq", "+", "*", "/", "%", "<", ">", "<=", ">=",
        "bitand", "bitor", "bitxor", "shl", "shr", "=>", "as", "as?", "as!", "with", "by",
    };

    /// <summary>Les opérateurs qui, en fin de ligne, attendent leur second opérande sur la ligne suivante.</summary>
    private static readonly HashSet<string> _trailing = new(StringComparer.Ordinal)
    {
        "|>", "||>", "|+>", "|!|>", "|S|>", "||S|>", "!>", "->", "<-", "|", "+", "-", "*", "/", "%", "?", ":", "??", "?°", "and", "or", "xor", "eq", "=>", "<", ">", "<=", ">=", "=",
        "bitand", "bitor", "bitxor", "shl", "shr", "+<-", "-<-", "*<-", "/<-",
    };

    private sealed class Frame(char kind, int openLine, bool isSwitch)
    {
        public char Kind { get; } = kind;
        public int OpenLine { get; } = openLine;
        public bool IsSwitch { get; } = isSwitch;
        public bool HasLabel { get; set; }
    }

    private readonly record struct Item(SourceSpan Span, Token? Token, TriviaKind? Comment);

    /// <summary>
    /// Le texte qui remplace les lignes <paramref name="firstLine"/> à <paramref name="lastLine"/> (tout le document sans elles) ; <see langword="null"/> si
    /// rien ne change, ou si le texte formaté n'aurait pas les mêmes jetons.
    /// </summary>
    public static FormatEdit? Format(SemanticDocument document, int? firstLine = null, int? lastLine = null)
    {
        var content = document.Source.Content;
        var eol = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        var endsWithEol = content.EndsWith('\n');
        if (endsWithEol) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return null;

        var from = Math.Clamp(firstLine ?? 0, 0, lines.Count - 1);
        var to = Math.Clamp(lastLine ?? lines.Count - 1, from, lines.Count - 1);
        var formatted = _Lines(document, lines);

        // Pas de ligne vide au début du fichier ni deux de suite (sauf dans un texte) ; celles de la fin tombent.
        var replaced = new List<string>();
        var previousBlank = from == 0 || formatted[from - 1] is { Text.Length: 0 };
        var atStart = from == 0;
        for (var i = from; i <= to; ++i)
        {
            var (text, locked) = formatted[i];
            if (text.Length == 0 && !locked)
            {
                if (previousBlank || atStart) continue;
                previousBlank = true;
            }
            else previousBlank = false;
            atStart = false;
            replaced.Add(text);
        }
        var isLast = to == lines.Count - 1;
        if (isLast)
        {
            while (replaced.Count > 0 && replaced[^1].Length == 0) replaced.RemoveAt(replaced.Count - 1);
        }

        var tail = isLast ? eol : "";
        var replacement = string.Join(eol, replaced) + (replaced.Count > 0 ? tail : "");
        var original = string.Join(eol, lines.Skip(from).Take(to - from + 1)) + (isLast && endsWithEol ? eol : "");
        if (replacement == original) return null;

        // Le formatage ne doit jamais changer ce qui est écrit.
        var whole = string.Join(eol, lines.Take(from).Concat(replaced).Concat(lines.Skip(to + 1)));
        if (isLast ? replaced.Count > 0 : true) whole += eol;
        if (!_SameTokens(document, whole)) return null;
        return new FormatEdit(from, to, replacement);
    }

    private static bool _SameTokens(SemanticDocument document, string formatted)
    {
        var again = SemanticDocument.Create(document.Self.Path, formatted, document.Project);
        var before = document.Self.Tokens.Where(t => t.Kind != TokenKind.EndOfFile).Select(t => (t.Kind, t.Text));
        var after = again.Self.Tokens.Where(t => t.Kind != TokenKind.EndOfFile).Select(t => (t.Kind, t.Text));
        return before.SequenceEqual(after);
    }

    /// <summary>Les lignes formatées, sans fin de ligne ; une ligne « verrouillée » (dans un texte) ne se supprime jamais.</summary>
    private static (string Text, bool Locked)[] _Lines(SemanticDocument document, List<string> lines)
    {
        var source = document.Source;
        var content = source.Content;
        int LineOf(uint offset) => (int)source[Rotomeca.Utils.Types.RUint.CreateChecked(offset)].Line.Value - 1;
        int LineStart(int line) => (int)source.LineStart[line].Value;

        var items = new List<Item>();
        foreach (var token in document.Self.Tokens)
        {
            foreach (var trivia in token.Before) if (trivia.Kind is TriviaKind.SingleComment or TriviaKind.DocComment or TriviaKind.BlockComment) items.Add(new Item(trivia.Span, null, trivia.Kind));
            if (token.Kind != TokenKind.EndOfFile && token.Span.Length.Value > 0) items.Add(new Item(token.Span, token, null));
            foreach (var trivia in token.After) if (trivia.Kind is TriviaKind.SingleComment or TriviaKind.DocComment or TriviaKind.BlockComment) items.Add(new Item(trivia.Span, null, trivia.Kind));
        }
        items = [.. items.OrderBy(i => i.Span.Start.Value)];

        var result = new string[lines.Count];
        var untouched = new bool[lines.Count];
        var keepEnd = new bool[lines.Count];
        var shifted = new bool[lines.Count];
        for (var i = 0; i < lines.Count; ++i) result[i] = lines[i];

        // Les lignes que couvre un élément sur plusieurs lignes : à l'intérieur d'un texte, rien ne change ; la fin d'une ligne qui y entre non plus.
        foreach (var item in items)
        {
            var (first, last) = (LineOf(item.Span.Start.Value), LineOf(Math.Max(item.Span.End.Value, 1u) - 1));
            if (last <= first) continue;
            keepEnd[first] = true;
            for (var line = first + 1; line <= last; ++line) untouched[line] = true;
            if (item.Comment != TriviaKind.BlockComment) keepEnd[last] = true;
        }

        string Indent(int level) => string.Concat(Enumerable.Repeat(IndentUnit, level));
        int Width(string leading) => leading.Sum(c => c == '\t' ? IndentUnit.Length : 1);

        var frames = new List<Frame>();
        Token? previous = null;
        var previousLabelLine = false;
        var lineFirstSeen = new HashSet<int>();
        var labelOnLine = -1;
        var labelDepth = 0;
        var parenSwitch = new Stack<bool>();
        var lastClosedWasSwitch = false;

        foreach (var item in items)
        {
            var line = LineOf(item.Span.Start.Value);
            var isFirst = !untouched[line] && lineFirstSeen.Add(line) && content.AsSpan(LineStart(line), (int)item.Span.Start.Value - LineStart(line)).Trim().Length == 0;
            var token = item.Token;
            var isCloser = token is { Kind: TokenKind.CloseBrace or TokenKind.CloseBracket or TokenKind.CloseParen };

            if (isFirst)
            {
                var remaining = isCloser && frames.Count > 0 ? frames.Take(frames.Count - 1).ToList() : frames;
                var level = remaining.Select(f => f.OpenLine).Distinct().Count();
                if (remaining.Count > 0 && remaining[^1].IsSwitch)
                {
                    var isLabel = token is { Kind: TokenKind.CaseKeyword or TokenKind.DefaultKeyword };
                    if (!isLabel && remaining[^1].HasLabel) ++level;
                }
                // Un opérateur en tête de ligne continue la précédente ; un opérateur en fin de ligne aussi, sauf entre parenthèses ou crochets où les lignes
                // d'une liste (a and, b and, …) restent alignées.
                var inBrackets = remaining.Count > 0 && remaining[^1].Kind is '(' or '[';
                if (token is not null && !isCloser && previous is not null
                    && (_leading.Contains(token.Text) || !inBrackets && _trailing.Contains(previous.Text) && !(previousLabelLine && previous.Kind == TokenKind.Colon)))
                    ++level;

                var oldLeading = lines[line][..(lines[line].Length - lines[line].TrimStart().Length)];
                var text = lines[line].Trim();
                result[line] = Indent(level) + (keepEnd[line] ? lines[line].TrimStart() : text);

                // Un commentaire de bloc entraîne ses lignes : elles gardent leur écart avec la première.
                if (item.Comment == TriviaKind.BlockComment)
                {
                    var last = LineOf(Math.Max(item.Span.End.Value, 1u) - 1);
                    var delta = Width(Indent(level)) - Width(oldLeading);
                    for (var inner = line + 1; inner <= last; ++inner)
                    {
                        if (lines[inner].Trim().Length == 0) continue;
                        var inside = lines[inner][..(lines[inner].Length - lines[inner].TrimStart().Length)];
                        shifted[inner] = true;
                        result[inner] = new string(' ', Math.Max(0, Width(inside) + delta)) + lines[inner].TrimStart();
                    }
                }
            }

            if (token is null) continue;

            // La pile des ouvertures.
            switch (token.Kind)
            {
                case TokenKind.OpenParen:
                    parenSwitch.Push(previous is { Kind: TokenKind.SwitchKeyword });
                    frames.Add(new Frame('(', line, false));
                    break;

                case TokenKind.OpenBracket:
                    parenSwitch.Push(false);
                    frames.Add(new Frame('[', line, false));
                    break;

                case TokenKind.OpenBrace:
                    frames.Add(new Frame('{', line, lastClosedWasSwitch && previous is { Kind: TokenKind.CloseParen }));
                    break;

                case TokenKind.CloseParen or TokenKind.CloseBracket:
                    if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    lastClosedWasSwitch = parenSwitch.Count > 0 && parenSwitch.Pop();
                    break;

                case TokenKind.CloseBrace:
                    if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    break;

                case TokenKind.CaseKeyword or TokenKind.DefaultKeyword when frames.Count > 0 && frames[^1].IsSwitch && isFirst:
                    (labelOnLine, labelDepth) = (line, frames.Count);
                    break;

                case TokenKind.Colon when labelOnLine >= 0 && frames.Count == labelDepth && frames[^1].IsSwitch:
                    frames[^1].HasLabel = true;
                    labelOnLine = -1;
                    break;
            }
            if (token.Kind != TokenKind.CloseParen) lastClosedWasSwitch = false;
            previousLabelLine = lines[line].TrimStart().StartsWith("case", StringComparison.Ordinal) || lines[line].TrimStart().StartsWith("default", StringComparison.Ordinal);
            previous = token;
        }

        // Le blanc de fin de ligne, et les lignes qui n'ont que du blanc.
        var final = new (string, bool)[lines.Count];
        for (var i = 0; i < lines.Count; ++i)
        {
            if (untouched[i]) final[i] = (shifted[i] ? result[i] : lines[i], true);
            else if (lines[i].Trim().Length == 0) final[i] = ("", false);
            else final[i] = (keepEnd[i] ? result[i] : result[i].TrimEnd(), false);
        }
        return final;
    }
}
