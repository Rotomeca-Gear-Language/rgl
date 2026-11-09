using Rgl.Diagnostics;
using Rgl.Text;
using Rotomeca.Core.Collections;
using Rotomeca.Core.Optionals;
using Rotomeca.Utils.Types;

namespace Rgl.Syntax;

/// <summary>
/// Découpe un fichier source en tokens, chacun avec son span et ses trivia. Écrit à la main :
/// le lexer contient l'algorithme de lecture, et demande à <see cref="SyntaxFacts"/> tout ce
/// qui relève de la connaissance du langage (mots-clés, symboles, caractères d'identificateur).
/// </summary>
/// <remarks>
/// <para>
/// Les erreurs (E021 à E027) sont signalées dans le <see cref="DiagnosticBag"/> sans arrêter la
/// lecture : un caractère invalide devient un token <see cref="TokenKind.Bad"/> et le lexer
/// continue, pour que l'utilisateur voie toutes ses erreurs d'un coup. W006 est signalé pour un
/// fichier sans instruction.
/// </para>
/// <para>
/// Les symboles sont reconnus en plus longue correspondance : <c>&lt;-</c> est toujours un seul
/// token, et <c>&gt;&gt;</c> deux <see cref="TokenKind.Greater"/>. Un <c>/</c> est une division
/// après un token qui peut terminer une valeur (<see cref="SyntaxFacts.CanEndValue"/>), le début
/// d'une regex sinon.
/// </para>
/// </remarks>
/// <param name="source">Fichier à découper, déjà lu et décodé.</param>
/// <param name="diagnostics">Reçoit les erreurs lexicales et W006.</param>
internal sealed class Lexer(SourceText source, DiagnosticBag diagnostics)
{
    /// <summary>Résultat du découpage.</summary>
    /// <param name="Source">Fichier découpé.</param>
    /// <param name="Tokens">Les tokens, le dernier étant toujours <see cref="TokenKind.EndOfFile"/>.</param>
    public readonly record struct TokenizeResult(SourceText Source, IReadOnlyList<Token> Tokens);

    private readonly SourceText _source = source;
    private readonly string _content = source.Content;
    private readonly DiagnosticBag _diagnostics = diagnostics;

    /// <summary>Indice du prochain caractère à lire dans <see cref="_content"/>.</summary>
    private int _position = 0;

    /// <summary>Fin (exclue) du texte à découper : la fin du fichier, ou celle d'une tranche.</summary>
    private readonly int _end = source.Content.Length;

    /// <summary>Vrai pour une tranche du fichier (l'expression d'un template), qui peut être vide sans W006.</summary>
    private readonly bool _isFragment;

    /// <summary>
    /// Lexer d'une tranche du fichier : <c>[start, end[</c>, par exemple l'expression entre les
    /// accolades d'un template. Les spans restent ceux du fichier entier ; le token de fin est placé
    /// en <paramref name="end"/>.
    /// </summary>
    /// <param name="source">Le fichier.</param>
    /// <param name="diagnostics">Reçoit les erreurs lexicales.</param>
    /// <param name="start">Début de la tranche.</param>
    /// <param name="end">Fin (exclue) de la tranche.</param>
    public Lexer(SourceText source, DiagnosticBag diagnostics, int start, int end) : this(source, diagnostics)
    {
        _position = start;
        _end = end;
        _isFragment = true;
    }

    /// <summary>Sorte du dernier token lu ; vide en début de fichier. Décide du sens de <c>/</c>.</summary>
    private MayBe<TokenKind> _previousKind = MayBe<TokenKind>.Null;

    /// <summary>Résultat gardé après le premier appel, pour ne pas signaler deux fois les erreurs.</summary>
    private MayBe<TokenizeResult> _result = MayBe<TokenizeResult>.Null;

