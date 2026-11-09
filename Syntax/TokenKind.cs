namespace Rgl.Syntax;

/// <summary>
/// Sorte d'un token produit par le lexer. Le parseur ne regarde que cette valeur ; le texte
/// exact (valeur d'un littéral, nom d'un identificateur) reste dans le token.
/// </summary>
/// <remarks>
/// <para>
/// Les opérateurs sont reconnus en plus longue correspondance : devant <c>&lt;</c>, le lexer
/// essaie <c>&lt;-</c>, puis <c>&lt;=</c>, puis <c>&lt;</c>. Un <c>&gt;&gt;</c> est toujours
/// produit comme deux <see cref="Greater"/> (le décalage s'écrit <c>shr</c>).
/// </para>
/// <para>
/// Les mots-clés contextuels (<c>forge</c>, <c>done</c>, <c>eval</c>, <c>last</c>…) n'ont pas
/// de valeur ici : ils sont produits comme <see cref="Identifier"/>, et le parseur les
/// reconnaît dans leur contexte. Les espaces et commentaires ne sont pas des tokens mais des
/// trivia, rattachées aux tokens.
/// </para>
/// </remarks>
internal enum TokenKind
{
    #region Spéciaux

    /// <summary>Caractère ou séquence invalide ; le diagnostic est déjà signalé, le lexer continue après.</summary>
    Bad,

    /// <summary>Fin du fichier, toujours le dernier token ; son span est vide, à <c>Content.Length</c>.</summary>
    EndOfFile,

    /// <summary>Nom : <c>[a-zA-Z_][a-zA-Z0-9_]*</c>, 255 caractères au plus. Couvre aussi les mots-clés contextuels.</summary>
    Identifier,

    /// <summary>Bloc C++ opaque, du mot cpp à son accolade fermante.</summary>
    CppBlock,

    #endregion

    #region Littéraux

    /// <summary>Entier : <c>1_000</c>, <c>0xFF</c>, <c>0b1010</c>, <c>0o17</c>.</summary>
    IntegerLiteral,

    /// <summary>Flottant : <c>1.5</c>, <c>1.5e3</c> (jamais <c>1.</c>).</summary>
    FloatLiteral,

    /// <summary>Durée : nombre suivi de <c>ms</c>, <c>s</c>, <c>min</c> ou <c>h</c> (<c>100ms</c>).</summary>
    DurationLiteral,

    /// <summary>Chaîne classique avec échappements C++ et <c>\u{XXXX}</c> : <c>"a\n"</c>.</summary>
    StringLiteral,

    /// <summary>Chaîne brute, sans échappement, <c>''</c> pour une apostrophe : <c>'C:\chemin'</c>.</summary>
    RawStringLiteral,

    /// <summary>Template évalué à l'exécution : <c>`x = {x}`</c>. Les <c>{…}</c> sont analysés plus tard.</summary>
    TemplateStringLiteral,

    /// <summary>Chaîne mutable (<c>mstring</c>), préfixe <c>m</c> : <c>m"modifiable"</c>.</summary>
    MutableStringLiteral,

    /// <summary>Symbole interné : <c>§nom</c>.</summary>
    SymbolLiteral,

    /// <summary>Regex native : <c>/motif/flags</c>.</summary>
    RegexLiteral,

    #endregion

    #region Ponctuation

    /// <summary><c>(</c></summary>
    OpenParen,

    /// <summary><c>)</c></summary>
    CloseParen,

    /// <summary><c>{</c></summary>
    OpenBrace,

    /// <summary><c>}</c></summary>
    CloseBrace,

    /// <summary><c>[</c></summary>
    OpenBracket,

    /// <summary><c>]</c></summary>
    CloseBracket,

    /// <summary><c>;</c></summary>
    Semicolon,

    /// <summary><c>:</c> : déclaration (<c>var x: 1</c>), type, cas de <c>switch</c>, champ.</summary>
    Colon,

    /// <summary><c>::</c> : déclaration qui déplace sa valeur quand c'est possible (<c>var b:: a</c>).</summary>
    ColonColon,

    /// <summary><c>,</c></summary>
    Comma,

