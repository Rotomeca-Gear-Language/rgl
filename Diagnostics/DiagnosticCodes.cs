namespace Rgl.Diagnostics;

/// <summary>
/// Codes des diagnostics émis par le compilateur : E pour une erreur, W pour un avertissement.
/// </summary>
internal static class DiagnosticCodes
{
    /// <summary>Bloc C++ mal formé ou signature native incompatible.</summary>
    public const string InvalidCppBlock = "E149";
    /// <summary>Code natif ou contrat de confiance interdit par le manifeste.</summary>
    public const string NativePolicy = "E150";
    /// <summary>Contrat @trusted invalide ou incohérent.</summary>
    public const string InvalidTrust = "E151";

    public const string UnsafePointerLeak = "W068";
    public const string UnsafeRequired = "E147";
    public const string NativePackRequired = "E148";

    /// <summary><c>var</c> dont le type ne peut pas être déduit de la valeur (<c>var x: null;</c>).</summary>
    public const string CannotInferType = "E001";

    /// <summary>Affectation ou <c>undefine</c> d'une variable qui n'existe pas.</summary>
    public const string UnknownVariable = "E002";

    /// <summary>Valeur d'un type différent de celui de la variable qui la reçoit (déclaration ou affectation).</summary>
    public const string AssignmentTypeMismatch = "E003";

    /// <summary>Type rappelé dans <c>nom: T &lt;-</c> différent du type déclaré de la variable.</summary>
    public const string TypeHintMismatch = "E004";

    /// <summary>Affectation d'une constante.</summary>
    public const string AssignToConstant = "E005";

    /// <summary>Chaîne, template ou regex non fermé.</summary>
    public const string UnterminatedString = "E021";

    /// <summary>Commentaire de bloc <c>/* … */</c> non fermé.</summary>
    public const string UnterminatedBlockComment = "E022";

    /// <summary>Caractère qui ne peut commencer aucun token.</summary>
    public const string InvalidCharacter = "E023";

    /// <summary>Flottant incomplet : <c>1.</c> au lieu de <c>1.0</c>.</summary>
    public const string IncompleteFloat = "E024";

    /// <summary>Littéral <c>0x</c>, <c>0b</c> ou <c>0o</c> sans chiffres.</summary>
    public const string MissingDigits = "E025";

    /// <summary>Identificateur de plus de 255 caractères.</summary>
    public const string IdentifierTooLong = "E026";

    /// <summary>Suffixe inconnu après un nombre (les durées acceptent <c>ms</c>, <c>s</c>, <c>min</c>, <c>h</c>).</summary>
    public const string UnknownDurationSuffix = "E027";

    /// <summary>Octet UTF-8 invalide dans un fichier source.</summary>
    public const string InvalidUtf8 = "E028";

    /// <summary>Fichier source encodé en UTF-16 ou UTF-32 (marque BOM détectée).</summary>
    public const string UnsupportedEncoding = "E029";

    /// <summary>Token attendu absent : <c>;</c>, <c>)</c>, <c>train</c>, nom de paramètre…</summary>
    public const string ExpectedToken = "E030";

    /// <summary>Étape de pipeline attendue après <c>|&gt;</c> ou <c>!&gt;</c> : gear, <c>done</c>, <c>fail</c> ou <c>$ valeur</c>.</summary>
    public const string ExpectedStep = "E031";

    /// <summary>Expression attendue : littéral, nom, appel ou expression entre parenthèses.</summary>
    public const string ExpectedExpression = "E032";

    /// <summary>Type attendu : type primitif, nom de type ou <c>[T]</c>.</summary>
    public const string ExpectedType = "E033";

    /// <summary>Contenu après le <c>;</c> du train : un fichier ne contient qu'un seul train.</summary>
    public const string UnexpectedAfterTrain = "E034";

    /// <summary>Train d'entrée dont la signature n'est pas <c>([string] args): uint</c>.</summary>
    public const string EntryTrainSignature = "E015";

    /// <summary>Gear appelé mais introuvable.</summary>
    public const string UnknownGear = "E035";

    /// <summary>Fichier d'entrée sans train d'entrée.</summary>
    public const string MissingEntryTrain = "E036";

