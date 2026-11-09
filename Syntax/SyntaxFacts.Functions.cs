using Rotomeca.Core.Optionals;

namespace Rgl.Syntax;

/// <summary>Questions sur le lexique : les fonctions de <see cref="SyntaxFacts"/>, les tables sont dans <c>SyntaxFacts.cs</c>.</summary>
internal static partial class SyntaxFacts
{
    /// <summary>
    /// Texte fixe de <paramref name="kind"/> : <c>";"</c> pour <see cref="TokenKind.Semicolon"/>,
    /// <c>"var"</c> pour <see cref="TokenKind.VarKeyword"/>. Sert aux messages (« <c>;</c> attendu »)
    /// et à l'affichage des tokens dans les tests golden.
    /// </summary>
    /// <param name="kind">Kind dont on veut le texte.</param>
    /// <returns>
    /// Le texte, ou une valeur vide pour les kinds sans texte fixe : <see cref="TokenKind.Identifier"/>,
    /// les littéraux, <see cref="TokenKind.Bad"/>, <see cref="TokenKind.EndOfFile"/> et
    /// <see cref="TokenKind.ReservedKeyword"/>.
    /// </returns>
    public static MayBe<string> GetText(TokenKind kind) =>
        _textsByKind.TryGetValue(kind, out var text) ? MayBe<string>.Some(text) : MayBe<string>.Null;

    /// <summary>Indique si <paramref name="item"/> peut commencer un identificateur : lettre ASCII ou <c>_</c>.</summary>
    /// <param name="item">Caractère à tester.</param>
    /// <returns><see langword="true"/> pour <c>[a-zA-Z_]</c>.</returns>
    public static bool IsIdentifierStart(char item) => char.IsAsciiLetter(item) || item == '_';

    /// <summary>Indique si <paramref name="item"/> peut continuer un identificateur : lettre ASCII, chiffre ou <c>_</c>.</summary>
    /// <param name="item">Caractère à tester.</param>
    /// <returns><see langword="true"/> pour <c>[a-zA-Z0-9_]</c>.</returns>
    public static bool IsIdentifierPart(char item) => char.IsAsciiLetterOrDigit(item) || item == '_';

    /// <summary>Indique si <paramref name="text"/> est un suffixe de durée : <c>ms</c>, <c>s</c>, <c>min</c> ou <c>h</c>.</summary>
    /// <param name="text">Mot collé à la fin d'un nombre.</param>
    /// <returns><see langword="true"/> si le nombre est une durée (<c>100ms</c>).</returns>
    public static bool IsDurationSuffix(string text) => text is "ms" or "s" or "min" or "h";

    /// <summary>
    /// Indique si un token de cette sorte peut terminer une valeur. Décide du sens d'un
    /// <c>/</c> qui suit : division après une valeur (<c>a / 2</c>, <c>f() / 2</c>), début
    /// de regex sinon (<c>var r: /a+/;</c>). C'est la règle de JavaScript.
    /// </summary>
    /// <param name="kind">Sorte du token qui précède le <c>/</c>.</param>
    /// <returns><see langword="true"/> pour un identificateur, un littéral, <c>)</c>, <c>]</c>, <c>$</c>, <c>#</c> ou un type primitif.</returns>
    public static bool CanEndValue(TokenKind kind) =>
        kind is TokenKind.Identifier
            or TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.DurationLiteral
            or TokenKind.StringLiteral or TokenKind.RawStringLiteral or TokenKind.TemplateStringLiteral
            or TokenKind.MutableStringLiteral or TokenKind.SymbolLiteral or TokenKind.RegexLiteral
            or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NullKeyword
            or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.Dollar or TokenKind.Hash
            or TokenKind.Degree
        || IsPrimitiveType(kind);

    /// <summary>Niveau de précédence d'un opérateur placé devant son opérande : plus fort que tous les opérateurs binaires.</summary>
    public const int PrefixPrecedence = 16;

    /// <summary>Opérateur binaire de sorte <paramref name="kind"/>, s'il en est un.</summary>
    /// <param name="kind">Token rencontré après un opérande.</param>
    /// <returns>Sa précédence et son associativité, ou une valeur vide si ce n'est pas un opérateur binaire.</returns>
    public static MayBe<BinaryOperatorInfo> GetBinaryOperator(TokenKind kind) =>
        _binaryOperatorsByKind.TryGetValue(kind, out var op) ? MayBe<BinaryOperatorInfo>.Some(op) : MayBe<BinaryOperatorInfo>.Null;

    /// <summary>Indique si <paramref name="kind"/> est un opérateur placé devant son opérande (<c>-</c>, <c>not</c>, <c>increment</c>…).</summary>
    /// <param name="kind">Token rencontré au début d'une expression.</param>
    /// <returns><see langword="true"/> pour un opérateur préfixe.</returns>
    public static bool IsPrefixOperator(TokenKind kind) => _prefixOperators.Contains(kind) || kind is TokenKind.Star or TokenKind.Ampersand or TokenKind.DeleteKeyword;

    /// <summary>Indique si <paramref name="kind"/> est le mot-clé d'un type primitif (<c>int</c>, <c>string</c>…).</summary>
    /// <param name="kind">Sorte de token à tester.</param>
    /// <returns><see langword="true"/> pour un mot-clé de la catégorie <see cref="KeywordCategory.PrimitiveType"/>.</returns>
    public static bool IsPrimitiveType(TokenKind kind) =>
        _keywords.Some(keyword => keyword.Kind == kind && keyword.Category == KeywordCategory.PrimitiveType);
}