    /// <summary><c>.</c> : accès à un membre.</summary>
    Dot,

    /// <summary><c>..</c> : plage (<c>int&lt;0..100&gt;</c>, <c>[T;N..M]</c>).</summary>
    DotDot,

    /// <summary><c>...</c> : décomposition (<c>[...items, 1]</c>, <c>{...pos, x: 3.0}</c>).</summary>
    DotDotDot,

    /// <summary><c>@</c> : annotation (<c>@tap</c>, <c>@deprecated</c>).</summary>
    At,

    /// <summary><c>$</c> : valeur courante (<c>@tap</c>, <c>case eval</c>) ou code de sortie du train d'entrée.</summary>
    Dollar,

    /// <summary><c>#</c> : valeur courante dans une entrée de blueprint (<c>Fans.boost(#)</c>).</summary>
    Hash,

    /// <summary><c>^</c> : point de sauvegarde (<c>^1 tryLoad</c>).</summary>
    Caret,

    /// <summary><c>?</c> : ternaire, <c>as?</c>.</summary>
    Question,

    /// <summary><c>!</c> : train sans guard (<c>!train</c>), type d'erreur (<c>T ! E</c>), <c>as!</c>, <c>*&lt;!N&gt;</c>.</summary>
    Bang,

    /// <summary><c>&amp;</c> : prise d'adresse dans <c>unsafe</c>, forme <c>-&gt; last &amp; {…}</c> d'un guard.</summary>
    Ampersand,

    /// <summary><c>|</c> : ouverture de bloc blueprint (<c>|AFan&gt;</c>).</summary>
    Bar,

    /// <summary><c>=</c> : liaisons natives (<c>=cpp=&gt;</c>, <c>=rgl=&gt;</c>), où il encadre le nom du langage.</summary>
    Equals,

    #endregion

    #region Opérateurs symboliques

    /// <summary><c>+</c></summary>
    Plus,

    /// <summary><c>-</c> : soustraction ou moins unaire.</summary>
    Minus,

    /// <summary><c>*</c> : multiplication, déréférencement dans <c>unsafe</c>, boucle de guard (<c>*&lt;10&gt;</c>).</summary>
    Star,

    /// <summary><c>/</c></summary>
    Slash,

    /// <summary><c>%</c></summary>
    Percent,

    /// <summary><c>&lt;</c> : comparaison ou ouverture de paramètres génériques.</summary>
    Less,

    /// <summary><c>&gt;</c> : comparaison ou fermeture de paramètres génériques.</summary>
    Greater,

    /// <summary><c>&lt;=</c></summary>
    LessEqual,

    /// <summary><c>&gt;=</c></summary>
    GreaterEqual,

    /// <summary><c>??</c> : coalescence, garde la gauche si elle est non <c>null</c>.</summary>
    QuestionQuestion,

    /// <summary><c>?°</c> : coalescence, garde la gauche si elle est non vide.</summary>
    QuestionDegree,

    /// <summary><c>?.</c> : accès sûr à un champ d'un nullable (<c>null</c> si la valeur l'est).</summary>
    QuestionDot,

    /// <summary><c>°</c> : retire la marque d'un ingot (<c>uid°</c> est sa valeur de base).</summary>
    Degree,

    /// <summary><c>|&gt;</c> : pipeline.</summary>
    Pipe,

    /// <summary><c>||&gt;</c> : parcours de collection ; garde les éléments valides, chaque échec part sur le rail.</summary>
    EachPipe,

    /// <summary><c>|!|&gt;</c> : parcours de collection ; abandonne tout au premier échec.</summary>
    EachStrictPipe,

    /// <summary><c>|S|&gt;</c> : parcours des lignes d'un mold en colonnes ; échoue si les colonnes n'ont pas la même taille.</summary>
    SoaPipe,

    /// <summary><c>||S|&gt;</c> : parcours des lignes d'un mold en colonnes ; s'arrête à la colonne la plus courte.</summary>
    SoaShortestPipe,

    /// <summary><c>|+&gt;</c> : accumulation d'un tableau en une seule valeur (<c>notes |+&gt; 0 with ajouter</c>).</summary>
    FoldPipe,