    /// <summary>
    /// Découpe tout le fichier. Les appels suivants renvoient le même résultat sans relire le
    /// fichier ni signaler à nouveau les erreurs.
    /// </summary>
    /// <returns>Les tokens du fichier, terminés par <see cref="TokenKind.EndOfFile"/>.</returns>
    public TokenizeResult Tokenize()
    {
        if (_result.HasValue) return _result.Value;

        RArray<Token> tokens = new();
        Token current;

        do
        {
            current = _NextToken();
            tokens.Push(current);
        }
        while (current.Kind != TokenKind.EndOfFile);

        if (tokens.Length == 1 && !_isFragment)
            _diagnostics.Warning(current.Span, DiagnosticCodes.EmptyFile, "fichier source vide : aucune instruction",
                help: ["ajoute un train, par exemple : train |> done;"]);

        _result = MayBe<TokenizeResult>.Some(new TokenizeResult(_source, [.. tokens]));
        return _result.Value;
    }

    #region Lecture des caractères

    /// <summary>Indique si <paramref name="add"/> caractères plus loin, on est au-delà de la fin du fichier.</summary>
    private bool _IsEndFile(int add = 0) => _position + add >= _end;

    /// <summary>Caractère courant ; à n'appeler qu'après avoir vérifié <see cref="_IsEndFile"/>.</summary>
    private char _Peek() => _content[_position];

    /// <summary>Caractère situé <paramref name="add"/> positions plus loin, ou une valeur vide au-delà de la fin.</summary>
    private MayBe<char> _Future(int add = 1) =>
        _IsEndFile(add) ? MayBe<char>.Null : MayBe<char>.Some(_content[_position + add]);

    /// <summary>Indique si le caractère situé <paramref name="add"/> positions plus loin vaut <paramref name="expected"/>.</summary>
    private bool _IsAt(char expected, int add = 0) => !_IsEndFile(add) && _content[_position + add] == expected;

    /// <summary>Indique si on est sur une fin de ligne : <c>\n</c> ou <c>\r\n</c>.</summary>
    private bool _IsEndOfLine() => _IsAt('\n') || (_IsAt('\r') && _IsAt('\n', 1));

    /// <summary>Span de <paramref name="start"/> à la position courante.</summary>
    private SourceSpan _SpanFrom(int start) =>
        SourceSpan.FromBounds(_source, RUint.CreateChecked(start), RUint.CreateChecked(_position));

    /// <summary>Span de <paramref name="length"/> caractères à partir de <paramref name="start"/>.</summary>
    private SourceSpan _Span(int start, int length) =>
        new(_source, RUint.CreateChecked(start), RUint.CreateChecked(length));

    #endregion

    #region Tokens

    /// <summary>Lit les trivia avant, un token, puis les trivia après jusqu'à la fin de ligne incluse.</summary>
    private Token _NextToken()
    {
        var before = _SkipTrivias(untilEndOfLine: false);

        var start = _position;
        var kind = _IsEndFile() ? TokenKind.EndOfFile : _ReadToken();
        var span = _SpanFrom(start);

        // Le token de fin n'a pas de trivia après : tout ce qui reste est déjà dans ses trivia avant.
        IReadOnlyList<Trivia> after = kind == TokenKind.EndOfFile ? [] : _SkipTrivias(untilEndOfLine: true);

        _previousKind = MayBe<TokenKind>.Some(kind);
        return new Token(kind, span, before, after);
    }

    /// <summary>Lit un token à la position courante (hors fin de fichier) et renvoie sa sorte.</summary>
    private TokenKind _ReadToken()
    {
        var current = _Peek();

        if (current == 'm' && _IsAt('"', 1))
        {
            ++_position;
            _ReadQuotedString('"');
            return TokenKind.MutableStringLiteral;
        }

        if (SyntaxFacts.IsIdentifierStart(current)) return _ReadWord();
        if (char.IsAsciiDigit(current)) return _ReadNumber();

        switch (current)
        {
            case '"':
                _ReadQuotedString('"');
                return TokenKind.StringLiteral;

            case '\'':
                _ReadRawString();
                return TokenKind.RawStringLiteral;

            case '`':
                _ReadQuotedString('`');
                return TokenKind.TemplateStringLiteral;

            case '§':
                return _ReadSymbolLiteral();

            case '/' when !_previousKind.HasValue || !SyntaxFacts.CanEndValue(_previousKind.Value):
                return _ReadRegex();
        }

        var symbol = _Symbol();
        if (symbol.HasValue) return symbol.Value;

        return _ReadInvalidCharacter();
    }

