namespace Rgl.Binding;

/// <summary>
/// Vérifie qu'un motif de regex s'écrit dans ce que le moteur (CTRE) sait compiler : le moteur refuse un
/// motif inconnu en faisant échouer Clang, et aucune erreur de Clang ne doit arriver jusqu'à l'utilisateur.
/// La vérification est volontairement un peu plus stricte que le moteur (mieux vaut refuser un motif
/// douteux avec une explication que de laisser Clang échouer).
/// <list type="bullet">
/// <item>Atomes : un caractère (tout sauf <c>\ [ ( ) ^ $ . * + ? { | }</c>), <c>.</c>, <c>^</c>, <c>$</c>, une classe <c>[…]</c>, un échappement.</item>
/// <item>Échappements : <c>\d \D \w \W \s \S \h \H \v \V \N</c>, <c>\b \B \A \z \Z</c>, <c>\n \r \t \f \a \e \0</c>, <c>\xHH \x{H…} \uHHHH \u{H…}</c>, <c>\1</c> à <c>\9</c> (un groupe déjà fermé), et un de <c>$ ( ) * + - . / &lt; &gt; ? [ \ " ] ^ { | }</c> pour le caractère lui-même.</item>
/// <item>Groupes : <c>( )</c>, <c>(?: )</c>, <c>(?&lt;nom&gt; )</c>, <c>(?&gt; )</c> (atomique), <c>(?= )</c>, <c>(?! )</c>, <c>(?&lt;= )</c>, <c>(?&lt;! )</c>, <c>(?i)</c> <c>(?m)</c> <c>(?s)</c> <c>(?c)</c>.</item>
/// <item>Répétitions : <c>* + ? {n} {n,} {n,m}</c>, suivies de <c>?</c> (paresseuse) ou <c>+</c> (possessive) ; une seule par atome.</item>
/// <item>Classes : <c>[abc] [^abc] [a-z] [\d_] [[:alpha:]]</c> ; un <c>-</c> ou un <c>]</c> littéral s'écrit <c>\-</c>, <c>\]</c>.</item>
/// </list>
/// Pas pris en charge : <c>\p{…}</c>, <c>\g</c>, <c>\k</c>, <c>\G</c>, <c>\Q…\E</c> et les modificateurs de groupe <c>(?i:…)</c>.
/// </summary>
internal sealed class RegexSyntax
{
    private const int MaxRepeat = 1000;

    private static readonly string[] PosixClasses =
        ["alnum", "alpha", "ascii", "blank", "cntrl", "digit", "graph", "lower", "print", "punct", "space", "upper", "word", "xdigit"];

    private readonly string _pattern;
    private int _position;
    private int _closedGroups;

    private RegexSyntax(string pattern) => _pattern = pattern;

    /// <summary>Un défaut du motif : où (indice dans le motif) et pourquoi.</summary>
    /// <param name="Offset">L'indice du caractère en cause.</param>
    /// <param name="Message">Ce qui ne va pas, et ce qu'on peut écrire à la place.</param>
    public readonly record struct Problem(int Offset, string Message);

    /// <summary>Le premier défaut de <paramref name="pattern"/>, ou <see langword="null"/> si le moteur le compile.</summary>
    public static Problem? Check(string pattern)
    {
        if (pattern.Length == 0) return new Problem(0, "le motif est vide");
        var syntax = new RegexSyntax(pattern);
        try
        {
            syntax._Alternation(topLevel: true);
            if (syntax._position < pattern.Length) return new Problem(syntax._position, "« ) » sans « ( » avant lui : écris \\) pour la parenthèse elle-même");
            return null;
        }
        catch (InvalidPatternException exception)
        {
            return new Problem(exception.Offset, exception.Message);
        }
    }

    private sealed class InvalidPatternException(int offset, string message) : Exception(message)
    {
        public int Offset { get; } = offset;
    }

    private InvalidPatternException _Fail(string message) => new(Math.Min(_position, _pattern.Length - 1), message);

    private InvalidPatternException _FailAt(int offset, string message) => new(offset, message);