    /// <summary>Train sans guard non préfixé par <c>!</c> (sauf <c>train |&gt; done;</c>).</summary>
    public const string MissingUnguardedBang = "E037";

    /// <summary>Chemin (train ou rail) qui ne se termine ni par <c>done</c>, ni par <c>fail</c>, ni par <c>$ valeur</c>.</summary>
    public const string MissingTerminator = "E038";

    /// <summary>Étape écrite après la terminaison d'un chemin : elle ne serait jamais exécutée.</summary>
    public const string StepAfterTerminator = "E039";

    /// <summary>Types incompatibles : opérandes d'un opérateur, code de sortie qui n'est pas un <c>uint</c>, entier trop grand…</summary>
    public const string TypeMismatch = "E040";

    /// <summary>Nom utilisé mais déclaré nulle part.</summary>
    public const string UnknownName = "E041";

    /// <summary>Séquence d'échappement inconnue dans une chaîne (<c>"\q"</c>).</summary>
    public const string UnknownEscape = "E042";

    /// <summary>Variable sans valeur : déclarée sans valeur initiale (<c>int a;</c>), ou utilisée après <c>undefine</c>.</summary>
    public const string VariableWithoutValue = "E006";

    /// <summary>Accolade de template mal formée : <c>{</c> sans <c>}</c>, ou <c>}</c> seul (une accolade littérale s'écrit <c>\{</c> ou <c>\}</c>).</summary>
    public const string MalformedTemplate = "E045";

    /// <summary>Pas de <c>increment&lt;X&gt;</c> invalide : X doit être un entier au moins égal à 1, qui tient dans le type de la variable.</summary>
    public const string InvalidIncrementStep = "E046";

    /// <summary>Division ou reste entier par un zéro connu à la compilation : le programme s'arrêterait à coup sûr.</summary>
    public const string DivisionByZero = "E047";

    /// <summary>Le cas visé par <c>goto</c> n'existe pas dans le switch.</summary>
    public const string GotoUnknownCase = "E007";

    /// <summary><c>goto</c> vers une valeur qu'aucun <c>case</c> constant ne porte, dans un switch qui a des <c>case eval</c>.</summary>
    public const string GotoEvalCase = "E008";

    /// <summary><c>$</c> hors d'un <c>case eval</c> (ou d'un <c>$ valeur</c> de fin de train).</summary>
    public const string DollarOutsideEval = "E048";

    /// <summary><c>break</c> ou <c>goto</c> hors d'un switch.</summary>
    public const string JumpOutsideSwitch = "E049";

    /// <summary>La valeur d'un switch modifie une variable : elle doit être pure.</summary>
    public const string ImpureSwitchValue = "E050";

    /// <summary>Valeur de <c>case</c> (ou cible de <c>goto</c>) qui n'est pas connue à la compilation.</summary>
    public const string NonConstantCase = "E051";

    /// <summary>Deux <c>case</c> de même valeur, ou deux <c>default</c>, dans un switch.</summary>
    public const string DuplicateCase = "E052";

    /// <summary><c>break N</c> plus profond que le nombre de switch englobants : il sort de tous.</summary>
    public const string BreakTooDeep = "W002";

    /// <summary>Cas non vide qui ne se termine ni par <c>break</c> ni par <c>goto</c> : il continue dans le cas suivant.</summary>
    public const string SwitchFallthrough = "W003";

    /// <summary>Instruction au niveau du fichier (variable, if, switch…) : elle s'écrit dans un gear.</summary>
    public const string StatementOutsideGear = "E053";

    /// <summary>Constante de niveau fichier écrite après un gear ou le train : les constantes viennent en tête.</summary>
    public const string ConstantAfterGear = "E054";

    /// <summary>Deux gears de même nom dans le fichier.</summary>
    public const string DuplicateGear = "E055";

    /// <summary>Plusieurs surcharges d'un gear conviennent aussi bien aux arguments de l'appel.</summary>
    public const string AmbiguousGear = "E152";

    /// <summary>Une arène (<c>arena { … }</c>) ou un pool (<c>ArenaPool.set(…)</c>) mal utilisé : une attente ou un gear asynchrone dedans, une taille qui n'est pas une constante.</summary>
    public const string ArenaScope = "E154";