    /// <summary>
    /// Plus long symbole de la table à la position courante, en essayant les longueurs de
    /// <see cref="SyntaxFacts.SymbolsMaxSize"/> à 1 ; avance de sa longueur s'il y en a un.
    /// </summary>
    private MayBe<TokenKind> _Symbol()
    {
        var remaining = _end - _position;

        for (var length = Math.Min((int)SyntaxFacts.SymbolsMaxSize.Value, remaining); length > 0; --length)
        {
            var symbol = SyntaxFacts.GetSymbolKind(_content.Substring(_position, length));
            if (symbol.HasValue)
            {
                _position += length;
                return symbol;
            }
        }

        return MayBe<TokenKind>.Null;
    }

    /// <summary>Caractère qui ne commence aucun token : E023, token <see cref="TokenKind.Bad"/>.</summary>
    private TokenKind _ReadInvalidCharacter()
    {
        var start = _position;

        // Une paire de substitution (emoji…) forme un seul caractère pour l'utilisateur.
        _position += char.IsHighSurrogate(_Peek()) && !_IsEndFile(1) && char.IsLowSurrogate(_content[_position + 1]) ? 2 : 1;

        var span = _SpanFrom(start);
        _diagnostics.Error(span, DiagnosticCodes.InvalidCharacter, $"caractère invalide « {span.Text} » (U+{char.ConvertToUtf32(span.Text, 0):X4})",
            "ce caractère ne fait partie d'aucun mot ni symbole RGL",
            ["les noms n'utilisent que des lettres ASCII, des chiffres et « _ »",
             "pour écrire ce caractère, place-le dans une chaîne : \"…\""]);
        return TokenKind.Bad;
    }

    #endregion

    #region Mots et nombres

    /// <summary>Identificateur, mot-clé ou mot réservé.</summary>
    private TokenKind _ReadWord()
    {
        var start = _position;
        while (!_IsEndFile() && SyntaxFacts.IsIdentifierPart(_Peek())) ++_position;

        var span = _SpanFrom(start);
        var text = span.Text;

        if (text == "cpp" && CppBlockReader.BodyStart(_content, _position, _end) is { } cppStart)
        {
            var body = CppBlockReader.Read(_source, cppStart, _end, _diagnostics);
            _position = body.End;
            return TokenKind.CppBlock;
        }

        var keyword = SyntaxFacts.GetKeywordKind(text);
        if (keyword.HasValue) return keyword.Value;
        if (SyntaxFacts.IsReserved(text)) return TokenKind.ReservedKeyword;

        if (text.Length > SyntaxFacts.MaxIdentifierLength)
            _diagnostics.Error(span, DiagnosticCodes.IdentifierTooLong,
                $"identificateur trop long : {text.Length} caractères, {SyntaxFacts.MaxIdentifierLength} au plus",
                $"{text.Length} caractères", [$"raccourcis ce nom à {SyntaxFacts.MaxIdentifierLength} caractères au plus"]);

        return TokenKind.Identifier;
    }