    private bool _AtEnd => _position >= _pattern.Length;

    private char _Current => _pattern[_position];

    /// <summary>Alternatives séparées par <c>|</c>, jusqu'à la fin ou jusqu'à un <c>)</c> (non consommé).</summary>
    private void _Alternation(bool topLevel)
    {
        var first = true;
        while (true)
        {
            var empty = _Sequence();
            if (_AtEnd || _Current != '|') return;
            if (empty && !first) throw _Fail("deux « | » de suite : une alternative vide ne s'écrit qu'une fois");
            if (empty && topLevel) throw _Fail("une alternative ne commence pas par « | » en début de motif");
            ++_position;
            first = false;
        }
    }

    /// <summary>Une suite d'atomes et de répétitions ; rend vrai si elle est vide.</summary>
    private bool _Sequence()
    {
        var count = 0;
        while (!_AtEnd && _Current is not ('|' or ')'))
        {
            _Atom();
            _Repeat();
            ++count;
        }
        return count == 0;
    }

    private void _Atom()
    {
        var current = _Current;
        switch (current)
        {
            case '*' or '+' or '?':
                throw _Fail($"« {current} » répète ce qui le précède, et il n'y a rien avant : écris \\{current} pour le caractère");
            case '{' or '}':
                throw _Fail($"« {current} » s'écrit \\{current} sauf pour une répétition {{n}}, {{n,}} ou {{n,m}}");
            case '^' or '$' or '.':
                ++_position;
                return;
            case '[':
                _Class();
                return;
            case '(':
                _Group();
                return;
            case '\\':
                _Escape(inClass: false);
                return;
            default:
                _position += char.IsHighSurrogate(current) && _position + 1 < _pattern.Length ? 2 : 1;
                return;
        }
    }

    private void _Repeat()
    {
        if (_AtEnd) return;
        switch (_Current)
        {
            case '*' or '+' or '?':
                ++_position;
                break;
            case '{':
                _Counted();
                break;
            default:
                return;
        }

        if (!_AtEnd && _Current is '?' or '+') ++_position;
        if (!_AtEnd && _Current is '*' or '+' or '?' or '{')
            throw _Fail("une seule répétition par élément : mets-le entre parenthèses pour en ajouter une autre, par exemple (a+)*");
    }

    /// <summary><c>{n}</c>, <c>{n,}</c> ou <c>{n,m}</c>.</summary>
    private void _Counted()
    {
        var start = _position;
        ++_position;
        var low = _Number();
        if (low is null) throw _Fail("« { » attend un nombre : {n}, {n,} ou {n,m} ; écris \\{ pour l'accolade elle-même");
        if (!_AtEnd && _Current == ',')
        {
            ++_position;
            var high = _Number();
            if (high is { } known && known < low) throw _FailAt(start, $"{{{low},{known}}} : le minimum dépasse le maximum");
        }
        if (_AtEnd || _Current != '}') throw _Fail("« } » attendu pour fermer la répétition");
        ++_position;
    }

    private int? _Number()
    {
        var start = _position;
        var value = 0;
        while (!_AtEnd && char.IsAsciiDigit(_Current))
        {
            value = value * 10 + (_Current - '0');
            if (value > MaxRepeat) throw _Fail($"une répétition dépasse {MaxRepeat} : le moteur compile le motif en code, il grossirait trop");
            ++_position;
        }
        return _position == start ? null : value;
    }