    /// <summary>Un <c>soa mold</c> dont un champ n'est pas un tableau dynamique <c>[T]</c>, ou un parcours <c>|S|&gt;</c> mal formé.</summary>
    public const string SoaMold = "E153";

    /// <summary>Un gear qui renvoie une valeur peut atteindre la fin de son corps, ou un <c>return;</c> sans valeur.</summary>
    public const string MissingReturn = "E056";

    /// <summary>Un gear <c>pure</c> appelle un gear qui ne l'est pas.</summary>
    public const string ImpureCall = "E057";

    /// <summary>Appel d'un gear avec un mauvais nombre d'arguments.</summary>
    public const string ArgumentCount = "E058";

    /// <summary><c>return</c> hors d'un gear.</summary>
    public const string ReturnOutsideGear = "E059";

    /// <summary><c>fail</c>, <c>done</c> ou <c>as!</c> hors d'un guard : seul un guard peut échouer.</summary>
    public const string FailOutsideGuard = "E060";

    /// <summary>Un guard ou un emit appelé dans une expression : il ne s'appelle que comme étape d'un train.</summary>
    public const string GuardOrEmitInExpression = "E062";

    /// <summary>Un emit au milieu d'un chemin : il termine toujours le train ou le rail.</summary>
    public const string EmitNotLast = "E063";

    /// <summary>Train préfixé par <c>!</c> alors qu'il appelle un guard.</summary>
    public const string BangWithGuard = "E064";

    /// <summary><c>done</c> dans un guard dont le type de retour n'est pas celui de son premier paramètre.</summary>
    public const string DoneTypeMismatch = "E065";

    /// <summary>Entrée ou sortie (<c>-&gt; console</c>, <c>&lt;- console</c>) dans un gear <c>pure</c>.</summary>
    public const string InputOutputInPure = "E066";

    /// <summary>Emit qui termine le train d'entrée avec un type de retour autre que <c>void</c> ou <c>uint</c>.</summary>
    public const string EmitReturnType = "E067";

    /// <summary><c>pure</c> sur un guard ou un emit : seul un gear peut l'être.</summary>
    public const string PureGuardOrEmit = "E068";

    /// <summary>Rail <c>!&gt;</c> hors d'un train : un gear laisse remonter les erreurs de ses appels.</summary>
    public const string RailOutsideTrain = "E069";

    /// <summary>Mot réservé (<c>continue</c>, <c>for</c>, <c>class</c>…) utilisé comme nom, valeur ou instruction.</summary>
    public const string ReservedWord = "E070";

    /// <summary>Modification en place du contenu d'une variable qui n'est pas <c>mut</c> : <c>pos.x &lt;- 3.0</c>.</summary>
    public const string InPlaceModification = "E013";

    /// <summary>Ajout (<c>push</c>, <c>add</c>) dans un tableau ou un dictionnaire reçu en paramètre sans <c>mut</c>.</summary>
    public const string AppendToParameter = "E014";

    /// <summary>Deux champs de même nom dans un mold, un type mold anonyme ou une valeur de mold.</summary>
    public const string DuplicateField = "E071";

    /// <summary>Valeur par défaut d'un champ qui n'est pas connue à la compilation.</summary>
    public const string NonConstantDefault = "E072";

    /// <summary>Mold qui se contient lui-même, directement ou par d'autres molds (ou par ses spreads).</summary>
    public const string RecursiveMold = "E073";

    /// <summary>Champ inconnu dans <c>valeur.champ</c>, ou <c>.</c> sur une valeur qui n'est pas un mold.</summary>
    public const string UnknownField = "E074";

    /// <summary>Deux molds de même nom, ou un mold qui prend un nom de type réservé (<c>Exception</c>).</summary>
    public const string DuplicateMold = "E075";

    /// <summary>Symbole réservé : <c>++</c>, <c>--</c>, <c>?:</c>.</summary>
    public const string ReservedSymbol = "E076";

    /// <summary>Type nullable mal formé : <c>T??</c>, <c>void?</c>, le nullable d'un nullable.</summary>
    public const string InvalidNullable = "E077";

