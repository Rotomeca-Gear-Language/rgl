using Rgl.Syntax;

namespace Rgl.Lsp;

/// <summary>Ce que le survol d'un mot montre : du Markdown, et le mot qu'il décrit.</summary>
/// <param name="Markdown">Le texte à afficher.</param>
/// <param name="Range">Le mot survolé.</param>
internal sealed record HoverResult(string Markdown, Rgl.Text.SourceSpan Range);

/// <summary>
/// Le survol, comme dans un vrai langage : la déclaration de ce qu'on survole (signature d'un gear, champs d'un mold, type d'une
/// variable, même déduit), le commentaire écrit juste avant elle, et le fichier qui la déclare. Un mot-clé ou un type primitif
/// est expliqué en une phrase.
/// </summary>
internal static class HoverProvider
{
    /// <summary>L'aide du mot à <paramref name="offset"/> ; <see langword="null"/> s'il n'y a rien à en dire.</summary>
    public static HoverResult? At(SemanticDocument document, int offset)
    {
        if (document.TargetAt(offset) is not { } target) return null;

        if (target.Symbol is { } symbol) return new HoverResult(Describe(document, symbol), target.Range);
        return KeywordDocumentation.For(target.Token.Text) is { } text ? new HoverResult(text, target.Range) : null;
    }

    /// <summary>La description Markdown d'une déclaration : sa signature, son commentaire, son origine.</summary>
    public static string Describe(SemanticDocument document, RglSymbol symbol)
    {
        var shown = symbol.Kind is RglSymbolKind.Variable or RglSymbolKind.Constant or RglSymbolKind.Parameter && symbol.TypeText is null
            ? symbol with { TypeText = document.TypeOfSymbol(symbol) }
            : symbol;

        var parts = new List<string> { "```rgl\n" + Signatures.Of(shown) + "\n```" };
        if (symbol.Deprecation is { } deprecation) parts.Add("**Obsolète**" + (deprecation.Length > 0 ? " : " + deprecation : "."));
        if (symbol.Node is Rgl.AST.Gears.GearDeclaration gear && gear.Notes?.Annotations.OfType<Rgl.AST.Notes.OtherAnnotation>().Any(a => a.Name.Name is "trusted" or "trust") == true)
            parts.Add(gear.IsPure ? "**@trusted · pure** : partie RGL vérifiée ; sûreté et pureté de la frontière native attestées par l’auteur." : "**@trusted** : contrat de sûreté de la frontière native attesté par l’auteur ; ce contrat ne rend pas la fonction pure.");
        if (symbol.Kind == RglSymbolKind.Module)
        {
            if (symbol.Module is { } module)
                parts.Add($"{(module.Kind is null ? "Fichier" : $"Fichier `forge: {module.Kind}`")} `{Path.GetFileName(module.Path)}`" + (symbol.Documentation is { } description ? $" — {description}" : ""));
        }
        else
        {
            if (symbol.Documentation is { } documentation) parts.Add(documentation);
            if (_Origin(document, symbol) is { } origin) parts.Add(origin);
        }
        return string.Join("\n\n", parts);
    }

    private static string? _Origin(SemanticDocument document, RglSymbol symbol) => symbol.Kind switch
    {
        RglSymbolKind.Variable => "*variable locale*",
        RglSymbolKind.Parameter => "*paramètre*",
        RglSymbolKind.TypeParameter => "*paramètre générique*",
        RglSymbolKind.Field or RglSymbolKind.Method when symbol.Container is { } container => $"*membre de* `{container}`",
        RglSymbolKind.Variant when symbol.Node is Rgl.AST.Datasets.DatasetEntry && symbol.Container is { } datasetName => $"*entrée du dataset* `{datasetName}`",
        RglSymbolKind.Variant when symbol.Container is { } container => $"*variante de* `{container}`",
        _ when symbol.Module is { } module && module != document.Self => $"*défini dans* `{Path.GetFileName(module.Path)}`",
        _ when symbol.Module is null && symbol.Kind == RglSymbolKind.Error => "*erreur standard*",
        _ => null,
    };
}

