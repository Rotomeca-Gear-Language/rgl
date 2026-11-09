using System.Collections.Frozen;
using System.Collections.Immutable;
using Rotomeca.Core.Collections;
using Rotomeca.Core.Optionals;
using Rotomeca.Utils.Collections;
using Rotomeca.Utils.Types;

namespace Rgl.Syntax;

/// <summary>Catégorie d'un mot-clé, qui la range parmi les déclarations, le contrôle, les opérateurs, les littéraux ou les types.</summary>
internal enum KeywordCategory
{
    /// <summary>Introduit une déclaration (<c>gear</c>, <c>var</c>, <c>import</c>…) ; sert à la récupération sur erreur du parseur.</summary>
    Declaration,

    /// <summary>Contrôle de flux (<c>if</c>, <c>switch</c>, <c>return</c>…).</summary>
    Control,

    /// <summary>Opérateur écrit en mot (<c>and</c>, <c>not</c>, <c>bitand</c>…).</summary>
    WordOperator,

    /// <summary>Littéral écrit en mot (<c>true</c>, <c>false</c>, <c>null</c>).</summary>
    Literal,

    /// <summary>Type primitif (<c>int</c>, <c>string</c>…) ; ne peut pas servir d'identificateur (E011).</summary>
    PrimitiveType,
}

/// <summary>Une ligne de la table des mots-clés : le mot, son kind et sa catégorie.</summary>
/// <param name="Text">Le mot tel qu'il s'écrit dans le source.</param>
/// <param name="Kind">Le kind du token produit pour ce mot.</param>
/// <param name="Category">La catégorie du mot-clé.</param>
internal readonly record struct KeywordInfo(string Text, TokenKind Kind, KeywordCategory Category);

/// <summary>Une ligne de la table des symboles : le texte du symbole et son kind.</summary>
/// <param name="Text">Le symbole tel qu'il s'écrit dans le source.</param>
/// <param name="Kind">Le kind du token produit pour ce symbole.</param>
internal readonly record struct SymbolInfo(string Text, TokenKind Kind);

/// <summary>
/// Une ligne de la table des opérateurs binaires : le token, son niveau de précédence (plus il
/// est haut, plus l'opérateur lie fort) et son associativité.
/// </summary>
/// <param name="Kind">Le token de l'opérateur.</param>
/// <param name="Precedence">Niveau de précédence, de 3 (coalescence) à 14 (multiplicatifs).</param>
/// <param name="IsRightAssociative">Vrai si <c>a op b op c</c> se lit <c>a op (b op c)</c>.</param>
internal readonly record struct BinaryOperatorInfo(TokenKind Kind, int Precedence, bool IsRightAssociative = false);