    /// <summary>Union ou résultat mal formé : <c>null</c>, un nullable ou <c>void</c> comme membre.</summary>
    public const string InvalidUnion = "E078";

    /// <summary>Bras de <c>match</c> qui ne correspond pas à la valeur : type absent de l'union, <c>ok</c>/<c>err</c> sur une union, type sur un résultat.</summary>
    public const string MatchArmMismatch = "E080";

    /// <summary>Deux bras de <c>match</c> pour le même cas, ou deux <c>default</c>.</summary>
    public const string DuplicateMatchArm = "E081";

    /// <summary><c>match</c> sans <c>default</c> qui ne couvre pas tous les cas.</summary>
    public const string NonExhaustiveMatch = "E082";

    /// <summary>Enum mal formé : vide, variante en double, type sous-jacent qui n'est pas un primitif.</summary>
    public const string InvalidEnum = "E083";

    /// <summary>
    /// Valeurs d'enum : toutes ou aucune selon que le type est écrit, connues à la compilation, du
    /// type sous-jacent, sans doublon.
    /// </summary>
    public const string InvalidEnumValue = "E084";

    /// <summary>Cast impossible : types sans relation, ou conversion que <c>as</c> ne fait pas (texte vers nombre).</summary>
    public const string InvalidCast = "E085";

    /// <summary>Valeur connue à la compilation qui ne tient pas dans le type d'un cast, ou hors des bornes d'un type borné.</summary>
    public const string OutOfRange = "E086";

    /// <summary>Type borné mal formé : type non numérique, borne inconnue à la compilation ou du mauvais type, borne basse au-dessus de la haute.</summary>
    public const string InvalidBounds = "E087";

    /// <summary><c>°</c> ou <c>°&lt;-</c> sur une valeur qui n'est pas un ingot.</summary>
    public const string NotAnIngot = "E088";

    /// <summary>Ingot mal formé : nom déjà pris par un gear, ingot qui repose sur lui-même.</summary>
    public const string InvalidIngot = "E089";

    /// <summary>Rail mal placé : sans crochets (<c>!&gt; [ … ]</c>), ou après une étape qui n'est pas un appel.</summary>
    public const string MisplacedRail = "E090";

    /// <summary><c>fail</c> d'une valeur qui n'est ni un texte ni une erreur (un mold avec <c>message</c> et <c>where</c>).</summary>
    public const string NotAnError = "E091";

    /// <summary>Rail derrière une étape qui ne peut pas échouer : il ne sera jamais pris.</summary>
    public const string UnreachableRail = "W004";

    /// <summary>Guard qui peut atteindre la fin de son corps : <c>done</c> implicite.</summary>
    public const string ImplicitDone = "W007";

    /// <summary>Taille de tableau invalide : inconnue à la compilation, trop petite, ou minimum au-dessus du maximum.</summary>
    public const string InvalidArraySize = "E092";

    /// <summary>Type de clé de dictionnaire sans ordre fiable (flottant, mold…).</summary>
    public const string InvalidDictionaryKey = "E093";

    /// <summary>Deux clés égales, connues à la compilation, dans un dictionnaire écrit.</summary>
    public const string DuplicateKey = "E094";

    /// <summary>Boucle <c>*</c> sur autre chose qu'un guard.</summary>
    public const string RepeatNotGuard = "E095";

    /// <summary>Guard bouclé par <c>*</c> qui ne renvoie pas le type de sa première entrée : sa valeur ne peut pas y revenir.</summary>
    public const string RepeatTypeMismatch = "E096";

    /// <summary>Point de reprise <c>^N</c> déjà posé dans le train, ou devant autre chose qu'un gear ou un guard à un seul paramètre.</summary>
    public const string InvalidCheckpoint = "E097";

    /// <summary>Reprise <c>^N</c> vers un point inconnu, ou que le chemin n'a pas encore passé.</summary>
    public const string UnknownCheckpoint = "E098";

    /// <summary>Limite de tours ou de reprises nulle, ou trop grande (<c>*&lt;0&gt;</c>, <c>^1&lt;0&gt;</c>).</summary>
    public const string InvalidRepeatLimit = "E099";