    /// <summary>
    /// Nombre : entier décimal (<c>1_000</c>), en base (<c>0xFF</c>, <c>0b1010</c>, <c>0o17</c>),
    /// flottant (<c>1.5</c>, <c>1.5e3</c>) ou durée (<c>100ms</c>, <c>1.5s</c>).
    /// </summary>
    private TokenKind _ReadNumber()
    {
        var start = _position;

        if (_Peek() == '0' && _Future() is { HasValue: true } prefix && prefix.Value is 'x' or 'b' or 'o')
        {
            _ReadBasedInteger(prefix.Value);
            return _ReadSuffix(start, TokenKind.IntegerLiteral, allowDuration: false);
        }

        _SkipDigits();
        var kind = TokenKind.IntegerLiteral;

        if (_IsAt('.') && !_IsEndFile(1) && char.IsAsciiDigit(_content[_position + 1]))
        {
            ++_position;
            _SkipDigits();
            kind = TokenKind.FloatLiteral;
        }
        else if (_IsAt('.') && !_IsAt('.', 1))
        {
            // « 1. » est interdit : il faut écrire « 1.0 ». « 1..5 » reste une plage (DotDot).
            ++_position;
            _diagnostics.Error(_SpanFrom(start), DiagnosticCodes.IncompleteFloat, "flottant incomplet",
                "chiffre manquant après le point", [$"la bonne formulation est : {_SpanFrom(start).Text}0"]);
            kind = TokenKind.FloatLiteral;
        }

        if (_IsExponent())
        {
            _position += _IsAt('+', 1) || _IsAt('-', 1) ? 2 : 1;
            _SkipDigits();
            kind = TokenKind.FloatLiteral;
        }

        return _ReadSuffix(start, kind, allowDuration: true);
    }

    /// <summary>Indique si un exposant (<c>e3</c>, <c>E-2</c>) commence à la position courante.</summary>
    private bool _IsExponent()
    {
        if (!_IsAt('e') && !_IsAt('E')) return false;

        var digitAt = _IsAt('+', 1) || _IsAt('-', 1) ? 2 : 1;
        return !_IsEndFile(digitAt) && char.IsAsciiDigit(_content[_position + digitAt]);
    }

    /// <summary>Chiffres décimaux et séparateurs <c>_</c>.</summary>
    private void _SkipDigits()
    {
        while (!_IsEndFile() && (char.IsAsciiDigit(_Peek()) || _Peek() == '_')) ++_position;
    }

    /// <summary>Entier en base 16, 2 ou 8 ; E025 s'il n'a aucun chiffre.</summary>
    private void _ReadBasedInteger(char prefix)
    {
        var start = _position;
        _position += 2;

        Func<char, bool> isDigit = prefix switch
        {
            'x' => char.IsAsciiHexDigit,
            'b' => c => c is '0' or '1',
            _ => c => c is >= '0' and <= '7',
        };

        var digitsStart = _position;
        while (!_IsEndFile() && (isDigit(_Peek()) || _Peek() == '_')) ++_position;

        if (_position == digitsStart)
        {
            var name = prefix switch { 'x' => "hexadécimal", 'b' => "binaire", _ => "octal" };
            var example = prefix switch { 'x' => "0xFF", 'b' => "0b1010", _ => "0o17" };
            _diagnostics.Error(_SpanFrom(start), DiagnosticCodes.MissingDigits, $"littéral {name} sans chiffres",
                "chiffres attendus après le préfixe", [$"« 0{prefix} » doit être suivi d'au moins un chiffre, par exemple : {example}"]);
        }
    }

    /// <summary>
    /// Mot collé à la fin d'un nombre : un suffixe de durée en fait une durée ; tout autre mot
    /// (ou une durée sur un entier en base) donne E027 et un token <see cref="TokenKind.Bad"/>.
    /// </summary>
    private TokenKind _ReadSuffix(int numberStart, TokenKind kind, bool allowDuration)
    {
        if (_IsEndFile() || !SyntaxFacts.IsIdentifierPart(_Peek())) return kind;

        var suffixStart = _position;
        while (!_IsEndFile() && SyntaxFacts.IsIdentifierPart(_Peek())) ++_position;
        var suffix = _SpanFrom(suffixStart).Text;

        if (allowDuration && SyntaxFacts.IsDurationSuffix(suffix)) return TokenKind.DurationLiteral;

        _diagnostics.Error(_SpanFrom(numberStart), DiagnosticCodes.UnknownDurationSuffix, $"suffixe « {suffix} » inconnu après un nombre",
            "suffixe inconnu", ["une durée s'écrit avec ms, s, min ou h, par exemple : 100ms",
                                "pour un nombre suivi d'un nom, sépare-les par un espace"]);
        return TokenKind.Bad;
    }

    #endregion

    #region Chaînes, symboles et regex