    /// <summary>Un groupe, à partir de son <c>(</c>.</summary>
    private void _Group()
    {
        var open = _position;
        ++_position;
        var capturing = true;

        if (!_AtEnd && _Current == '?')
        {
            capturing = false;
            ++_position;
            if (_AtEnd) throw _Fail("groupe incomplet après « (? »");
            switch (_Current)
            {
                case ':' or '>' or '=' or '!':
                    ++_position;
                    break;
                case '<':
                    ++_position;
                    if (!_AtEnd && _Current is '=' or '!')
                    {
                        ++_position;
                        break;
                    }
                    _GroupName();
                    capturing = true;
                    break;
                case 'i' or 'm' or 's' or 'c':
                    while (!_AtEnd && _Current is 'i' or 'm' or 's' or 'c') ++_position;
                    if (_AtEnd || _Current != ')') throw _Fail("un changement de mode s'écrit (?i), (?m), (?s) ou (?c), sans corps : (?i:…) n'est pas pris en charge");
                    ++_position;
                    return;
                default:
                    throw _Fail($"« (?{_Current} » n'est pas pris en charge ; groupes possibles : (?: ), (?<nom> ), (?> ), (?= ), (?! ), (?<= ), (?<! )");
            }
        }

        _Alternation(topLevel: false);
        if (_AtEnd || _Current != ')') throw _FailAt(open, "« ( » jamais fermée : il manque un « ) »");
        ++_position;
        if (capturing) ++_closedGroups;
    }

    private void _GroupName()
    {
        var start = _position;
        while (!_AtEnd && (char.IsAsciiLetterOrDigit(_Current) || _Current == '_')) ++_position;
        if (_position == start || char.IsAsciiDigit(_pattern[start])) throw _Fail("le nom d'un groupe commence par une lettre ou « _ » : (?<nom>…)");
        if (_AtEnd || _Current != '>') throw _Fail("« > » attendu après le nom du groupe : (?<nom>…)");
        ++_position;
    }

    /// <summary>Un échappement, à partir de son <c>\</c> ; rend le caractère qu'il désigne, ou -1 pour une classe ou une assertion.</summary>
    private int _Escape(bool inClass)
    {
        var start = _position;
        ++_position;
        if (_AtEnd) throw _Fail("« \\ » en fin de motif : il échappe le caractère suivant");
        var current = _Current;
        ++_position;

        switch (current)
        {
            case 'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'h' or 'H' or 'v' or 'V':
                return -1;
            case 'N' or 'b' or 'B' or 'A' or 'z' or 'Z' when inClass:
                throw _FailAt(start, $"\\{current} n'est pas permis dans une classe [ ]");
            case 'N' or 'b' or 'B' or 'A' or 'z' or 'Z':
                return -1;
            case 'n': return '\n';
            case 'r': return '\r';
            case 't': return '\t';
            case 'f': return '\f';
            case 'a': return 7;
            case 'e': return 27;
            case '0': return 0;
            case 'x':
                return _Hexadecimal(exactDigits: 2);
            case 'u':
                return _Hexadecimal(exactDigits: 4);
            case >= '1' and <= '9':
                if (inClass) throw _FailAt(start, "une référence à un groupe (\\1…) n'est pas permise dans une classe [ ]");
                if (current - '0' > _closedGroups)
                    throw _FailAt(start, $"\\{current} désigne le groupe {current}, qui n'est pas fermé avant : numérote les groupes dans l'ordre d'ouverture");
                return -1;
            case '$' or '(' or ')' or '*' or '+' or '-' or '.' or '/' or '<' or '>' or '?' or '[' or '\\' or '"' or ']' or '^' or '{' or '|' or '}':
                return current;
            case 'p' or 'P':
                throw _FailAt(start, $"\\{current}{{…}} (propriété Unicode) n'est pas pris en charge ; écris une classe, par exemple [a-zà-ÿ]");
            case 'g' or 'k' or 'G' or 'K' or 'Q' or 'E' or 'c':
                throw _FailAt(start, $"\\{current} n'est pas pris en charge par le moteur de regex");
            default:
                throw _FailAt(start, $"échappement inconnu « \\{current} » : un caractère spécial s'échappe, une lettre garde son sens (\\d, \\w, \\n…)");
        }
    }