    /// <summary>
    /// Intervalle invalide : borne ou pas qui n'est pas un entier, bornes sans type commun, pas nul, ou
    /// pas qui va dans l'autre sens que des bornes connues (<c>[10..0 by 2]</c>).
    /// </summary>
    public const string InvalidRange = "E100";

    /// <summary>
    /// <c>mut</c> mal placé : sur une constante, sur un type qui n'est ni un tableau, ni un mold, ni un
    /// dictionnaire, sur un paramètre de train, sur un paramètre de lambda hors d'un parcours, ou un gear
    /// à paramètre <c>mut</c> passé comme une lambda.
    /// </summary>
    public const string InvalidMut = "E101";

    /// <summary>
    /// Argument d'un paramètre <c>mut</c> qui n'est pas une variable <c>mut</c> du même type, ou passé
    /// deux fois au même appel ; parcours qui modifie sur place autre chose qu'une variable <c>mut</c>.
    /// </summary>
    public const string InvalidMutArgument = "E102";

    /// <summary>Cast inutile : la valeur a déjà le type visé.</summary>
    public const string UselessCast = "W013";

    /// <summary>
    /// Déplacement impossible (<c>::</c>, <c>&lt;-|</c>) : la variable est une constante, un paramètre, une
    /// variable capturée par une lambda, ou sa valeur doit être convertie ; elle est copiée, mais le nom source est libéré.
    /// </summary>
    public const string MoveNotPossible = "W014";

    /// <summary><c>switch</c> sur un enum, sans <c>default</c>, qui ne couvre pas toutes les variantes.</summary>
    public const string NonExhaustiveEnumSwitch = "W012";

    /// <summary>Coalescence ou accès sûr inutile : la valeur n'est jamais <c>null</c> (ou jamais vide, ou toujours vide).</summary>
    public const string UselessCoalescence = "W011";

    /// <summary>Gear récursif, directement ou par d'autres gears : sa profondeur est limitée (clé <c>recursion</c>).</summary>
    public const string RecursiveGear = "W010";

    /// <summary>Nom déjà déclaré dans le même bloc (et pas libéré par <c>undefine</c>).</summary>
    public const string AlreadyDeclared = "E044";

    /// <summary>Redéclaration d'une variable libérée par <c>undefine</c>.</summary>
    public const string RedeclaredAfterUndefine = "W001";

    /// <summary>Fichier source vide : aucune instruction.</summary>
    public const string EmptyFile = "W006";

    /// <summary><c>&lt;-</c> dans une expression, lu comme <c>&lt; -</c> : <c>a&lt;-5</c> devient <c>a &lt; -5</c>.</summary>
    public const string GluedAssignInExpression = "W008";

    /// <summary>Conversion numérique implicite sans perte (<c>int</c> vers <c>double</c>…) : une notice, rien à corriger.</summary>
    public const string ImplicitConversion = "I001";

    /// <summary>Nom de type primitif (<c>int</c>, <c>string</c>…) utilisé comme identificateur.</summary>
    public const string TypeNameAsIdentifier = "E011";

    /// <summary>Variable d'un bloc imbriqué qui masque une variable de même nom d'un bloc englobant.</summary>
    public const string ShadowedVariable = "W009";

    /// <summary>Parcours d'un tableau de molds dont un champ est un tableau : cette colonne n'est pas aplatie.</summary>
    public const string NestedArrayNotFlattened = "W041";

    /// <summary>Un nom déclaré par l'utilisateur masque un nom pris dans la std.</summary>
    public const string StdNameMasked = "W015";

    /// <summary>Un flux de fichier déclaré <c>const</c> : il a un état interne qui change à chaque lecture.</summary>
    public const string FileStreamConstant = "W016";

    /// <summary>Le résultat d'un <c>spawn</c> est ignoré : il peut être une erreur.</summary>
    public const string SpawnResultIgnored = "W060";

    /// <summary>L'appel d'un gear annoté <c>@deprecated</c>.</summary>
    public const string DeprecatedUse = "W061";

    /// <summary>Un <c>@tap(before, …)</c> écrit après un <c>@tap</c> qui s'exécute après le gear.</summary>
    public const string TapOrder = "W062";

    /// <summary>Fichier <c>.forge</c> sans en-tête <c>forge:</c>.</summary>
    public const string MissingHeader = "E016";