/// <summary>Une phrase pour chaque mot-clé et type primitif du langage.</summary>
internal static class KeywordDocumentation
{
    private static readonly Dictionary<string, string> _words = new(StringComparer.Ordinal)
    {
        ["before"] = "`@before(contrôle)` : avant le corps du gear, `contrôle` (une fonction qui reçoit les paramètres du gear et rend `R?`) est appelée ; une réponse non nulle est renvoyée à la place du corps, `null` laisse le gear s'exécuter.",
        ["tap"] = "`@tap(…)` : un effet de bord attaché à un gear (journal, trace), avant (`before`) ou après lui, sans changer sa valeur.",
        ["deprecated"] = "`@deprecated(\"message\")` : chaque appel du gear avertit (W061) avec ce message.",
        ["sync"] = "`@sync` : le gear attend lui-même les opérations asynchrones qu'il lance.",
        ["fire"] = "`@fire` : gear void lancé en coroutine détachée, sans attendre son résultat.",
        ["trusted"] = "Contrat explicite de confiance : interface sûre autour de cpp/unsafe ; pure reste vérifié en RGL et attesté pour la partie native. Soumis à no-trust et cpp-trust.",
        ["trust"] = "Alias de @trusted : contrat explicite de confiance pour une frontière native.",
        ["cpp"] = "Bloc C++ dans un gear unsafe ou @trusted : cpp { ... }(Type nom: valeur): Retour. Soumis à no-cpp et cpp-trust.",
        ["unsafe"] = "Déclare un gear autorisant les pointeurs et les appels natifs bruts. Interdit depuis un train safe ; les appels se propagent dans un contexte unsafe.",
        ["ptr"] = "`ptr<T> p: valeur;` alloue une valeur possédée, libérée à la destruction. Déplacement uniquement (`::` ou `<-|`), jamais de copie.",
        ["uptr"] = "`uptr<T>` : pointeur unsafe sans libération automatique. `.free()`, `delete` ou `undefine` libèrent une allocation ; W068 signale une libération potentiellement manquante.",
        ["char"] = "Caractère natif C++ sur 8 bits. Son signe dépend de la cible ; une conversion entière explicite est nécessaire.",
        ["delete"] = "Libère une allocation pointée et invalide la variable, dans un gear unsafe. Une adresse empruntée (`&x`) ne peut pas être libérée.",
        ["pack"] = "`pack(tableau)` prépare une représentation par éléments pour un appel natif. Le tableau source conserve sa représentation SoA.",
        ["descriptor"] = "Décrit une classe C++ et ses membres : constructeur (`create`), champs, méthodes et méthodes statiques. Les appels sont des guards ; l’objet possède sa ressource et se déplace.",
        ["gear"] = "Déclare un gear : une transformation, qui renvoie toujours une valeur du type annoncé (`void` pour aucune).",
        ["guard"] = "Déclare un guard : un gear qui peut échouer (`fail`), appelé comme étape d'un train, avec un rail pour son échec.",
        ["emit"] = "Déclare un emit : la dernière étape d'un train, qui produit sa sortie (ou son code de sortie).",
        ["pure"] = "Un gear `pure` n'a aucun effet de bord : ni entrée-sortie, ni appel d'un gear qui n'est pas `pure`.",
        ["train"] = "Un train : la suite d'étapes `|>` qui va de la valeur de départ à `done`, `fail`, `$ code` ou un emit.",
        ["dataset"] = "Déclare un dataset : une table dont chaque entrée a un nom, qui devient son identifiant typé (`Moves.id`). `of Move` lit les entrées comme un mold déclaré, `as Move` crée le mold depuis elles, `from \"moves.json\"` les lit d'un fichier ; `id.get()` rend l'entrée.",
        ["mold"] = "Déclare un mold : un type à champs (`mold Vec2 { float x; float y; }`). Les valeurs se typent par leur forme (duck typing).",
        ["error"] = "Déclare un type d'erreur : un mold qui reçoit d'office `message` et `where`.",
        ["enum"] = "Déclare une énumération : des variantes nommées, avec ou sans valeur.",
        ["ingot"] = "Déclare un type marqué : même représentation que sa base, mais un autre type (`ingot UserId: int;`).",
        ["var"] = "Déclare une variable dont le type est déduit de sa valeur.",
        ["const"] = "Déclare une constante, qui ne se réaffecte pas.",
        ["mut"] = "Rend le contenu modifiable (éléments, champs) ; devant un paramètre, la fonction modifie la variable de l'appelant.",
        ["undefine"] = "Libère une variable : elle ne peut plus être lue.",
        ["import"] = "Importe un fichier `.forge` du projet, par son nom : `import Fichier;` ou `import Fichier only {nom};`.",
        ["extend"] = "Ajoute des constantes et des gears à un type, pour le compilateur seulement (fichier `forge: gears`) : `extend Vec2 { gear pure norme(this Vec2 v): float => … }`. `extend<T: number> [T] { … }` est générique.",
        ["namespace"] = "Nom (`namespace Mon/Ns;`) qui départage les extensions de même nom sur un même type : `v.<Mon/Ns>f()`. Unique dans le projet.",
        ["export"] = "Rend un nom visible de ceux qui importent le fichier : `export gear f() { … }` ou `export {a, b};`.",
        ["this"] = "Dans une extension, marque le premier paramètre : le gear s'appelle sur une valeur, `v.f(a)` valant `f(v, a)`.",
        ["only"] = "Choisit les noms importés d'un fichier : `import Fichier only {a, b as c};`.",
        ["if"] = "Exécute un bloc si la condition (un `bool`) est vraie.",
        ["else"] = "Branche prise quand la condition du `if` est fausse.",
        ["switch"] = "Choisit un cas selon une valeur ; chaque cas est un bloc, `break` en sort.",
        ["case"] = "Un cas d'un `switch`, par une valeur connue à la compilation (`case eval` pour une condition).",
        ["default"] = "Le cas pris quand aucun autre ne convient.",
        ["break"] = "Sort du `switch` (`break N` de N niveaux, `break all` de tous).",
        ["goto"] = "Saute au cas d'un `switch` qui vaut la valeur donnée.",
        ["match"] = "Choisit selon le type ou le résultat d'une valeur : `match (v) { int i |> …; ok x |> …; err e |> …; }`, ou, comme un switch qui donne une valeur, selon sa valeur : `match (n) { case 1, 2 |> 'petit'; case eval $ > 10 |> 'grand'; default |> 'autre'; }`.",
        ["return"] = "Renvoie la valeur d'un gear.",
        ["fail"] = "Fait échouer le guard ou le train, avec un texte ou une erreur.",
        ["panic"] = "Arrête le programme sur-le-champ (code 101) : le message et la position partent sur stderr. Permis partout, même dans un gear pure.",
        ["await"] = "Attend une promesse et donne son résultat (`T ! E`, ou `E?` sans valeur). Seulement dans un gear `@async` ; dans un `@sync`, il bloque.",
        ["cancel"] = "`cancel p;` demande l'annulation de la tâche de la promesse : elle s'arrête à son prochain `await` (tout de suite si elle attend), et ce qu'elle attendait est annulé aussi.",
        ["task"] = "`task f(x)` exécute un gear ordinaire sur le pool de threads, avec des copies de ses arguments, et rend une promesse de son résultat.",
        ["sleep"] = "`sleep(durée)` : une promesse qui se termine une fois la durée écoulée, sans bloquer le thread : `await sleep(500ms);`.",
        ["whenAny"] = "`whenAny([a, b])` : la promesse du résultat de la première promesse du tableau qui finit ; les autres continuent.",
        ["parseJson"] = "`parseJson<T>(texte)` : lit un texte JSON comme le format `json` d'un fichier ; rend `T ! ParseError` (le chemin de la valeur fautive est dans `input`).",
        ["toJson"] = "`toJson(valeur)` : écrit une valeur en JSON indenté ; rend `string ! ParseError` (un flottant `NaN` ou infini est refusé).",
        ["readBytesAsync"] = "`readBytesAsync(chemin)` : une promesse des octets du fichier, lus sur le pool de threads.",
        ["spawnAsyncWith"] = "`spawnAsyncWith('options', exécutable, arguments…)` : comme `spawnAsync`, avec les options littérales de `spawn` (`timeout=`, `cwd=`, `env=`…).",
        ["readAsync"] = "`readAsync(chemin)` : une promesse du texte du fichier, lu sur le pool de threads.",
        ["writeAsync"] = "`writeAsync(chemin, texte)` : une promesse sans valeur, l'écriture se fait sur le pool de threads.",
        ["spawnAsync"] = "`spawnAsync(exécutable, arguments…)` : une promesse du `ProcessResult` d'un processus enfant, lancé sur le pool de threads.",
        ["with"] = "p with {pos.x: 3, hp: p.hp - 1} : une copie de p dont certaines parties changent.",
        ["done"] = "Termine le train avec succès (ou le guard, en laissant passer sa valeur).",
        ["new"] = "Crée une valeur de mold : `new Vec2 {x: 1.0, y: 2.0}`.",
        ["as"] = "Convertit une valeur : `as` (contrôlé), `as?` (null en cas d'échec), `as!` (échoue dans un guard).",
        ["is"] = "Dans un corps générique, `T is X` teste que le type est exactement X (connu à la compilation).",
        ["accept"] = "Dans un corps générique, `T accept X` teste que le type a au moins les champs de X.",
        ["and"] = "ET logique, évalué de gauche à droite.",
        ["or"] = "OU logique, évalué de gauche à droite.",
        ["xor"] = "OU exclusif.",
        ["not"] = "Négation logique ; `a not eq b` se lit `not (a eq b)`.",
        ["eq"] = "Égalité de deux valeurs, champ par champ pour un mold.",
        ["invert"] = "Négation d'un `bool`, ou complément bit à bit d'un entier.",
        ["increment"] = "Ajoute 1 (ou `increment<N>`) à une variable et renvoie le nouveau résultat.",
        ["decrement"] = "Retire 1 (ou `decrement<N>`) à une variable et renvoie le nouveau résultat.",
        ["true"] = "La valeur vraie d'un `bool`.",
        ["false"] = "La valeur fausse d'un `bool`.",
        ["null"] = "L'absence de valeur d'un type nullable `T?`.",
        ["ok"] = "Construit ou lit le succès d'un résultat `T ! E`.",
        ["err"] = "Construit ou lit l'échec d'un résultat `T ! E`.",
        ["console"] = "L'entrée-sortie standard : `valeur -> console;` écrit, `var l <- console;` lit une ligne.",
        ["raw"] = "Un fichier entier : `var t <- raw \"a.txt\";` le lit en `string` (UTF-8 strict) ou en `bytes` si le type est écrit, `t -> raw \"a.txt\";` l'écrit ; dans un guard, un échec envoie une `IoError`.",
        ["forge"] = "En-tête d'un fichier `.forge` : son genre (`train`, `mold`, `gears`, `index`).",
        ["description"] = "En-tête d'un fichier `.forge` : sa description, montrée par l'éditeur.",
        ["with"] = "Introduit l'étape d'une accumulation : `valeurs |+> départ with étape`.",
        ["by"] = "Le pas d'une plage : `[0..10 by 2]`.",
        ["eval"] = "`case eval` : un cas dont la condition (avec `$`, la valeur du switch) est calculée.",
        ["all"] = "`break all` sort de tous les `switch` imbriqués.",
        ["int"] = "Entier signé de 32 bits.",
        ["uint"] = "Entier non signé de 32 bits ; le type du code de sortie.",
        ["byte"] = "Entier signé de 8 bits.",
        ["ubyte"] = "Entier non signé de 8 bits.",
        ["short"] = "Entier signé de 16 bits.",
        ["ushort"] = "Entier non signé de 16 bits.",
        ["long"] = "Entier signé de 64 bits.",
        ["ulong"] = "Entier non signé de 64 bits.",
        ["float"] = "Nombre à virgule flottante de 32 bits.",
        ["double"] = "Nombre à virgule flottante de 64 bits (le type des littéraux décimaux).",
        ["bool"] = "Booléen : `true` ou `false`.",
        ["string"] = "Texte immuable en UTF-8 (jusqu'à 22 octets sans allocation).",
        ["mstring"] = "Texte modifiable.",
        ["symbol"] = "Un nom unique comparé par identité : `§nom`.",
        ["duration"] = "Une durée en millisecondes : `100ms`, `1.5s`, `2min`, `1h`.",
        ["datetime"] = "Un instant en UTC, en millisecondes depuis 1970 : `date + 2h`, `b - a` (une durée). `<- clock` donne l'instant présent.",
        ["regex"] = "Une expression régulière, écrite en littéral : `/motif/flags` (g, i, m). Méthodes : `test`, `match`, `find`, `replace`.",
        ["void"] = "Aucune valeur : le type de retour d'un gear qui ne renvoie rien.",
    };

    /// <summary>Une phrase sur ce mot ; <see langword="null"/> si le mot n'est pas un mot-clé connu.</summary>
    public static string? For(string word) => _words.TryGetValue(word, out var text) ? $"**{word}** — {text}" : null;
}