    /// <summary>
    /// Chaîne classique <c>"…"</c> ou template <c>`…`</c>, avec échappements par <c>\</c>.
    /// Une chaîne classique s'arrête à la fin de ligne ; un template peut couvrir plusieurs lignes,
    /// et ses interpolations <c>{…}</c> sont sautées en entier : un accent grave à l'intérieur (un
    /// template imbriqué) ne ferme pas le template. Non fermée : E021.
    /// </summary>
    private void _ReadQuotedString(char quote)
    {
        var start = _position;
        ++_position;

        while (!_IsEndFile())
        {
            if (quote != '`' && _IsEndOfLine()) break;

            var current = _Peek();
            if (current == quote)
            {
                ++_position;
                return;
            }

            if (quote == '`' && current == '{')
            {
                _SkipInterpolation();
                continue;
            }

            // Un échappement saute le caractère suivant, guillemet compris ; il ne traverse pas une fin de ligne.
            _position += current == '\\' && !_IsEndFile(1) && !(quote != '`' && _content[_position + 1] is '\n' or '\r') ? 2 : 1;
        }

        _ReportUnterminated(start, quote == '`' ? "template non fermé" : "chaîne non fermée", quote);
    }

    /// <summary>
    /// Saute une interpolation de template, du <c>{</c> courant jusqu'à l'accolade qui la ferme, en
    /// sautant les chaînes et les templates imbriqués. S'arrête à la fin du fichier si elle n'est
    /// pas fermée : le template est alors signalé non fermé.
    /// </summary>
    private void _SkipInterpolation()
    {
        var depth = 0;
        while (!_IsEndFile())
        {
            switch (_Peek())
            {
                case '{':
                    ++depth;
                    ++_position;
                    break;

                case '}':
                    ++_position;
                    if (--depth == 0) return;
                    break;

                case '"' or '\'' or '`':
                    _SkipNestedString(_Peek());
                    break;

                default:
                    ++_position;
                    break;
            }
        }
    }

    /// <summary>
    /// Saute une chaîne placée dans une interpolation, sans rien signaler : le lexer de
    /// l'interpolation la lira et signalera ses erreurs. Une chaîne brute n'a pas d'échappement ;
    /// un template imbriqué a ses propres interpolations.
    /// </summary>
    private void _SkipNestedString(char quote)
    {
        ++_position;
        while (!_IsEndFile())
        {
            var current = _Peek();
            if (current == quote)
            {
                ++_position;
                return;
            }

            if (quote == '`' && current == '{') _SkipInterpolation();
            else _position += current == '\\' && quote != '\'' && !_IsEndFile(1) ? 2 : 1;
        }
    }

    /// <summary>
    /// Chaîne brute <c>'…'</c> : aucun échappement, <c>''</c> pour une apostrophe. Elle s'arrête
    /// à la fin de ligne ; non fermée : E021.
    /// </summary>
    private void _ReadRawString()
    {
        var start = _position;
        ++_position;

        while (!_IsEndFile() && !_IsEndOfLine())
        {
            if (_Peek() == '\'')
            {
                if (!_IsAt('\'', 1))
                {
                    ++_position;
                    return;
                }

                ++_position;
            }

            ++_position;
        }

        _ReportUnterminated(start, "chaîne brute non fermée", '\'');
    }

    /// <summary>
    /// Regex <c>/motif/flags</c> : <c>\</c> échappe, un <c>/</c> dans une classe <c>[…]</c> ne la
    /// ferme pas. Elle s'arrête à la fin de ligne ; non fermée : E021.
    /// </summary>
    private TokenKind _ReadRegex()
    {
        var start = _position;
        ++_position;
        var inClass = false;

        while (!_IsEndFile() && !_IsEndOfLine())
        {
            var current = _Peek();

            if (current == '/' && !inClass)
            {
                ++_position;
                while (!_IsEndFile() && char.IsAsciiLetter(_Peek())) ++_position;
                return TokenKind.RegexLiteral;
            }

            if (current == '[') inClass = true;
            else if (current == ']') inClass = false;

            _position += current == '\\' && !_IsEndFile(1) && _content[_position + 1] is not ('\n' or '\r') ? 2 : 1;
        }

        _ReportUnterminated(start, "regex non fermée", '/');
        return TokenKind.RegexLiteral;
    }

