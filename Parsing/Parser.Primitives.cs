using Rgl.AST;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Parsing;

internal sealed partial class Parser
{
    private enum ExpectKind: byte
    {
        And,
        Or
    }

    private RUint _position = 0;

    private bool _IsEndToken() => _IsEndToken(0);
    private bool _IsEndToken(int add) => _position + add >= _tokens.Count;
    private bool _IsEndToken(RUint add) => _IsEndToken((int)add.Value);

    private int _PositionInt => (int)_position.Value;
    private Token _LastToken => _tokens[^1];
    private Token _Current => _IsEndToken() == false ? _tokens[_PositionInt] : _LastToken;

    /// <summary>Token situé <paramref name="add"/> positions après le courant, ou le token de fin au-delà.</summary>
    private Token _Future(int add = 1) => _IsEndToken(add) ? _LastToken : _tokens[_PositionInt + add];

    private Token _Consume()
    {
        var tmp = _Current;

        if (!_IsEndToken(1)) ++_position;

        _previous = tmp;
        return tmp;
    }

    /// <summary>Dernier token consommé ; <see langword="null"/> avant le premier.</summary>
    private Token? _previous;

    /// <summary>Début du token où la dernière erreur a été signalée, pour ne pas en empiler plusieurs au même endroit.</summary>
    private RUint? _lastErrorStart;

    /// <summary>
    /// Signale une erreur de syntaxe, sauf si une erreur est déjà signalée au même endroit, ou
    /// si le token courant est un <see cref="TokenKind.Bad"/> que le lexer a déjà signalé.
    /// </summary>
    /// <param name="span">Endroit souligné dans l'extrait de code.</param>
    /// <param name="code">Code de l'erreur (voir <see cref="DiagnosticCodes"/>).</param>
    /// <param name="message">Ce qui ne va pas, en une phrase courte.</param>
    /// <param name="label">Texte affiché sous le soulignement.</param>
    /// <param name="help">Suggestions : ce qu'on peut écrire à la place, ou la bonne formulation.</param>
    private void _Error(SourceSpan span, string code, string message, string? label = null, params string[] help)
    {
        if (_Current.Kind == TokenKind.Bad || _lastErrorStart == span.Start) return;

        _lastErrorStart = span.Start;
        _diagnosticBag.Error(span, code, message, label, help.Length == 0 ? null : help);
    }

    /// <summary>Span vide au début du token courant : là où un élément manquant était attendu.</summary>
    private SourceSpan _MissingAtCurrent() => new(_source, _Current.Span.Start, RUint.Zero);

    /// <summary>
    /// Span vide juste après le dernier token consommé : là où un <c>;</c> ou une <c>)</c> oubliés
    /// devaient se trouver, sur la ligne de ce qui précède plutôt qu'au début de la ligne suivante.
    /// </summary>
    private SourceSpan _MissingAfterPrevious() =>
        _previous is null ? _MissingAtCurrent() : new(_source, _previous.Span.End, RUint.Zero);

    /// <summary>
    /// Consomme un token de sorte <paramref name="kind"/>, ou signale E030 « <c>texte</c> attendu »
    /// juste après le token précédent, avec « manquant ici » sous l'endroit.
    /// </summary>
    /// <param name="kind">Sorte de token attendue.</param>
    /// <param name="context">Fin du message : ce que le token devait fermer ou terminer.</param>
    /// <param name="help">Suggestions affichées sous l'extrait.</param>
    /// <returns><see langword="true"/> si le token était là.</returns>
    private bool _ExpectOrReport(TokenKind kind, string context, params string[] help)
    {
        if (_ExpectAndConsume(kind)) return true;

        var text = SyntaxFacts.GetText(kind).ContinueIfNull(kind.ToString);
        _Error(_MissingAfterPrevious(), DiagnosticCodes.ExpectedToken, $"« {text} » attendu {context}", $"« {text} » manquant ici", help);
        return false;
    }