    /// <summary>En-tête sans <c>description:</c>.</summary>
    public const string MissingDescription = "W005";

    /// <summary>
    /// Un nom désigne deux choses dans un fichier : train, gear, mold, enum, ingot, constante ou import
    /// partagent un seul espace de noms (<c>as</c> renomme un import qui gêne).
    /// </summary>
    public const string NameInUse = "E103";

    /// <summary>
    /// Déclaration qui n'est pas à sa place : commune écrite après un train nommé, déclaration qu'un fichier de ce
    /// genre ne contient pas, train nommé hors d'un fichier <c>forge: train</c>.
    /// </summary>
    public const string MisplacedDeclaration = "E104";

    /// <summary>Le fichier <c>.rgl</c> d'entrée n'a pas d'en-tête.</summary>
    public const string UnexpectedHeader = "E105";

    /// <summary>Genre de fichier inconnu après <c>forge:</c>.</summary>
    public const string UnknownFileKind = "E106";

    /// <summary>Import d'un fichier introuvable, ou ambigu.</summary>
    public const string ImportNotFound = "E107";

    /// <summary>Imports circulaires : le graphe des fichiers doit être sans cycle.</summary>
    public const string CircularImport = "E108";

    /// <summary><c>import X only {nom}</c> d'un nom que le fichier importé ne déclare pas.</summary>
    public const string UnknownImportedName = "E109";

    /// <summary>
    /// Un train appelle un train qui n'est pas écrit au-dessus de lui, ou une déclaration commune appelle un train
    /// du fichier.
    /// </summary>
    public const string TrainCallOrder = "E110";

    /// <summary>Un train dont les fins n'ont pas de type commun.</summary>
    public const string TrainResultType = "E111";

    /// <summary><c>export {nom}</c> d'un nom que l'index de la box n'a pas importé.</summary>
    public const string UnknownExport = "E113";

    /// <summary>Fichier <c>forge: train</c> sans train par défaut, ou bloc de train sans train.</summary>
    public const string MissingDefaultTrain = "E112";

    /// <summary>Un argument générique qui ne respecte pas la contrainte de son paramètre (<c>number</c>, <c>error</c>, <c>{TYPE champ}</c>).</summary>
    public const string ConstraintNotMet = "E114";

    /// <summary>Arguments génériques en trop, ou paramètre générique qu'aucun argument ne permet de déduire.</summary>
    public const string TypeArguments = "E115";

    /// <summary>Argument d'un paramètre de valeur qui n'est pas une valeur connue à la compilation, ou valeur là où un type est attendu (et inversement).</summary>
    public const string InvalidValueArgument = "E116";

    /// <summary>Déclaration générique invalide : nom de paramètre en double, valeur dans un mold, contrainte inconnue.</summary>
    public const string InvalidGenericDeclaration = "E117";

    /// <summary>Un opérateur que deux types de l'opération surchargent de la même façon, ou plusieurs définitions qui conviennent.</summary>
    public const string AmbiguousOperator = "E009";

    /// <summary><c>&lt;</c> défini sans <c>&gt;</c>, ou <c>&gt;</c> sans <c>&lt;</c>.</summary>
    public const string ComparisonPair = "E010";

    /// <summary>Opérateur ou conversion mal déclaré : mauvais nombre de paramètres, mold absent des opérandes, type de retour, doublon.</summary>
    public const string InvalidOverload = "E118";

    /// <summary>Extension mal déclarée : nom en double, <c>this</c> qui n'est pas du type étendu, constante ou gear sans <c>this</c> sur un type qui n'est pas nommé.</summary>
    public const string InvalidExtension = "E119";

    /// <summary>Paramètre par défaut mal placé ou mal écrit : avant un paramètre obligatoire, <c>mut</c>, <c>this</c>, dans un train ou un opérateur, valeur spéciale d'un mauvais type.</summary>
    public const string InvalidDefault = "E120";

    /// <summary>Precast mal écrit : champ sans valeur dans un precast qui déclare son mold, ou valeur non connue à la compilation.</summary>
    public const string InvalidPrecast = "E121";