/// <summary>
/// Ce que le langage sait de ses tokens : tables et questions, sans état. Les données du
/// lexique vivent ici ; le lexer et le parseur ne font que les interroger.
/// </summary>
/// <remarks>
/// Chaque table dérivée est calculée à partir d'une table source unique. Les champs statiques
/// s'initialisent dans l'ordre du fichier : une table source doit toujours être déclarée
/// avant les tables qui en dérivent.
/// </remarks>
internal static partial class SyntaxFacts
{
    /// <summary>
    /// Table source des mots-clés, rangés par catégorie. Les mots-clés contextuels n'y sont pas ;
    /// les mots réservés sont
    /// dans <see cref="_reservedWords"/>.
    /// </summary>
    private static readonly RArray<KeywordInfo> _keywords = RArray<KeywordInfo>.Of(
        // Déclarations
        new("gear", TokenKind.GearKeyword, KeywordCategory.Declaration),
        new("pure", TokenKind.PureKeyword, KeywordCategory.Declaration),
        new("guard", TokenKind.GuardKeyword, KeywordCategory.Declaration),
        new("emit", TokenKind.EmitKeyword, KeywordCategory.Declaration),
        new("unsafe", TokenKind.UnsafeKeyword, KeywordCategory.Declaration),
        new("train", TokenKind.TrainKeyword, KeywordCategory.Declaration),
        new("mold", TokenKind.MoldKeyword, KeywordCategory.Declaration),
        new("soa", TokenKind.SoaKeyword, KeywordCategory.Declaration),
        new("precast", TokenKind.PrecastKeyword, KeywordCategory.Declaration),
        new("ingot", TokenKind.IngotKeyword, KeywordCategory.Declaration),
        new("enum", TokenKind.EnumKeyword, KeywordCategory.Declaration),
        new("dyn_enum", TokenKind.DynEnumKeyword, KeywordCategory.Declaration),
        new("dyn", TokenKind.DynKeyword, KeywordCategory.Declaration),
        new("wrapper", TokenKind.WrapperKeyword, KeywordCategory.Declaration),
        new("descriptor", TokenKind.DescriptorKeyword, KeywordCategory.Declaration),
        new("blueprint", TokenKind.BlueprintKeyword, KeywordCategory.Declaration),
        new("var", TokenKind.VarKeyword, KeywordCategory.Declaration),
        new("mut", TokenKind.MutKeyword, KeywordCategory.Declaration),
        new("const", TokenKind.ConstKeyword, KeywordCategory.Declaration),
        new("undefine", TokenKind.UndefineKeyword, KeywordCategory.Declaration),
        new("import", TokenKind.ImportKeyword, KeywordCategory.Declaration),

        // Contrôle
        new("if", TokenKind.IfKeyword, KeywordCategory.Control),
        new("else", TokenKind.ElseKeyword, KeywordCategory.Control),
        new("switch", TokenKind.SwitchKeyword, KeywordCategory.Control),
        new("case", TokenKind.CaseKeyword, KeywordCategory.Control),
        new("default", TokenKind.DefaultKeyword, KeywordCategory.Control),
        new("break", TokenKind.BreakKeyword, KeywordCategory.Control),
        new("goto", TokenKind.GotoKeyword, KeywordCategory.Control),
        new("match", TokenKind.MatchKeyword, KeywordCategory.Control),
        new("return", TokenKind.ReturnKeyword, KeywordCategory.Control),
        new("fail", TokenKind.FailKeyword, KeywordCategory.Control),
        new("spawn", TokenKind.SpawnKeyword, KeywordCategory.Control),
        new("await", TokenKind.AwaitKeyword, KeywordCategory.Control),

        // Opérateurs en mots
        new("and", TokenKind.AndKeyword, KeywordCategory.WordOperator),
        new("or", TokenKind.OrKeyword, KeywordCategory.WordOperator),
        new("xor", TokenKind.XorKeyword, KeywordCategory.WordOperator),
        new("not", TokenKind.NotKeyword, KeywordCategory.WordOperator),
        new("eq", TokenKind.EqKeyword, KeywordCategory.WordOperator),
        new("is", TokenKind.IsKeyword, KeywordCategory.WordOperator),
        new("as", TokenKind.AsKeyword, KeywordCategory.WordOperator),
        new("new", TokenKind.NewKeyword, KeywordCategory.WordOperator),
        new("delete", TokenKind.DeleteKeyword, KeywordCategory.WordOperator),
        new("invert", TokenKind.InvertKeyword, KeywordCategory.WordOperator),
        new("increment", TokenKind.IncrementKeyword, KeywordCategory.WordOperator),
        new("decrement", TokenKind.DecrementKeyword, KeywordCategory.WordOperator),
        new("post_increment", TokenKind.PostIncrementKeyword, KeywordCategory.WordOperator),
        new("post_decrement", TokenKind.PostDecrementKeyword, KeywordCategory.WordOperator),
        new("bitand", TokenKind.BitAndKeyword, KeywordCategory.WordOperator),
        new("bitor", TokenKind.BitOrKeyword, KeywordCategory.WordOperator),
        new("bitxor", TokenKind.BitXorKeyword, KeywordCategory.WordOperator),
        new("shl", TokenKind.ShlKeyword, KeywordCategory.WordOperator),
        new("shr", TokenKind.ShrKeyword, KeywordCategory.WordOperator),

        // Littéraux
        new("true", TokenKind.TrueKeyword, KeywordCategory.Literal),
        new("false", TokenKind.FalseKeyword, KeywordCategory.Literal),
        new("null", TokenKind.NullKeyword, KeywordCategory.Literal),

        // Types primitifs
        new("byte", TokenKind.ByteKeyword, KeywordCategory.PrimitiveType),
        new("ubyte", TokenKind.UByteKeyword, KeywordCategory.PrimitiveType),
        new("short", TokenKind.ShortKeyword, KeywordCategory.PrimitiveType),
        new("ushort", TokenKind.UShortKeyword, KeywordCategory.PrimitiveType),
        new("int", TokenKind.IntKeyword, KeywordCategory.PrimitiveType),
        new("uint", TokenKind.UIntKeyword, KeywordCategory.PrimitiveType),
        new("long", TokenKind.LongKeyword, KeywordCategory.PrimitiveType),
        new("ulong", TokenKind.ULongKeyword, KeywordCategory.PrimitiveType),
        new("float", TokenKind.FloatKeyword, KeywordCategory.PrimitiveType),
        new("double", TokenKind.DoubleKeyword, KeywordCategory.PrimitiveType),
        new("bool", TokenKind.BoolKeyword, KeywordCategory.PrimitiveType),
        new("string", TokenKind.StringKeyword, KeywordCategory.PrimitiveType),
        new("mstring", TokenKind.MStringKeyword, KeywordCategory.PrimitiveType),
        new("symbol", TokenKind.SymbolKeyword, KeywordCategory.PrimitiveType),
        new("datetime", TokenKind.DateTimeKeyword, KeywordCategory.PrimitiveType),
        new("regex", TokenKind.RegexKeyword, KeywordCategory.PrimitiveType),
        new("duration", TokenKind.DurationKeyword, KeywordCategory.PrimitiveType),
        new("bytes", TokenKind.BytesKeyword, KeywordCategory.PrimitiveType),
        new("void", TokenKind.VoidKeyword, KeywordCategory.PrimitiveType),
        new("char", TokenKind.CharKeyword, KeywordCategory.PrimitiveType),
        new("uptr", TokenKind.UptrKeyword, KeywordCategory.PrimitiveType),
        new("ptr", TokenKind.PtrKeyword, KeywordCategory.PrimitiveType));