    /// <summary>
    /// Un nom attendu : consomme l'identificateur courant. Un nom de type primitif (<c>int</c>…) à
    /// sa place est signalé (E011) puis lu comme le nom ; tout autre token donne un nom manquant
    /// (E030).
    /// </summary>
    /// <param name="what">Ce que le nom désigne, pour les messages : « nom de variable »…</param>
    /// <param name="help">Exemple de la bonne forme, affiché en aide.</param>
    private Identifier _ParseName(string what, string help)
    {
        if (_Expect(TokenKind.Identifier)) return new Identifier(_Consume().Span);

        if (SyntaxFacts.IsPrimitiveType(_Current.Kind))
        {
            var keyword = _Consume();
            _Error(keyword.Span, DiagnosticCodes.TypeNameAsIdentifier, $"« {keyword.Text} » est un nom de type : il ne peut pas servir de {what}", "nom de type",
                "choisis un autre nom, par exemple en le complétant : valeur, total, texte…");
            return new Identifier(keyword.Span);
        }

        if (_Expect(TokenKind.ReservedKeyword))
        {
            var word = _Consume();
            _ReportReserved(word);
            return new Identifier(word.Span);
        }

        var missing = new Identifier(_MissingAtCurrent());
        _Error(missing.Span, DiagnosticCodes.ExpectedToken, $"{what} attendu", "nom manquant ici", help);
        return missing;
    }

    /// <summary>
    /// Un nom, éventuellement qualifié par un fichier importé : <c>ajouter</c> ou <c>Commandes.ajouter</c>. Le nom
    /// qualifié est un seul <see cref="Identifier"/>, dont le morceau de source couvre les deux noms.
    /// </summary>
    private Identifier _ParseQualifiedName(string what, string help)
    {
        var name = _ParseName(what, help);
        while (!name.IsMissing && _Expect(TokenKind.Dot) && _Future().Kind == TokenKind.Identifier)
        {
            _Consume();
            name = new Identifier(name.Span.Union(_Consume().Span));
        }
        return name;
    }

    private bool _ExpectOneOf(params TokenKind[] kinds) => kinds.Contains(_Current.Kind);
    private bool _Expect(TokenKind kind) => _Current.Kind == kind;
    private bool _Expect(TokenKind kind, string value) => _Expect(kind) && value == _Current.Text;
    private bool _ExpectAndConsume(TokenKind kind)
    {
        var val = _Expect(kind);

        if (val) _Consume();

        return val;
    }

    /// <summary>
    /// Resynchronisation : saute les tokens jusqu'au prochain <c>;</c> inclus. Elle s'arrête avant
    /// la <c>}</c> qui ferme le bloc courant et avant le début d'une déclaration (<c>gear</c>,
    /// <c>train</c>…), laissés à l'appelant : une erreur ne fait pas perdre la structure du fichier.
    /// Un bloc ouvert en chemin est sauté avec son contenu.
    /// </summary>
    private void _SkipToSemicolon()
    {
        var depth = 0;
        while (!_Expect(TokenKind.EndOfFile))
        {
            if (depth == 0 && (_Expect(TokenKind.CloseBrace) || _AtDeclarationStart())) return;

            var token = _Consume();
            if (token.Kind == TokenKind.OpenBrace) ++depth;
            else if (token.Kind == TokenKind.CloseBrace) --depth;
            else if (token.Kind == TokenKind.Semicolon && depth == 0) return;
        }
    }

    /// <summary>Indique si le token courant commence une déclaration de niveau fichier : <c>gear</c>, <c>guard</c>, <c>emit</c>, <c>train</c> ou <c>!train</c>.</summary>
    private bool _AtDeclarationStart() =>
        _ExpectOneOf(TokenKind.GearKeyword, TokenKind.GuardKeyword, TokenKind.EmitKeyword, TokenKind.TrainKeyword, TokenKind.UnsafeKeyword)
        || (_Expect(TokenKind.Bang) && _Future().Kind == TokenKind.TrainKeyword);

    /// <summary>
    /// Signale (E070) un mot réservé utilisé là où un nom, une valeur ou une instruction est attendu,
    /// avec ce qu'on écrit à la place en RGL.
    /// </summary>
    private void _ReportReserved(Token word)
    {
        var help = word.Text switch
        {
            "for" or "while" or "do" or "continue" =>
                "RGL n'a pas de boucle impérative : on répète avec un switch et goto, la récursion, ou ||> sur une collection",
            "try" or "catch" or "throw" => "RGL n'a pas d'exceptions : un guard échoue avec fail, et le train les reçoit sur son rail !>",
            "async" => "RGL n'écrit pas async à l'appel : un gear s'écrit @async devant sa déclaration (il rend une promesse que await attend), et task f(x) lance un gear ordinaire sur le pool de threads",
            "let" => "une variable se déclare avec var, avec son type (int x: 5;) ou avec const",
            _ => "ce mot est réservé par le langage : choisis un autre nom",
        };
        _Error(word.Span, DiagnosticCodes.ReservedWord, $"« {word.Text} » est un mot réservé", "réservé", help);
    }

}