    /// <summary>Namespace mal écrit, en double dans un fichier, ou déjà pris par un autre fichier du projet.</summary>
    public const string InvalidNamespace = "E123";

    /// <summary><c>spawn</c> mal placé (argument, étape de pipeline), mal écrit (options inconnues, exécutable <c>rgl</c>) ou sans la std.</summary>
    public const string InvalidSpawn = "E124";

    /// <summary>Une annotation inconnue, mal placée ou mal écrite, ou un <c>@tap</c> sur un gear qui n'en veut pas.</summary>
    public const string InvalidAnnotation = "E125";

    /// <summary>Durée littérale qui ne tient pas dans 64 bits de millisecondes ou n'est pas un nombre entier de millisecondes (<c>1.0005s</c>).</summary>
    public const string InvalidDuration = "E126";

    /// <summary>Regex mal écrite : un motif que le moteur ne compile pas, un flag inconnu ou en double, une méthode mal appelée.</summary>
    public const string InvalidRegex = "E127";

    /// <summary>Un en-tête C d'un fichier <c>forge: native</c> que Clang ne lit pas : il a une erreur, ou la chaîne de compilation manque.</summary>
    public const string UnreadableHeader = "E144";

    /// <summary>Un symbole d'une bibliothèque C (fonction, struct, enum) que le code RGL nomme et que les liaisons n'ont pas converti (<c>rgl check</c>).</summary>
    public const string NativeSymbolUnconverted = "E019";

    /// <summary>Le cache de RGL prend au moins 2 % du disque : <c>rgl cache clear</c> le vide.</summary>
    public const string CacheLarge = "W067";

    /// <summary>Un composant écrit en C++ (<c>guard f(…): T;</c> et son fichier <c>.rgl.cpp</c>) mal déclaré : pas de fichier, pas un guard, un type ou une option qu'il ne prend pas encore en charge.</summary>
    public const string NativeComponent = "E145";

    /// <summary>Le C++ d'un fichier <c>.rgl.cpp</c> que Clang refuse : c'est une erreur du fichier de l'utilisateur, pas un bug du transpileur.</summary>
    public const string NativeCodeFailed = "E146";

    /// <summary><c>link: dynamic</c> dans un fichier <c>forge: native</c> : seule la liaison statique est disponible.</summary>
    public const string DynamicLinkUnavailable = "E020";

    /// <summary>Gear <c>pure</c> avec un paramètre <c>mut</c> : modifier l'appelant est un effet.</summary>
    public const string PureWithMutParameter = "E122";

    /// <summary><c>@fire</c> sur un gear dont le type de retour n'est pas <c>void</c> : personne ne lira son résultat.</summary>
    public const string FireReturnType = "E017";

    /// <summary><c>@async</c> ou <c>@fire</c> sur un gear <c>pure</c> : attendre est un effet.</summary>
    public const string AsyncPure = "E018";

    /// <summary><c>await</c> hors d'un gear <c>@async</c> : il n'y a rien à suspendre.</summary>
    public const string AwaitOutsideAsync = "E128";

    /// <summary>Une promesse jetée : l'appel d'un gear <c>@async</c> écrit comme instruction, sans <c>await</c> ni variable qui la garde.</summary>
    public const string PromiseDiscarded = "E129";

    /// <summary><c>await</c> ou <c>cancel</c> d'autre chose qu'une promesse.</summary>
    public const string AwaitNotPromise = "E130";

    /// <summary>Un type <c>Promise&lt;…&gt;</c> mal écrit, ou un type d'erreur de promesse qui n'a pas les champs d'<c>AsyncException</c>.</summary>
    public const string InvalidPromise = "E131";

    /// <summary>Un gear <c>@task</c> qui partage un état entre threads : un paramètre promesse ou flux de fichier, ou un appel qui attend, lance ou bloque sur la boucle.</summary>
    public const string TaskUnsafe = "E132";

    /// <summary>Un gear <c>@async</c> qui ne se suspend jamais : il aurait pu être un gear ordinaire.</summary>
    public const string AsyncNeverSuspends = "W053";

    /// <summary>Le résultat d'un <c>await</c> est ignoré : il peut être une erreur.</summary>
    public const string AwaitResultIgnored = "W063";