    /// <summary><c>|]&gt;</c> : fermeture d'un bloc blueprint.</summary>
    BlueprintClose,

    /// <summary><c>!&gt;</c> : rail d'erreur.</summary>
    Rail,

    /// <summary><c>=&gt;</c> : corps expression d'un gear, lambda, liaison native.</summary>
    FatArrow,

    /// <summary><c>-&gt;</c> : écriture vers une cible (<c>x -&gt; console</c>), forme <c>-&gt; T</c> d'un guard.</summary>
    Arrow,

    #endregion

    #region Affectations (instructions, jamais dans une expression)

    /// <summary><c>&lt;-</c> : assignation, ou lecture depuis une source (<c>var l &lt;- console</c>).</summary>
    Assign,

    /// <summary><c>&lt;-|</c> : assignation qui déplace sa valeur quand c'est possible (<c>b &lt;-| a</c>).</summary>
    MoveAssign,

    /// <summary><c>+&lt;-</c></summary>
    PlusAssign,

    /// <summary><c>-&lt;-</c></summary>
    MinusAssign,

    /// <summary><c>*&lt;-</c></summary>
    StarAssign,

    /// <summary><c>/&lt;-</c></summary>
    SlashAssign,

    /// <summary><c>°&lt;-</c> : assignation sans contrôle de marque (ingot).</summary>
    DegreeAssign,

    #endregion

    #region Symboles réservés (toujours une erreur)

    /// <summary><c>++</c> : réservé, utiliser <c>increment</c> ou <c>post_increment</c>.</summary>
    PlusPlus,

    /// <summary><c>--</c> : réservé, utiliser <c>decrement</c> ou <c>post_decrement</c>.</summary>
    MinusMinus,

    /// <summary><c>?:</c> : réservé, utiliser <c>??</c> (non <c>null</c>), <c>?°</c> (non vide) ou <c>or</c>.</summary>
    QuestionColon,

    #endregion

    #region Mots-clés : déclarations

    /// <summary><c>gear</c></summary>
    GearKeyword,

    /// <summary><c>pure</c></summary>
    PureKeyword,

    /// <summary><c>guard</c></summary>
    GuardKeyword,

    /// <summary><c>emit</c></summary>
    EmitKeyword,

    /// <summary><c>unsafe</c></summary>
    UnsafeKeyword,

    /// <summary><c>train</c></summary>
    TrainKeyword,

    /// <summary><c>mold</c></summary>
    MoldKeyword,

    /// <summary><c>soa</c></summary>
    SoaKeyword,

    /// <summary><c>precast</c></summary>
    PrecastKeyword,

    /// <summary><c>ingot</c></summary>
    IngotKeyword,

    /// <summary><c>enum</c></summary>
    EnumKeyword,

    /// <summary><c>dyn_enum</c></summary>
    DynEnumKeyword,

    /// <summary><c>dyn</c></summary>
    DynKeyword,

    /// <summary><c>wrapper</c></summary>
    WrapperKeyword,

    /// <summary><c>descriptor</c></summary>
    DescriptorKeyword,

    /// <summary><c>blueprint</c></summary>
    BlueprintKeyword,

    /// <summary><c>var</c></summary>
    VarKeyword,

    /// <summary><c>mut</c></summary>
    MutKeyword,

    /// <summary><c>const</c></summary>
    ConstKeyword,

    /// <summary><c>undefine</c></summary>
    UndefineKeyword,

    /// <summary><c>import</c></summary>
    ImportKeyword,

    #endregion

    #region Mots-clés : contrôle

    /// <summary><c>if</c></summary>
    IfKeyword,

    /// <summary><c>else</c></summary>
    ElseKeyword,

    /// <summary><c>switch</c></summary>
    SwitchKeyword,

    /// <summary><c>case</c></summary>
    CaseKeyword,

    /// <summary><c>default</c></summary>
    DefaultKeyword,

    /// <summary><c>break</c></summary>
    BreakKeyword,

    /// <summary><c>goto</c></summary>
    GotoKeyword,

    /// <summary><c>match</c></summary>
    MatchKeyword,