    /// <summary>Symbole interné <c>§nom</c> ; un <c>§</c> sans nom est un caractère invalide.</summary>
    private TokenKind _ReadSymbolLiteral()
    {
        if (!_IsEndFile(1) && SyntaxFacts.IsIdentifierStart(_content[_position + 1]))
        {
            ++_position;
            while (!_IsEndFile() && SyntaxFacts.IsIdentifierPart(_Peek())) ++_position;
            return TokenKind.SymbolLiteral;
        }

        return _ReadInvalidCharacter();
    }

    /// <summary>Signale E021 sur le délimiteur ouvrant situé à <paramref name="start"/>.</summary>
    private void _ReportUnterminated(int start, string what, char delimiter) =>
        _diagnostics.Error(_Span(start, 1), DiagnosticCodes.UnterminatedString, $"{what} : « {delimiter} » fermant manquant",
            "ouvert ici", delimiter == '`'
                ? ["ajoute le « ` » fermant"]
                : [$"ajoute le « {delimiter} » fermant avant la fin de la ligne", "pour du texte sur plusieurs lignes, utilise un template : `…`"]);

    #endregion

    #region Trivia

    /// <summary>
    /// Lit les trivia à la position courante : espaces, fins de ligne, commentaires. Avec
    /// <paramref name="untilEndOfLine"/> (trivia après un token), s'arrête juste après la
    /// première fin de ligne.
    /// </summary>
    private IReadOnlyList<Trivia> _SkipTrivias(bool untilEndOfLine)
    {
        RArray<Trivia> trivias = new();

        while (!_IsEndFile())
        {
            var start = _position;
            TriviaKind kind;

            if (_IsAt(' ') || _IsAt('\t') || (_IsAt('\r') && !_IsAt('\n', 1)))
            {
                // Un « \r » isolé (ancien Mac) n'est pas une fin de ligne : il compte comme espace.
                while (!_IsEndFile() && (_IsAt(' ') || _IsAt('\t') || (_IsAt('\r') && !_IsAt('\n', 1)))) ++_position;
                kind = TriviaKind.Whitespace;
            }
            else if (_IsEndOfLine())
            {
                _position += _IsAt('\r') ? 2 : 1;
                trivias.Push(new Trivia(TriviaKind.EndOfLine, _SpanFrom(start)));
                if (untilEndOfLine) break;
                continue;
            }
            else if (_IsAt('/') && _IsAt('/', 1))
            {
                kind = _IsAt('/', 2) && !_IsAt('/', 3) ? TriviaKind.DocComment : TriviaKind.SingleComment;
                while (!_IsEndFile() && !_IsEndOfLine()) ++_position;
            }
            else if (_IsAt('/') && _IsAt('*', 1))
            {
                _SkipBlockComment();
                kind = TriviaKind.BlockComment;
            }
            else break;

            trivias.Push(new Trivia(kind, _SpanFrom(start)));
        }

        return trivias.Length == 0 ? [] : [.. trivias];
    }

    /// <summary>
    /// Commentaire de bloc, jusqu'au premier <c>*/</c> (pas d'imbrication). La recherche
    /// commence après <c>/*</c> : <c>/*/</c> n'est pas fermé. Non fermé : E022, et le
    /// commentaire va jusqu'à la fin du fichier.
    /// </summary>
    private void _SkipBlockComment()
    {
        var start = _position;
        _position += 2;

        while (!_IsEndFile())
        {
            if (_IsAt('*') && _IsAt('/', 1))
            {
                _position += 2;
                return;
            }

            ++_position;
        }

        _diagnostics.Error(_Span(start, 2), DiagnosticCodes.UnterminatedBlockComment,
            "commentaire de bloc non fermé : « */ » manquant", "commentaire ouvert ici",
            ["ajoute « */ » à la fin du commentaire", "les commentaires de bloc ne s'imbriquent pas : le premier « */ » les ferme"]);
    }

    #endregion
}