    /// <summary>
    /// Table source des mots réservés et interdits : ils produisent tous un token
    /// <see cref="TokenKind.ReservedKeyword"/> et sont toujours une erreur. Les symboles réservés
    /// <c>++</c> et <c>--</c> sont dans <see cref="_symbols"/>.
    /// </summary>
    private static readonly FrozenSet<string> _reservedWords = RArray<string>.Of(
        // Héritage C++
        "auto", "class", "friend", "interface", "override", "private",
        "protected", "public", "struct", "template", "typedef", "virtual",

        // Exceptions, remplacées par guard et fail
        "try", "catch", "throw",

        // Boucles impératives
        "for", "while", "do", "continue",

        // Évolution future
        "async", "fn", "func", "function", "yield", "contract", "let", "frozen",

        // Interop web
        "undefined").ToFrozenSet();

    /// <summary>
    /// Table source des symboles : ponctuation, opérateurs, affectations et symboles réservés,
    /// dans l'ordre de <see cref="TokenKind"/>. Le lexer y cherche la plus longue correspondance.
    /// </summary>
    private static readonly RArray<SymbolInfo> _symbols = RArray<SymbolInfo>.Of(
        // Ponctuation
        new("(", TokenKind.OpenParen),
        new(")", TokenKind.CloseParen),
        new("{", TokenKind.OpenBrace),
        new("}", TokenKind.CloseBrace),
        new("[", TokenKind.OpenBracket),
        new("]", TokenKind.CloseBracket),
        new(";", TokenKind.Semicolon),
        new(":", TokenKind.Colon),
        new("::", TokenKind.ColonColon),
        new(",", TokenKind.Comma),
        new(".", TokenKind.Dot),
        new("..", TokenKind.DotDot),
        new("...", TokenKind.DotDotDot),
        new("@", TokenKind.At),
        new("$", TokenKind.Dollar),
        new("#", TokenKind.Hash),
        new("^", TokenKind.Caret),
        new("?", TokenKind.Question),
        new("!", TokenKind.Bang),
        new("&", TokenKind.Ampersand),
        new("|", TokenKind.Bar),
        new("=", TokenKind.Equals),

        // Opérateurs symboliques
        new("+", TokenKind.Plus),
        new("-", TokenKind.Minus),
        new("*", TokenKind.Star),
        new("/", TokenKind.Slash),
        new("%", TokenKind.Percent),
        new("<", TokenKind.Less),
        new(">", TokenKind.Greater),
        new("<=", TokenKind.LessEqual),
        new(">=", TokenKind.GreaterEqual),
        new("??", TokenKind.QuestionQuestion),
        new("?°", TokenKind.QuestionDegree),
        new("?.", TokenKind.QuestionDot),
        new("°", TokenKind.Degree),
        new("|>", TokenKind.Pipe),
        new("||>", TokenKind.EachPipe),
        new("|!|>", TokenKind.EachStrictPipe),
        new("|S|>", TokenKind.SoaPipe),
        new("||S|>", TokenKind.SoaShortestPipe),
        new("|+>", TokenKind.FoldPipe),
        new("|]>", TokenKind.BlueprintClose),
        new("!>", TokenKind.Rail),
        new("=>", TokenKind.FatArrow),
        new("->", TokenKind.Arrow),

        // Affectations
        new("<-", TokenKind.Assign),
        new("<-|", TokenKind.MoveAssign),
        new("+<-", TokenKind.PlusAssign),
        new("-<-", TokenKind.MinusAssign),
        new("*<-", TokenKind.StarAssign),
        new("/<-", TokenKind.SlashAssign),
        new("°<-", TokenKind.DegreeAssign),

        // Symboles réservés
        new("++", TokenKind.PlusPlus),
        new("--", TokenKind.MinusMinus),
        new("?:", TokenKind.QuestionColon));