    /// <summary><c>return</c></summary>
    ReturnKeyword,

    /// <summary><c>fail</c></summary>
    FailKeyword,

    /// <summary><c>spawn</c></summary>
    SpawnKeyword,

    /// <summary><c>await</c></summary>
    AwaitKeyword,

    #endregion

    #region Mots-clés : opérateurs en mots

    /// <summary><c>and</c></summary>
    AndKeyword,

    /// <summary><c>or</c></summary>
    OrKeyword,

    /// <summary><c>xor</c></summary>
    XorKeyword,

    /// <summary><c>not</c></summary>
    NotKeyword,

    /// <summary><c>eq</c> : égalité (<c>a eq b</c>, <c>a not eq b</c>).</summary>
    EqKeyword,

    /// <summary><c>is</c></summary>
    IsKeyword,

    /// <summary><c>as</c> (aussi <c>as?</c> et <c>as!</c>, suivis de <see cref="Question"/> ou <see cref="Bang"/>).</summary>
    AsKeyword,

    /// <summary><c>new</c></summary>
    NewKeyword,

    /// <summary><c>delete</c></summary>
    DeleteKeyword,

    /// <summary><c>invert</c></summary>
    InvertKeyword,

    /// <summary><c>increment</c></summary>
    IncrementKeyword,

    /// <summary><c>decrement</c></summary>
    DecrementKeyword,

    /// <summary><c>post_increment</c></summary>
    PostIncrementKeyword,

    /// <summary><c>post_decrement</c></summary>
    PostDecrementKeyword,

    /// <summary><c>bitand</c></summary>
    BitAndKeyword,

    /// <summary><c>bitor</c></summary>
    BitOrKeyword,

    /// <summary><c>bitxor</c></summary>
    BitXorKeyword,

    /// <summary><c>shl</c></summary>
    ShlKeyword,

    /// <summary><c>shr</c></summary>
    ShrKeyword,

    #endregion

    #region Mots-clés : littéraux

    /// <summary><c>true</c></summary>
    TrueKeyword,

    /// <summary><c>false</c></summary>
    FalseKeyword,

    /// <summary><c>null</c></summary>
    NullKeyword,

    #endregion

    #region Mots-clés : types primitifs (E011 s'ils servent d'identificateur)

    /// <summary><c>byte</c></summary>
    ByteKeyword,

    /// <summary><c>ubyte</c></summary>
    UByteKeyword,

    /// <summary><c>short</c></summary>
    ShortKeyword,

    /// <summary><c>ushort</c></summary>
    UShortKeyword,

    /// <summary><c>int</c></summary>
    IntKeyword,

    /// <summary><c>uint</c></summary>
    UIntKeyword,

    /// <summary><c>long</c></summary>
    LongKeyword,

    /// <summary><c>ulong</c></summary>
    ULongKeyword,

    /// <summary><c>float</c></summary>
    FloatKeyword,

    /// <summary><c>double</c></summary>
    DoubleKeyword,

    /// <summary><c>bool</c></summary>
    BoolKeyword,

    /// <summary><c>string</c></summary>
    StringKeyword,

    /// <summary><c>mstring</c></summary>
    MStringKeyword,

    /// <summary><c>symbol</c></summary>
    SymbolKeyword,

    /// <summary><c>datetime</c></summary>
    DateTimeKeyword,

    /// <summary><c>regex</c></summary>
    RegexKeyword,

    /// <summary><c>duration</c></summary>
    DurationKeyword,

    /// <summary><c>bytes</c></summary>
    BytesKeyword,

    /// <summary><c>void</c></summary>
    VoidKeyword,

    /// <summary><c>char</c></summary>
    CharKeyword,

    /// <summary><c>ptr</c></summary>
    PtrKeyword,

    #endregion

    #region Mots réservés

    /// <summary>
    /// Mot réservé et interdit (<c>class</c>, <c>for</c>, <c>try</c>, <c>async</c>, <c>let</c>,
    /// <c>undefined</c>…) : toujours une erreur. Le mot exact est dans le texte du token.
    /// </summary>
    ReservedKeyword,
    UptrKeyword,

    #endregion
}