    /// <summary>Après <c>\x</c> ou <c>\u</c> : <c>HH</c> (ou <c>HHHH</c>) ou <c>{H…}</c>, une valeur Unicode valide.</summary>
    private int _Hexadecimal(int exactDigits)
    {
        if (!_AtEnd && _Current == '{')
        {
            ++_position;
            long value = 0;
            var digits = 0;
            while (!_AtEnd && char.IsAsciiHexDigit(_Current))
            {
                value = value * 16 + Convert.ToInt32(_Current.ToString(), 16);
                if (value > 0x10FFFF) throw _Fail("un caractère Unicode va jusqu'à 10FFFF");
                ++digits;
                ++_position;
            }
            if (digits == 0 || _AtEnd || _Current != '}') throw _Fail("\\x{…} et \\u{…} contiennent des chiffres hexadécimaux, puis « } »");
            ++_position;
            return (int)value;
        }

        var number = 0;
        for (var i = 0; i < exactDigits; ++i)
        {
            if (_AtEnd || !char.IsAsciiHexDigit(_Current)) throw _Fail($"{exactDigits} chiffres hexadécimaux attendus, ou {{…}} pour un nombre libre");
            number = number * 16 + Convert.ToInt32(_Current.ToString(), 16);
            ++_position;
        }
        return number;
    }

    /// <summary>Une classe <c>[…]</c>, à partir de son <c>[</c>.</summary>
    private void _Class()
    {
        var open = _position;
        ++_position;
        if (!_AtEnd && _Current == '^') ++_position;

        var items = 0;
        var previous = -1;
        while (true)
        {
            if (_AtEnd) throw _FailAt(open, "« [ » jamais fermée : il manque un « ] »");
            var current = _Current;

            if (current == ']')
            {
                if (items == 0) throw _Fail("une classe vide : écris \\] pour le crochet lui-même");
                ++_position;
                return;
            }

            if (current == '[')
            {
                _PosixClass();
                previous = -1;
            }
            else if (current == '-')
            {
                if (previous < 0 || _position + 1 >= _pattern.Length || _pattern[_position + 1] == ']')
                    throw _Fail("un « - » dans une classe sépare les bornes d'un intervalle (a-z) ; écris \\- pour le tiret lui-même");
                ++_position;
                var end = _RangeEnd();
                if (end >= 0 && previous > end) throw _Fail("l'intervalle est à l'envers : la borne basse dépasse la haute");
                previous = -1;
            }
            else if (current == '\\')
            {
                previous = _Escape(inClass: true);
            }
            else
            {
                previous = char.ConvertToUtf32(_pattern, _position);
                _position += char.IsHighSurrogate(current) && _position + 1 < _pattern.Length ? 2 : 1;
            }
            ++items;
        }
    }

    /// <summary>La borne haute d'un intervalle <c>a-z</c> : un caractère ou un échappement de caractère.</summary>
    private int _RangeEnd()
    {
        if (_AtEnd) throw _Fail("la borne haute de l'intervalle manque");
        var current = _Current;
        if (current is ']' or '[' or '-') throw _Fail($"« {current} » ne peut pas finir un intervalle : écris \\{current}");
        if (current == '\\')
        {
            var value = _Escape(inClass: true);
            if (value < 0) throw _Fail("une classe d'échappement (\\d, \\w…) ne peut pas finir un intervalle");
            return value;
        }
        var character = char.ConvertToUtf32(_pattern, _position);
        _position += char.IsHighSurrogate(current) && _position + 1 < _pattern.Length ? 2 : 1;
        return character;
    }

    /// <summary><c>[:alpha:]</c> ou <c>[:^alpha:]</c> dans une classe.</summary>
    private void _PosixClass()
    {
        var start = _position;
        if (_position + 1 >= _pattern.Length || _pattern[_position + 1] != ':')
            throw _Fail("un « [ » dans une classe s'écrit \\[ sauf pour une classe nommée [:alpha:]");
        _position += 2;
        if (!_AtEnd && _Current == '^') ++_position;
        var nameStart = _position;
        while (!_AtEnd && char.IsAsciiLetter(_Current)) ++_position;
        var name = _pattern[nameStart.._position];
        if (!PosixClasses.Contains(name)) throw _FailAt(start, $"classe nommée inconnue « [:{name}:] » ; possibles : {string.Join(", ", PosixClasses)}");
        if (_position + 1 >= _pattern.Length || _Current != ':' || _pattern[_position + 1] != ']') throw _Fail("« :] » attendu pour finir la classe nommée");
        _position += 2;
    }
}