    /// <summary>
    /// Table source des opérateurs binaires, du plus lié au moins lié. <c>not eq</c> s'écrit en
    /// deux mots : le parseur le lit comme <c>not (a eq b)</c>. Les opérateurs de pipeline et
    /// d'affectation n'en font pas partie : ce ne sont pas des opérateurs d'expression.
    /// </summary>
    private static readonly RArray<BinaryOperatorInfo> _binaryOperators = RArray<BinaryOperatorInfo>.Of(
        // Multiplicatifs
        new(TokenKind.Star, 14),
        new(TokenKind.Slash, 14),
        new(TokenKind.Percent, 14),

        // Additifs
        new(TokenKind.Plus, 13),
        new(TokenKind.Minus, 13),

        // Décalages
        new(TokenKind.ShlKeyword, 12),
        new(TokenKind.ShrKeyword, 12),

        // Bit à bit, plus prioritaires que les comparaisons : « a bitand b eq 0 » se lit « (a bitand b) eq 0 »
        new(TokenKind.BitAndKeyword, 11),
        new(TokenKind.BitXorKeyword, 10),
        new(TokenKind.BitOrKeyword, 9),

        // Comparaisons
        new(TokenKind.Less, 8),
        new(TokenKind.Greater, 8),
        new(TokenKind.LessEqual, 8),
        new(TokenKind.GreaterEqual, 8),

        // Égalité
        new(TokenKind.EqKeyword, 7),

        // Logique
        new(TokenKind.AndKeyword, 6),
        new(TokenKind.XorKeyword, 5),
        new(TokenKind.OrKeyword, 4),

        // Coalescence
        new(TokenKind.QuestionQuestion, 3, IsRightAssociative: true),
        new(TokenKind.QuestionDegree, 3, IsRightAssociative: true));

    /// <summary>Opérateurs unaires placés devant leur opérande (<c>-x</c>, <c>not ok</c>, <c>increment n</c>).</summary>
    private static readonly FrozenSet<TokenKind> _prefixOperators = RArray<TokenKind>.Of(
        TokenKind.Minus,
        TokenKind.NotKeyword,
        TokenKind.InvertKeyword,
        TokenKind.IncrementKeyword,
        TokenKind.DecrementKeyword,
        TokenKind.PostIncrementKeyword,
        TokenKind.PostDecrementKeyword).ToFrozenSet();