    /// <summary>Un paramètre <c>params</c> mal placé ou mal écrit : pas le dernier, pas un tableau <c>[T]</c>, avec <c>mut</c>, <c>this</c> ou une valeur par défaut, dans un train ou un opérateur.</summary>
    public const string InvalidParams = "E134";

    /// <summary>Dans un gear <c>@fire</c>, le résultat d'un <c>await</c> qui peut échouer est ignoré : personne ne lira l'erreur.</summary>
    public const string FireErrorIgnored = "E133";

    /// <summary>Une coroutine qui appelle un gear <c>@sync</c> : il bloque toute la boucle d'événements.</summary>
    public const string SyncInCoroutine = "W064";

    /// <summary>Le gear de fin d'un mold (<c>mold Handle -&gt; fermer</c>) n'existe pas, n'est pas un gear ordinaire sans échec, ou n'a pas la signature <c>gear fermer(Handle h): void</c>.</summary>
    public const string InvalidDestructor = "E135";

    /// <summary>Copie d'une valeur à cycle de vie : elle n'a qu'un propriétaire, elle se déplace (<c>b:: a</c>) ou se rend d'un calcul.</summary>
    public const string LifecycleCopy = "E136";

    /// <summary>Un type à cycle de vie là où il serait copié ou partagé : une constante, un tableau, un nullable, une union, un résultat, une lambda, une tâche.</summary>
    public const string LifecycleUnsupported = "E137";

    /// <summary>Un dataset invalide : son mold est inconnu ou ne se déduit pas, une entrée est en double ou porte un nom réservé, il n'a aucune entrée, ou un identifiant est inconnu.</summary>
    public const string InvalidDataset = "E138";

    /// <summary>Mode sûr : une erreur qu'un pas d'un train (ou d'un rail) peut envoyer et qu'aucun rail ne traite.</summary>
    public const string UnhandledError = "E139";

    /// <summary>Mode sûr : une reprise <c>^N&lt;M&gt;</c> dans un rail, qui s'épuiserait en continuant avec ce qui la suit sans rien dire.</summary>
    public const string SilentResumeInRail = "E140";

    /// <summary>Mode sûr : le résultat ignoré d'un <c>spawn</c> ou d'un <c>await</c> écrit comme instruction, qui peut être une erreur (un avertissement en mode libre).</summary>
    public const string IgnoredResult = "E141";

    /// <summary>Mode sûr : un handler de rail finit en succès sans avoir lu l'erreur qu'il reçoit ; il se tait en la lisant ou par la métadonnée <c>[ignoreError]</c> sur le handler.</summary>
    public const string ErrorIgnored = "W065";

    /// <summary>Boucles bornées (clé <c>loop</c> de <c>rgl.json</c> à <c>"safe"</c>) : une boucle de guard ou une reprise sans limite, ou un <c>goto</c> de switch, qui peut tourner sans fin.</summary>
    public const string UnboundedLoop = "E142";

    /// <summary>Mode sûr : un pas du train atteint un <c>@fire</c>, détaché : ses erreurs et son résultat n'arrivent à aucun rail.</summary>
    public const string FireInSafeTrain = "E143";

    /// <summary>Mode sûr : un rail fait échouer le train avec une erreur neuve sans avoir lu celle qu'il a reçue, qui disparaît.</summary>
    public const string ErrorLost = "W066";

    /// <summary>Mode sûr : le certificat d'un programme accepté, avec les trains vérifiés et les rails qui n'ont pas lu leur erreur.</summary>
    public const string SafeCertificate = "I002";

    /// <summary>Une boucle du programme est vectorisée par Clang (<c>--explain-vectorization</c>).</summary>
    public const string LoopVectorized = "I003";

    /// <summary>Une boucle du programme n'est pas vectorisée par Clang, avec la raison (<c>--explain-vectorization</c>).</summary>
    public const string LoopNotVectorized = "W069";

    /// <summary>Une optimisation du compilateur est appliquée (<c>--explain-optimizations</c>).</summary>
    public const string OptimizationApplied = "I004";

    /// <summary>Une optimisation du compilateur est écartée, avec la raison (<c>--explain-optimizations</c>).</summary>
    public const string OptimizationRefused = "W070";
}