    /// <summary>Mots-clés indexés par leur texte ; dérivé de <see cref="_keywords"/>.</summary>
    private static readonly FrozenDictionary<string, KeywordInfo> _keywordsByText =
        _keywords.ToFrozenDictionary(keyword => keyword.Text, StringComparer.Ordinal);

    /// <summary>Symboles indexés par leur texte ; dérivé de <see cref="_symbols"/>.</summary>
    private static readonly FrozenDictionary<string, SymbolInfo> _symbolsByText =
        _symbols.ToFrozenDictionary(symbol => symbol.Text, StringComparer.Ordinal);

    /// <summary>
    /// Texte de chaque kind qui a un texte fixe (mots-clés et symboles) ; dérivé de
    /// <see cref="_keywords"/> et <see cref="_symbols"/>.
    /// </summary>
    private static readonly FrozenDictionary<TokenKind, string> _textsByKind =
        _keywords.Map(keyword => (keyword.Kind, keyword.Text))
            .Concat(_symbols.Map(symbol => (symbol.Kind, symbol.Text)))
            .ToFrozenDictionary(entry => entry.Kind, entry => entry.Text);

    /// <summary>Opérateurs binaires indexés par leur token ; dérivé de <see cref="_binaryOperators"/>.</summary>
    private static readonly FrozenDictionary<TokenKind, BinaryOperatorInfo> _binaryOperatorsByKind =
        _binaryOperators.ToFrozenDictionary(op => op.Kind);

    /// <summary>Tous les mots-clés, rangés par catégorie.</summary>
    public static IEnumerable<KeywordInfo> Keywords => _keywords;

    /// <summary>Tous les symboles, dans l'ordre de <see cref="TokenKind"/>.</summary>
    public static IEnumerable<SymbolInfo> Symbols => _symbols;

    /// <summary>Tous les mots réservés et interdits.</summary>
    public static IEnumerable<string> ReservedWords => _reservedWords;

    /// <summary>
    /// Taille maximal d'un symbole
    /// </summary>
    public static readonly RUint SymbolsMaxSize = RUint.CreateChecked(Symbols.Max(x => x.Text.Length));

    /// <summary>Kind du mot-clé <paramref name="text"/>, s'il en est un (la casse compte).</summary>
    /// <param name="text">Mot lu par le lexer.</param>
    /// <returns>Le kind du mot-clé, ou une valeur vide si <paramref name="text"/> n'est pas un mot-clé.</returns>
    public static MayBe<TokenKind> GetKeywordKind(string text) =>
        _keywordsByText.TryGetValue(text, out var keyword) ? MayBe<TokenKind>.Some(keyword.Kind) : MayBe<TokenKind>.Null;

    /// <summary>
    /// Kind du symbole dont le texte est exactement <paramref name="text"/>. Pour la plus longue
    /// correspondance, le lexer essaie les textes du plus long au plus court à partir de la
    /// position courante ; le premier reconnu est le bon.
    /// </summary>
    /// <param name="text">Texte candidat, par exemple <c>"+&lt;-"</c>.</param>
    /// <returns>Le kind du symbole, ou une valeur vide si <paramref name="text"/> n'est pas un symbole.</returns>
    public static MayBe<TokenKind> GetSymbolKind(string text) =>
        _symbolsByText.TryGetValue(text, out var symbol) ? MayBe<TokenKind>.Some(symbol.Kind) : MayBe<TokenKind>.Null;

    /// <summary>Indique si <paramref name="text"/> est un mot réservé et interdit (la casse compte).</summary>
    /// <param name="text">Mot lu par le lexer.</param>
    /// <returns><see langword="true"/> si le mot est réservé : le lexer produit alors un <see cref="TokenKind.ReservedKeyword"/>.</returns>
    public static bool IsReserved(string text) => _reservedWords.Contains(text);

    /// <summary>Longueur maximale d'un identificateur, au-delà, E026.</summary>
    public const int MaxIdentifierLength = 255;
}
