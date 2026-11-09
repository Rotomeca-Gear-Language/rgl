# RGL — Blocs de base (v0 simplifiée)

Oct 13, 2026 · @Tommy Delphin

## Périmètre

Ce document décrit comment s'écrit le RGL : les variables (1 à 6), le contrôle de flux (7), les valeurs et les types (8 et 9), les gears, trains et données (10 à 28), les liaisons et méta-déclarations (29 à 33), les opérateurs (34), la ligne de commande (35), l'asynchronisme (36), puis l'intégration C++ (37). Toute variable est initialisée à sa déclaration ; une variable n'a jamais de valeur par défaut (seuls les champs de moule peuvent en déclarer une, §20). Le langage est fortement typé : chaque variable, paramètre et retour a un type connu à la compilation. Hors périmètre : `frozen`, trains typés `guard` / `emit`, contrats, macros, RML, paquets.

Ce document remplace, pour ce périmètre, la spec v015 : `var` remplace `let` (comme en C#), une variable typée se déclare sans mot-clé (`int x: 5;`, `declare` n'existe plus) et toujours avec une valeur.

**État d'avancement.** Cette spec décrit le langage visé et reflète ce que le compilateur fait aujourd'hui : variables, types, contrôle de flux, gears, guards, emits, trains (nommés, appelables), rails, boucles de guard, points de reprise, parcours, accumulation, SoA, moules (génériques, opérateurs et conversions), énumérations, ingots, unions, casts, collections, lambdas, mutabilité, imports (fichiers, boxes, tools), exports, génériques, extensions et la ligne de commande avec son serveur de langage. Ce qui n'est **pas encore implémenté** est signalé comme tel dans sa section ; le compilateur le refuse alors par « pas encore pris en charge ». À ce jour :

- types `bytes`, `char` et `ptr`, tableaux `[[T;N]]` (AoSoA), littéraux de durée (`100ms`) et d'expression régulière (`/motif/`), déconstruction de tuple ;
- `-> last & {…}` ; un `guard` ou un `emit` générique, un opérateur ou une conversion dans un moule générique, un gear générique dans une extension ;
- `°<-` sur un paramètre `mut`, sur un champ ou sur un élément ;
- precasts (§21), formats d'entrée et de sortie autres que `console` (§27), `import file`, `import json`, `import xml` ;
- processus enfant (§28), annotations et métadonnées `[]` (§29), `unsafe` (§30), liaison native (§31, §32, §37, `forge: native`, `rgl check`), blueprint (§33), asynchronisme (§36).

## Fichiers et en-têtes — `forge:`

Un projet a **un seul fichier `.rgl`** : son point d'entrée, qui contient le train d'entrée. Tous les autres fichiers source portent l'extension **`.forge`**.

Chaque fichier `.forge` commence par un en-tête ; son absence est une erreur (**E016**) :

```rgl
forge: mold
description: "Types géométriques de base"
metadata: {"auteur": "Tommy", "version": 2}
```

- `forge:` donne le genre du fichier (tableau ci-dessous).
- `description:` documente le fichier ; son absence avertit (**W005**).
- `metadata:` est un objet JSON libre, facultatif, lu par les outils et ignoré par le compilateur. Il décrit le fichier, alors que les métadonnées `[…]` (§29) décrivent une déclaration.

| Genre | Contenu | Importable |
| --- | --- | --- |
| `train` | Des trains nommés puis un train par défaut, avec leurs gears, moules et constantes communs ou locaux (§13) | Chaque train ; le train par défaut sous le nom du fichier |
| `mold` | Des moules, enums et ingots | Ce qu'il exporte (tout, sans `export` : §26) |
| `precast` | Des precasts (§21) | Chaque precast |
| `gears` | Une bibliothèque de gears, guards et emits, avec des moules, enums, ingots, constantes et extensions (§26) | Ce qu'il exporte (tout, sans `export`) |
| `index` | L'index d'une box (§26) | Ce qu'il réexporte |
| `native` | La description d'une bibliothèque C ou C++ (§37) | Les symboles importés de ses en-têtes |

Le fichier `.rgl` d'entrée n'a **pas d'en-tête** (**E105** s'il en a un) : il est implicitement de genre `train` et ne contient qu'**un seul train**, le train d'entrée (pas de train nommé). Un genre inconnu est **E106** ; une déclaration que le genre du fichier n'accepte pas est **E104** (un train nommé hors d'un `forge: train`, une extension hors d'un `forge: gears`, un `export` dans le `.rgl`…).

## Règles mémoire et mutabilité

- **Passage par référence.** Une valeur passée d'un gear à un autre (paramètre ou étape de pipeline) l'est par référence, sans copie. Le gear ne peut pas la modifier, sauf par un paramètre `mut` ; le C++ émis utilise des références `const`, et une référence modifiable pour un paramètre `mut`.
- **Toute affectation copie.** Deux variables ne partagent jamais un contenu : `var b: a;` donne à `b` sa propre copie, que modifier ne change pas `a` (modèle à la Swift, sans alias). Quand le compilateur prouve que la source n'est plus lue ensuite, il la déplace au lieu de la copier (`std::move`) : c'est invisible, seulement plus rapide.
- **Réaffectation libre.** Une variable déclarée avec `var` ou avec son type peut toujours être réassignée (§4) avec une nouvelle valeur entière ; une constante jamais.
- **Ajout.** On ajoute des éléments à la fin d'un tableau (`push`) et des clés à un dictionnaire (`add`) déclarés dans le gear, sans `mut`. Un tableau reçu en paramètre ne grandit que si le paramètre est `mut` (**E014** sinon).

### Mutabilité — `mut`

`mut` rend modifiable en place le **contenu** d'une variable : les éléments d'un tableau, les champs d'un moule, les valeurs d'un dictionnaire, en profondeur (`a[i] <- v`, `a[i].champ <- v`, `pos.x <- v`, `d[k] <- v`, et les affectations composées `+<-`…). Sans `mut`, une modification en place est **E013**. `mut` s'écrit en tête de la déclaration, devant `var` ou le type, et seulement sur un tableau, un moule ou un dictionnaire (**E101** sur un autre type ou sur une constante, qui n'est jamais `mut`). Les chaînes restent immuables (`mstring` pour un texte modifiable) ; une lambda capture par copie et ne modifie donc jamais une variable extérieure (**E005**).

```rgl
Vec2 pos: {x: 1.0, y: 2.0};
pos.x <- 3.0;                        // E013 — pos n'est pas mut
pos <- {...pos, x: 3.0};             // OK — nouvelle valeur

mut var nums: [1, 2, 3];
nums[0] <- 9;                        // OK — modification d'un élément
nums[1] +<- 1;

mut [string:int] ages: ["Alix": 12];
ages["Alix"] <- 13;                  // clé absente : arrêt du programme, ou échec du guard

mut int n: 3;                        // E101 — seulement tableaux, moules et dictionnaires
```

Un index hors limites ou une clé absente dans la cible arrête le programme avec la position RGL ; dans un guard, il fait échouer le guard avec une `Exception`. Les index de la cible sont évalués une fois, de gauche à droite, avant la valeur.

**Paramètre `mut`.** Un gear dont un paramètre est `mut` modifie la variable de l'appelant (son contenu, `push`, ou une nouvelle valeur entière) ; un tel gear est toujours synchrone. Rien ne s'écrit à l'appel : l'outillage affiche `mut`, grisé, devant l'argument. L'argument est une variable (ou un paramètre) `mut` du type exact du paramètre : ni constante, ni paramètre ordinaire, ni valeur calculée, et jamais la même variable deux fois en `mut` dans un même appel (**E102**). Dans un train, la valeur qui circule n'appartient qu'au train : une étape (un guard bouclé `*` compris) la reçoit dans son paramètre `mut` et la modifie sur place, sans copie (`train … |> frame*<100> |> report;`). Un gear à paramètre `mut` ne se passe pas comme une lambda, et un paramètre du train n'est jamais `mut` (**E101**).

```rgl
gear vieillir(mut Particule p): void {
    p.vie -<- 1;
}

gear naitre(mut [Particule] ps, float x): void => ps.push({x, vie: 3});

mut [Particule] ps: [];
naitre(ps, 0.0);                     // affiché naitre(mut ps, 0.0) par l'éditeur
naitre([], 0.0);                     // E102 — une valeur calculée
```

**Parcours qui modifie sur place.** `ps ||> (mut p) => { p.vie -<- 1; }` ou `ps ||> vieillir` (un gear dont le premier paramètre est `mut`) modifie chaque élément dans le tableau lui-même, sans en construire un nouveau. La source est une variable (ou un paramètre) `mut`, ou un tableau dans son contenu, et seule la première étape modifie (**E102** sinon) ; un paramètre de lambda n'est `mut` qu'à cette étape (**E101** ailleurs). Une étape qui modifie et ne renvoie rien termine le parcours, qui ne renvoie rien.

## 1. Déclaration typée — `Type nom`

Crée une variable dont le type est fixé explicitement et ne change plus. Elle s'écrit sans mot-clé : le type, puis le nom. La valeur initiale est obligatoire.

```rgl
int maVariable: 0;      // OK
string nom: "RGL";      // OK
(int | string)? valeur: null;

int autre;              // E006 — pas de valeur par défaut
```

Forme : `[mut] <Type> <nom>: <expression>;`

## 2. Déclaration inférée — `var`

Crée une variable mutable dont le type est déduit de la valeur initiale, puis figé. Un littéral entier donne toujours `int`. Si le compilateur ne peut pas déduire de type, il lève **E001**.

```rgl
var compteur: 0;        // int
var nom: "RGL";         // string

var inconnu: null;      // E001 — inférence impossible
var vide;               // E006 — pas de valeur
```

Forme : `[mut] var <nom>: <expression>;`

## 3. Constante — `const`

Crée une variable typée qui ne peut plus être réassignée. Toute assignation ultérieure lève **E005**.

```rgl
const int MAX: 5;

MAX <- 6;               // E005 — assignation à une constante
```

Forme : `const <Type> <nom>: <expression>;`

**Génération C++.** Une constante est toujours émise `const` ; elle devient `constexpr` quand sa valeur est entièrement connue à la compilation.

| Valeur | C++ émis |
| --- | --- |
| Primitif, `string`, moule constexpr, `[T;N]` littéral | `constexpr T` |
| `mstring`, moule avec un champ non constexpr, `[T]`, valeur calculée à l'exécution | `const T` |

Un moule est constexpr seulement si tous ses champs le sont, récursivement : un seul champ `mstring` ou `[T]` suffit à l'en empêcher.

## 4. Assignation — `<-`

Remplace la valeur d'une variable existante. La cible doit exister (**E002**) et la valeur doit avoir le type de la cible (**E003**).

```rgl
int score: 0;
score <- 5;              // OK

inexistant <- 5;         // E002 — la variable n'existe pas
score <- "cinq";         // E003 — string vers int
```

Forme : `<nom> <- <expression>;`

**Déplacement — `::` et `<-|`.** `Type nom:: valeur;` (ou `var nom:: valeur;`) et `nom <-| valeur;` (aussi sur un champ ou un élément d'une variable `mut`, ou un paramètre `mut`) font comme `:` et `<-`, mais **déplacent** la valeur au lieu de la copier quand c'est possible. Une valeur calculée (`fromGear()`) est toujours déplacée. Une variable locale, ni constante ni capturée par une lambda, dont la valeur n'a pas à être convertie, est déplacée puis **libérée**, comme par `undefine` : la relire est **E006**, la redéclarer **W001**. Sinon (constante, paramètre, variable capturée, conversion), la valeur est copiée et le compilateur le signale (**W014**).

```rgl
MyMold a:: fromGear();   // la valeur rendue est déplacée dans a
var b:: a;               // a est déplacée dans b, puis libérée
var c: a;                // E006 — a a été libérée par le déplacement
liste <-| autre;         // autre est déplacée dans liste, puis libérée
```

## 5. Assignation avec rappel de type — `nom: T <-`

Même effet que `<-`, avec le type de la cible rappelé après son nom pour la lisibilité. Ce n'est pas un cast : le compilateur vérifie seulement que l'indice correspond au type déclaré (**E004**), puis applique les règles de `<-`.

```rgl
int score: 0;
score: int <- 5;         // OK

score: float <- 5;       // E004 — score est un int
```

Forme : `<nom>: <Type> <- <expression>;`

Aucune confusion avec une déclaration : une déclaration commence par un mot-clé (`var`, `const`, `mut`) ou par un type suivi du nom (`int score: 5;`), une assignation par le nom.

## 6. Portée — `{ }` et `undefine`

Une variable vit jusqu'à l'accolade fermante de son bloc, puis est détruite (RAII). `undefine` la libère plus tôt ; le nom peut alors être redéclaré (**W001**).

```rgl
var a: 42;
{
    var b: a + 1;        // b visible ici seulement
}
// b n'existe plus

undefine a;
var a: 20;               // W001 — redéclaration d'un nom libéré
```

## 7. Contrôle de flux — `if`, `switch`, ternaire

`if` / `else` et `switch` s'écrivent dans un corps de gear, guard ou emit. Il n'y a aucune boucle impérative (`for`, `while`, `do`, `continue` sont réservés et interdits) : on itère avec `||>`, `|!|>`, l'accumulation `|+>`, un intervalle (`[0..<n] ||> f`) ou la boucle de guard `*`.

Dans un `switch` :

- `case v:` teste une valeur ; plusieurs valeurs s'écrivent `case v3, v4:` ou en empilant les `case`.
- `case eval <condition>:` teste une condition où `$` désigne la valeur du `switch`.
- `default:` attrape le reste.
- Un cas sans `break` continue dans le cas suivant, comme en C. Si ce cas contient des instructions, le compilateur avertit (**W003**) ; des `case` vides empilés ne déclenchent rien.
- `break` sort du `switch` ; `break 1` sort aussi du `switch` englobant ; `break all` sort de tous les `switch` imbriqués. Un `break N` plus profond que l'imbrication réelle lève **W002**.
- `goto v` saute au cas `v`. Un cas inexistant lève **E007** ; viser un `case eval` lève **E008**.

```rgl
if (n < 0) {
    fail "négatif";
} else if (n eq 0) {
    return 0;
} else {
    return n * 2;
}

switch (item) {
    case 1:
        break;
    case 2:
        goto 1;
    case 3, 4:
        break;
    case eval $ > 10:
        break;
    default:
        break;
}

var signe: n < 0 ? -1 : 1;     // ternaire
```

## 8. Lexique

Sources en UTF-8 ; une marque BOM en début de fichier est tolérée et ignorée. Un fichier vide est légal mais avertit (**W006**). Identificateurs : `[a-zA-Z_][a-zA-Z0-9_]*`, sensibles à la casse, 255 caractères max. Un nom de type primitif (`int`, `double`, `string`…) ne peut pas servir d'identificateur (**E011**). Les commentaires de bloc ne s'imbriquent pas.

```rgl
// commentaire de ligne
/* commentaire de bloc */

var dec: 1_000;          // entier décimal
var hex: 0xFF;           // aussi 0b1010, 0o17
var f: 1.5e3;            // flottant — écrire 1.0, jamais 1.
var ok: true;            // true, false, null sont des littéraux
var delai: 100ms;        // durées : ms, s, min, h

var brut: 'C:\chemin';   // chaîne brute, '' pour une apostrophe
var classique: "a\n";    // échappements C++, \u{XXXX}
var tmpl: `x = {x}`;     // template évalué à l'exécution
var texte: m"modifiable";  // préfixe m → mstring (mutable)
```

Toutes les chaînes sans `m` sont des `string` immuables.

Deux littéraux supplémentaires : le symbole `§nom` produit un `symbol` interné, comparé par identité sans allocation ; `/motif/flags` produit une regex native.

```rgl
var etat: §pret;
var email: /^[a-z]+@[a-z]+\.[a-z]+$/i;
```

**Mots-clés.** Un mot-clé ne peut jamais servir d'identificateur.

| Catégorie | Mots |
| --- | --- |
| Déclarations | `gear` `pure` `guard` `emit` `unsafe` `train` `mold` `soa` `precast` `ingot` `enum` `dyn_enum` `dyn` `wrapper` `descriptor` `blueprint` `var` `mut` `const` `undefine` `import` |
| Contrôle | `if` `else` `switch` `case` `default` `break` `goto` `match` `return` `fail` `spawn` |
| Opérateurs en mots | `and` `or` `xor` `not` `is` `as` `new` `delete` `invert` `increment` `decrement` `post_increment` `post_decrement` `bitand` `bitor` `bitxor` `shl` `shr` |
| Littéraux | `true` `false` `null` |
| Types primitifs (**E011**) | `byte` `ubyte` `short` `ushort` `int` `uint` `long` `ulong` `float` `double` `bool` `string` `mstring` `symbol` `bytes` `void` `char` `ptr` |

**Mots-clés contextuels**, réservés uniquement dans leur contexte :

| Mot | Contexte |
| --- | --- |
| `forge` `description` `metadata` | En-tête de fichier |
| `done` | Corps d'un guard |
| `last` | Forme `-> last & {…}` d'un guard |
| `accept` | Test structurel dans un corps générique |
| `eval` `all` | `case eval` et `break all` d'un `switch` |
| `implicit` `explicit` | Conversions déclarées dans un moule |
| `console` `file` `raw` `json` `xml` `custom` | Formats d'entrée / sortie et d'import |
| `box` `tools` | Genres d'import (les seuls imports avec un mot-clé) |
| `gears` `native` | Genres d'en-tête `forge:` |
| `header` `link` `library` | En-tête d'un fichier `forge: native` |
| `field` `static` `constructor` | Entrées d'un wrapper ou d'un descriptor |
| `before` `after` `closure` | Paramètres de `@tap` |

**Réservés et interdits** : ces mots ne peuvent pas être utilisés.

- Héritage C++ : `auto` `class` `friend` `interface` `namespace` `override` `private` `protected` `public` `struct` `template` `typedef` `virtual`.
- Exceptions, remplacées par guard et `fail` : `try` `catch` `throw`.
- Boucles impératives : `for` `while` `do` `continue`.
- Évolution future : `async` `await` `fn` `func` `function` `yield` `contract` `let` `frozen`.
- Interop web : `undefined`.
- Symboles : `++` et `--`.

## 9. Types

Primitifs : `byte` `ubyte` (8 bits), `short` `ushort` (16), `int` `uint` (32), `long` `ulong` (64), `float` `double`, `bool`, `string`, `mstring`, `symbol`, `bytes`, `void`. `char` n'existe que dans un bloc `unsafe`.

`T?` rend un type nullable ; `null` n'est assignable qu'à un `T?`. Un `T?` ne s'utilise comme un `T` que là où le compilateur sait qu'il n'est pas `null` : après `if (x not eq null)` dans le bloc, après `if (x eq null) { … }` qui sort (`return`, `fail`, `break`…) dans la suite, à droite d'un `and`, dans la branche d'un ternaire ; une affectation de la variable fait oublier ce test. Ailleurs, on écrit `x ?? valeur` ou `x?.champ`.

| Écriture | Sens |
| --- | --- |
| `[T]` | Tableau dynamique |
| `[T;N]` | Tableau de taille fixe |
| `[T;N..M]` | Tableau borné (N à M éléments) |
| `[T+]` | Tableau non vide |
| `[[T;N]]` | Tableau de blocs de N éléments, stocké en AoSoA (§19) |
| `[K:V]` | Dictionnaire |
| `(T, U)` | Tuple |
| `T \| U` | Union (résolue par `match`) |
| `T ! E` | Résultat : valeur `T` ou erreur `E` |
| `{T a, U b}` | Moule anonyme (type) ; la valeur s'écrit `{a: …, b: …}` |
| `int<0..100>` | Entier à plage restreinte |

```rgl
int? peutEtre: null;
[int] nums: [1, 2, 3];
int<0..100> pct: 50;
int | string val: 42;
```

**Collections.** Un tableau s'écrit `[1, 2, 3]`, vide `[]` (son type vient alors du contexte, E001 sinon), et `[...a, x, ...b]` en recopie d'autres (spread). Il prend le type attendu par le contexte, élément par élément ; sans contexte, ses éléments ont un type commun.

| Opération | Sens |
| --- | --- |
| `a[i]` | Lecture d'un élément (index entier, à partir de 0) |
| `a.len` | Nombre d'éléments (`uint`) |
| `a.push(v)` | Ajout à la fin d'un `[T]` ou `[T;N..M]` : une variable déclarée dans le gear, un paramètre `mut`, ou un tableau dans le contenu d'une variable `mut` (E014 sur un paramètre ordinaire, E005 sur une constante, E013 dans le contenu d'une variable qui n'est pas `mut`, E040 sur un `[T;N]` ou un `[T+]`) |
| `d[k]` | Lecture d'une valeur d'un dictionnaire |
| `d.has(k)` | La clé est-elle présente (`bool`) |
| `d.add(k, v)` | Ajout d'une clé absente (mêmes règles que `push`) |
| `t.x`, `t.Item1` | Élément d'un tuple, par son nom ou sa position (à partir de 1), comme en C# |

- **Modification d'élément** : `a[0] <- 9;` et `d[k] <- v` demandent une variable `mut` (E013 sinon), comme un champ de moule.
- **Hors limites** : un index connu à la compilation est vérifié par le compilateur ; sinon, un index hors limites, une clé absente, un ajout dans un tableau borné plein ou une clé ajoutée deux fois arrête le programme avec la position RGL, et, dans un guard, fait échouer le guard avec une `Exception`.
- **Tailles** : `[T;N]` a exactement N éléments (N ≥ 1), `[T;N..M]` entre N et M (N ≤ M, M ≥ 1), `[T+]` au moins un (E092 sinon). Un tableau écrit dont le nombre d'éléments est connu est vérifié à la compilation (E086) ; d'une sorte de tableau à une autre (mêmes éléments), la conversion vers `[T]`, ou vers une sorte qui contient toutes les tailles de la source, est libre, les autres sont contrôlées à l'exécution comme un type borné. `as` contrôle (arrêt), `as?` donne `null` si la taille ne convient pas, `as!` fait échouer le guard (`CastError`).
- **Dictionnaires** : `["Zoé": 7, "Alix": 12]`, vide `[:]`. Une clé est un entier, un `bool`, un `string`, un `symbol`, un enum ou un ingot de l'un d'eux (E093 sinon) ; deux clés connues égales dans un même dictionnaire écrit sont E094. Les clés sont rangées (`std::flat_map`) : le texte d'un dictionnaire les donne dans l'ordre.
- **Tuples** : `(1, "un")`, ou nommés `(x: 1, y: "un")` ; les noms ne comptent pas dans l'identité du type (`(int x, string y)` et `(int, string)` sont le même type). La déconstruction viendra plus tard.
- **Texte** : un tableau s'écrit `[1, "a"]`, un dictionnaire `["a": 1]` (`[:]` vide), un tuple `(1, "a")`.
- **Égalité** : `eq` compare deux tableaux, dictionnaires ou tuples élément par élément ; un `[T;3]` se compare à un `[T]`.
- Un `[T;N]` dont tous les éléments sont connus est `constexpr`.

**Chaînes.** `string` et `mstring` sont deux classes C++ du runtime (`rgl::standard::primitives`), indépendantes de `std::allocator`, avec une optimisation des petites chaînes jusqu'à 22 caractères (stockées sans allocation).

| Type | Fonctionnement |
| --- | --- |
| `string` | Trois modes internes : petite chaîne inline, bloc sur le tas avec compteur de références atomique (copie sans allocation), ou vue sur un littéral connu à la compilation |
| `mstring` | Mutable, jamais constexpr ; sa capacité croît par facteur 1,5 (32 caractères minimum). `to_immutable()` en produit une `string` |

Les deux acceptent un allocateur ABI-stable (trois fonctions : allouer, réallouer, libérer), pour les allocateurs personnalisés et la stabilité entre modules.

## 10. Gear — `gear`

Unité de transformation, locale à son fichier, sauf dans une bibliothèque de gears (`forge: gears`, §26) où elle est publique. Un gear déclare toujours son type de retour et doit `return` sur tous les chemins ; `void` peut s'en passer. `pure` garantit l'absence d'effet de bord : ni I/O, ni appel d'un train qui a un effet (la pureté d'un train est déduite), ni état externe modifié. Le compilateur peut s'en servir pour optimiser, mais n'ajoute aucun cache.

```rgl
gear twice(int n): int {
    var x: n * 2;
    return x;
}
gear pure add(int a, int b): int => a + b;   // corps expression
gear identity<T>(T val): T => val;           // générique
```

Les paramètres génériques acceptent des contraintes, et un générique peut porter une valeur au lieu d'un type :

| Forme | Exigence |
| --- | --- |
| `<T>` | Aucune |
| `<T:number>` | Type numérique primitif |
| `<T:{TYPE f}>` | Au moins le champ `f` de type `TYPE` |
| `<T:error>` | Type d'erreur (valeur d'un `fail`) |
| `<int max>` | Valeur connue à la compilation |

Dans le corps, `T is MyMold` teste le type exact et `T accept Vec2` teste qu'il a au moins les champs de `Vec2`.

```rgl
gear log(string msg): void { msg -> console; }
gear clamp<T:number>(T v, T lo, T hi): T { ... }
gear Max<int max>(int v): int => v > max ? max : v;
```

**Lambdas.** Une fonction anonyme s'écrit `(paramètres) => expression` ou `(paramètres) => { … }`. Les variables extérieures utilisées dans le corps sont **capturées par copie** : la lambda garde la valeur qu'elles avaient à sa création, même si elle survit au bloc qui l'a créée.

```rgl
var seuil: 10;
var filtre: (int v) => v > seuil;          // seuil copié (10)
var log: (string m) => { m -> console; };
```

- **Type** : `(int, string) => bool`, `() => void`, `pure (int) => int`. Il s'écrit partout où un type s'écrit (paramètre, déclaration typée, champ de moule, `[(int) => int]`, `((int) => bool)?`). Le type de retour d'un type de lambda va jusqu'au bout : `(int) => int | string` renvoie une union ; `((int) => int) | string` est une union qui contient une lambda.
- **Paramètres** : `(int v) => …`, ou `(v) => …` quand le contexte donne le type (déclaration typée, argument d'un gear, champ de moule, élément de tableau ; E001 sinon). Un paramètre ne masque pas un nom visible (E044).
- **Retour** : celui du type attendu, sinon celui du corps expression, ou du premier `return` d'un corps bloc (`void` sans `return` de valeur). Après `=>`, une `{` ouvre toujours un bloc : une lambda qui renvoie un moule écrit `=> ({x: 1})`.
- **Captures** : par copie, à la création. Une variable capturée ne se modifie pas dans la lambda (E005) ; ses propres variables, si.
- **Appel** : `f(3)`, `ops[0](3)`, `fabrique(2)(5)`, `pos.action(3)`, `v |> f`. Un gear se passe par son nom (`garder(nums, estPair)`) et a le type de sa signature ; un guard ou un emit, non (E062).
- **Pureté** : déduite du corps (pure s'il n'appelle que du pur et ne fait aucune entrée ni sortie). Une lambda pure entre dans le type sans `pure`, pas l'inverse ; quand le type attendu est `pure`, le corps est vérifié comme celui d'un gear pure (E057, E066). Un gear pure n'appelle que des lambdas de type `pure`.
- **Dans un guard** : une lambda n'est pas un guard (`fail`, `done`, `as!` : E060). Ce qui échouerait dans le guard (index hors limites…) arrête le programme dans une lambda.
- **Égalité, texte** : deux lambdas ne se comparent pas et ne s'écrivent pas en texte (E040), ni un moule ou un tableau qui en contient ; `f eq null` sur un `((…) => T)?` reste permis.
- **Type de retour d'un gear** : `gear f(): (int) => int { … }` renvoie une lambda ; avec un corps expression, le type se met entre parenthèses : `gear f(): ((int) => int) => (x) => x + 1;` (sans elles, `=>` commencerait le corps).
- **C++** : `rgl::standard::function<R(A…)>` du runtime (petite capture rangée sans allocation, grande sur le tas par un AbiAllocator, partagée par les copies), construite depuis une lambda C++ `[=]`.

### Paramètres multiples — `params`

`params [T] reste` est le dernier paramètre d'un gear, d'un guard ou d'un emit : il reçoit tous les arguments qui restent, comme en C#. Il vient après les paramètres obligatoires et ceux par défaut ; c'est un tableau `[T]` dynamique, sans `mut`, `this` ni valeur par défaut (E134).

```rgl
gear somme(int base, params [int] valeurs): int { … }

somme(1);                 // valeurs est []
somme(1, 2, 3);           // valeurs est [2, 3]
somme(1, [2, 3]);         // un tableau seul est le tableau lui-même
```

- **Un seul argument en trop** : s'il s'accepte comme tableau `[T]`, c'est le tableau (la forme normale, comme en C#) ; sinon c'est un élément. Plusieurs : des éléments, convertis chacun en `T` (E003).
- **Arité** : « au moins N arguments » (E058), N comptant les paramètres obligatoires. Avec le pipeline, `v |> f(a)` donne `v` en premier argument.
- **Génériques** : `gear liste<T>(params [T] xs)` déduit `T` de chaque élément, ou de l'élément du tableau passé seul.
- **Une valeur** : un gear qui a `params` pris comme valeur (`var f: somme;`) a le type de sa signature, dont le dernier paramètre est un tableau : on y passe un tableau (`f(1, [2, 3])`).
- **Pas de `params`** dans un train, un opérateur ni un opérateur d'index (E134).
- **C++** : l'appelant fabrique le tableau (`array<T>`) ; le gear reçoit un paramètre ordinaire.

## 11. Guard — `guard`

Gear de validation, le seul qui peut échouer. `fail` arrête le guard et envoie une erreur sur le rail de son étape. `done` laisse passer le premier paramètre : il n'est donc permis que si le type de retour est celui du premier paramètre ; sinon, on utilise `return`.

```rgl
guard positive(int n): int {
    if (n < 0) { fail "négatif"; }
    done;
}
```

`fail;` envoie une `Exception` de message « échec », `fail "texte";` une `Exception` de ce message, `fail valeur;` cette erreur. Le `where` de l'erreur reçoit toujours la position du `fail`.

**Types d'erreur.** Une erreur est un moule qui a au moins `string message` et `string where`. `error Nom { champs }` en déclare un : il reçoit d'office `message` (« échec » par défaut) et `where`. `Exception` est l'erreur standard, sans autre champ ; `CastError` est envoyée par un `as!`, un cast ou une borne contrôlés qui échouent dans un guard ; `LoopError` par une boucle de guard `*<!N>` qui atteint sa limite ; `NativeError` viendra avec l'intégration C++. Tout autre moule qui a les champs d'`Exception` est accepté comme erreur (duck typing). Une erreur s'écrit en texte « message (where) ».

```rgl
error Introuvable { string chemin; }

guard charger(string chemin): string {
    fail new Introuvable {chemin, message: "absent"};
}
```

**Propagation.** Un guard peut appeler un autre guard : l'appel donne sa valeur, et un échec quitte aussitôt l'appelant avec la même erreur. Les erreurs qu'un guard peut envoyer sont donc celles de ses `fail`, celles des guards qu'il appelle, `CastError` et `Exception` (division entière par une valeur inconnue). Un gear ou un emit n'appelle jamais de guard (**E062**).

Un guard qui atteint la fin de son corps sans `done`, `fail` ni `return` se comporte comme `done;` si son retour a le type de son premier paramètre, et le compilateur avertit (**W007**) ; sinon, c'est **E056**.

Avec la forme `-> last & {F₁, F₂}` (pas encore disponible), `done` renvoie un moule anonyme `{last: L, F₁: v₁, …}`. `last` vaut la valeur du pipeline juste avant le dernier changement de type ; s'il n'y en a eu aucun, `last` vaut `null` et le compilateur avertit.

```rgl
// Vec2 → Vec2 → float → guard
a |> add(b) |> getLength |> isEmpty
// dans isEmpty : valeur courante = float, last = le Vec2 produit par add
```

## 12. Emit — `emit`

Gear terminal : dernière étape d'un train, I/O et appels de trains autorisés. Il déclare son type de retour ; avec `: void`, il renvoie `done` implicitement. Un `emit` au milieu d'une chaîne est une erreur.

```rgl
emit print(int n): void {
    n -> console;
}
```

Un emit qui termine le **train d'entrée** est `void` (code 0) ou renvoie un `uint`, borné ou non (`uint<0..255>` par exemple) : sa valeur est le code de sortie (**E067** pour un autre type). Dans un fichier `forge: train`, un emit peut renvoyer n'importe quel type : c'est la valeur du train.

## 13. Train — `train`

Point d'entrée : une chaîne de gears qui se termine par un `emit`, `done` ou `fail`. Un train, rails compris, forme une seule instruction terminée par un unique `;`. Il part d'un paramètre ou de `null`. S'il ne contient aucun guard, il doit être préfixé par `!` (sauf `train |> done;`). Un fichier `forge: train` peut contenir plusieurs trains (voir plus bas) ; le fichier `.rgl` n'en contient qu'un.

```rgl
train(int n) n |> positive !> [logError] |> twice |> print;

!train(int n) n |> twice |> print;   // aucun guard → !
```

**Train d'entrée.** Le train du fichier `.rgl` a une signature fixe : `([string] args): uint`. Il reçoit les arguments de la ligne de commande et renvoie le code de sortie du programme : `done` donne 0, `fail` ou un guard qui échoue sans rail donnent 1, et `$ valeur` renvoie un code choisi. Une autre signature lève **E015**. Un `emit` qui le termine est `void` ou renvoie un `uint` (borné ou non), sinon **E067**.

```rgl
// main.rgl — pas d'en-tête
train([string] args) args |> parseArgs !> [printUsage |> $ 2] |> run |> $ 0;
```

**Plusieurs trains par fichier.** Un fichier `forge: train` s'écrit dans cet ordre :

1. en tête, les constantes, gears, moules… **communs** à tous ses trains ;
2. des **trains nommés** : `train nom { … }`, chacun avec ses constantes, gears et moules **locaux**, puis son train ;
3. le **train par défaut**, obligatoire et toujours le dernier : un train seul, ou un bloc sans nom `{ … }` avec ses déclarations locales.

```rgl
// Commandes.forge
forge: train
description: "Commandes de l'outil"

const int MAX: 100;                          // commun à tous les trains
gear pure propre(string t): string => …;

train ajouter {
    gear verifier(string t): string => …;    // local à ajouter
    train(string texte) texte |> lire !> [corriger] |> verifier |> enregistrer;
}

train retirer {
    train(string texte) texte |> ajouter |> effacer;     // appelle ajouter, écrit au-dessus
}

train([string] args) args |> choisir |> …;  // train par défaut
```

Un train n'appelle que les trains **écrits au-dessus** de lui : le graphe des trains est sans cycle, et chaque train est lié après ceux qu'il appelle. Les déclarations communes n'appellent aucun train du fichier.

**Appel d'un train.** Un train s'appelle comme un gear, n'importe où : dans une expression, comme étape (`|> ajouter`), dans un gear ou un emit. Un gear `pure` n'appelle qu'un train sans effet. Seuls les paramètres d'un train sont typés ; son **type de retour est toujours déduit** :

- `T` est le type commun de toutes ses fins : la valeur d'un emit, `done` (la valeur arrivée à cette étape), `$ v` (v), la valeur finale d'un rail ; des fins sans type commun sont une erreur ;
- s'il peut échouer (un guard sans rail, un `fail`), il renvoie `T ! E`, où E est l'union de ces erreurs ; un `!train` ne peut pas échouer et renvoie toujours `T`.

```rgl
import Commandes only {ajouter as add};

var r: add("x");                     // string ! Exception
var s: Commandes([]) ?? 0;           // le train par défaut, sous le nom du fichier
```

Le train d'entrée fait exception : sa signature est fixe et ses fins donnent le code de sortie.

## 14. Pipeline — `|>`

`V |> G(a, b)` vaut `G(V, a, b)` : la valeur courante est toujours le premier argument. Après un `fail`, la suite de la chaîne n'est pas évaluée.

```rgl
a |> add(b) |> getLength
```

## 15. Rail d'erreur — `!>`

Clause du train (pas une expression), écrite entre crochets juste après le guard dont elle reçoit l'échec : `|> lire !> [reparer |> $ 3]`. Elle n'existe que dans un train : un gear ne gère jamais lui-même les erreurs de ses appels. Un rail reçoit l'erreur de son guard dans sa première étape, et se termine lui aussi (`done`, `fail`, `$ valeur` ou un emit) : il termine le train, il ne reprend pas le chemin.

- Le rail reçoit l'**union des types d'erreur** que son guard peut envoyer (un seul type s'il n'y en a qu'un). Il n'y a pas d'aiguillage par type : c'est le gear qui reçoit l'erreur qui la distingue, avec `match`.
- Un guard **sans rail** qui échoue fait échouer le train (code de sortie 1 pour le train d'entrée).
- Les étapes d'un rail peuvent avoir leurs propres rails : `!> [w |> x !> [i |> p] |> n]`.
- Un `fail` en fin de chemin fait échouer le train.
- Seuls les guards échouent : un gear qui rencontre un problème (débordement, division par zéro…) arrête le programme.

```rgl
train path |> load !> [useDefault |> save |> done]
           |> parse !> [report |> $ 2]
           |> save;
```

Le compilateur avertit (**W004**) pour un rail qui ne sera jamais pris : derrière un gear, ou un guard qui ne peut pas échouer. Un rail sans crochets, ou après une fin de chemin, est **E090**.

## 16. Point de sauvegarde — `^N`

`^N` posé devant un gear ou un guard à un seul paramètre marque un point de reprise (**E097** devant autre chose, ou pour un numéro déjà posé : un seul point de chaque numéro dans le train, rails compris). Une étape `^N` y revient avec la valeur courante, qui entre alors dans l'étape marquée (du même type, **E003** sinon) ; elle peut s'écrire n'importe où après le point, sur son chemin ou dans un rail qui en part, même imbriqué ou par élément d'un `||>`, mais jamais en avant (**E098** pour un point inconnu ou pas encore passé). `^N` sans limite et `^N<!M>` terminent le chemin ; `^N<M>` reprend au plus M fois, puis le chemin continue après lui, ce qui le suit servant de repli ; `^N<!M>` fait alors échouer le train. Le compteur de reprises repart de zéro chaque fois que le chemin passe par le point en avançant.

```rgl
train path |> ^1 tryLoad !> [findAlt |> ^1<3> |> useFallback] |> process;
```

## 17. Boucle de guard — `*`

Seul un guard boucle (**E095**), et seulement comme étape d'un train ou d'un rail : il reçoit à chaque tour la valeur qu'il vient de renvoyer, il renvoie donc le type de sa première entrée (**E096**). `done` sort de la boucle, `fail` part sur le rail. `*` seul est une boucle sans limite (la boucle principale d'un jeu) ; `*<N>` s'arrête après N tours et passe la dernière valeur à l'étape suivante ; `*<!N>` échoue après N tours avec l'erreur standard `LoopError` (« N tours dépassés »), qui rejoint les erreurs du guard sur son rail. Une limite vaut au moins 1 (**E099**). Pour répéter un gear, on l'appelle depuis un guard qui termine par `done`.

```rgl
train(int n) n |> converge*<10> |> print;
```

## 18. Parcours de collection — `||>` et `|!|>`

Applique une étape à chaque élément d'un tableau (toute sorte de tableau) et donne le tableau `[T]` des valeurs obtenues. C'est la seule façon d'itérer : un parcours s'écrit dans un train, et comme expression partout ailleurs (gear, guard, emit, lambda, constante du fichier).

```rgl
train |> getUsers() ||> isActive !> [logSkipped] ||> notify |> done;

var doubles: nums ||> double;
var carres: nums ||> (x) => x * x ||> add(1);   // lambda sur place, arguments après l'élément
```

- **Étape** : un gear (il transforme l'élément), un guard (il garde l'élément s'il passe : sa valeur, ou l'élément lui-même pour un guard `void`), une lambda écrite sur place, ou une variable qui contient une lambda. Des arguments en plus suivent l'élément, comme dans un pipeline : `||> scale(2)`. Une étape `void` termine le parcours, qui ne renvoie alors rien (`nums ||> afficher;`). Un emit ne s'applique pas à chaque élément (**E063**).
- **`||>`** garde les éléments qui passent. Un élément raté passe par le rail de l'étape, s'il y en a un (dans un train), puis le parcours **continue** ; sans rail, il est écarté en silence. Dans ce rail par élément, `done` et un emit `void` passent à l'élément suivant ; `$ v`, `fail` ou un emit qui renvoie un code terminent le train comme tout rail.
- **`|!|>`** abandonne tout au premier échec : dans un train, l'échec part sur le rail de l'étape (qui termine le train) ou fait échouer le train ; dans un guard, il fait échouer le guard avec la même erreur ; ailleurs, il n'aurait nulle part où aller (**E062**). Un guard appliqué par `||>` dans un guard ne le fait pas échouer : ses éléments ratés sont écartés.
- **Enchaînement** : plusieurs parcours qui se suivent (`liste ||> a ||> b`) traitent chaque élément de bout en bout, `a` puis `b`, avant l'élément suivant ; `|>` reprend ensuite le tableau entier.
- **Intervalle** : `[0..10]` compte de 0 à 10 (fin comprise), `[0..<10]` de 0 à 9 (fin exclue), `[0..10 by 2]` avec un pas, `[10..0]` en descendant. Les bornes sont des entiers (pas de flottants), ramenées à un type commun ; un littéral prend le type de l'autre borne (`[0..<MAX]` compte en `uint` si `MAX` en est un), un mélange sans type commun demande un cast. Sans pas, l'intervalle monte, sauf quand ses bornes connues à la compilation descendent ; quand elles ne sont connues qu'à l'exécution, il faut un pas négatif pour descendre. Le pas n'est jamais nul (arrêt du programme s'il l'est à l'exécution) et ne va pas dans l'autre sens que des bornes connues (**E100**). C'est un tableau comme un autre (variable, argument, spread), de taille fixe `[T;N]` quand tout est connu à la compilation, `[T]` sinon ; devant un parcours ou une accumulation, il est parcouru sans être construit : `[0..<n] ||> naissance` est le « for » de RGL.
- **Accumulation** : `tableau |+> départ with étape` réduit un tableau à une seule valeur, dans un train et comme expression (`notes |+> 0 with (acc, n) => acc + n`). L'accumulateur part de la valeur de départ, obligatoire (un tableau vide donne le départ), puis reçoit le résultat de l'étape sur lui-même et sur chaque élément. Son type est celui du premier paramètre d'un gear ou d'un guard nommé, sinon celui du départ. L'étape est une lambda `(acc, x) => …`, une variable lambda, ou un gear ou un guard à deux paramètres (l'accumulateur, puis l'élément), avec des arguments en plus comme un pipeline. Un guard renvoie le nouvel accumulateur ; son `done` arrête l'accumulation avec l'accumulateur reçu, son `fail` fait échouer toute l'accumulation, jamais un élément écarté en silence : rail de l'étape dans un train, échec du guard englobant, ailleurs **E062**. Derrière des parcours (`v ||> f |+> 0 with g`), tout se fait dans une seule boucle, sans tableau intermédiaire.
- **Pureté** : un parcours a l'effet de ses étapes ; un gear `pure` n'applique que des étapes pures.
- **Sur place** : sur une variable `mut`, une première étape qui reçoit l'élément dans un paramètre `mut` (`ps ||> (mut p) => { p.vie -<- 1; }`, `ps ||> vieillir`) le modifie dans le tableau lui-même (voir *Mutabilité*).

## 19. Structure of Arrays — SoA

**Automatique.** Un tableau dynamique de moules `[Mold]` est rangé en colonnes, sans annotation, sauf quand les parcours du programme lisent tous ses champs : chaque ligne de cache chargée sert alors en entier, et les colonnes ne coûteraient que la reconstruction de chaque élément, il reste un tableau de structs. En colonnes : une colonne par champ, sous-moules aplatis (`position` donne `position.x` et `position.y`). Un élément se reconstruit à la lecture et se répartit à l'ajout ; une boucle qui ne lit que quelques champs ne charge que leurs colonnes. Un champ `[T]` reste une colonne de tableaux, non aplatie (**W041**, signalé au parcours). Un moule qui se contient lui-même à travers un tableau (`mold Noeud { [Noeud] enfants; }`) reste un tableau de structures.

```rgl
mold Entity { Vec2 position; float mass; }
// [Entity] génère les colonnes : position.x, position.y, mass
```

Les colonnes sont rangées de la plus chaude à la plus froide, puis, à chaleur égale, par alignement décroissant :

| Niveau | Champ… | Placement | État |
| --- | --- | --- | --- |
| Hot | lu dans une étape d'un parcours (gears appelés et lambdas compris) | En premier | Fait |
| Warm | lu dans la boucle, derrière une branche | Ensuite | VBeta |
| Cold | lu seulement hors des parcours | En dernier | Fait |
| Drop | jamais lu | Supprimé, aucune allocation | VBeta |

| Source | Structure C++ générée | Stockage par champ |
| --- | --- | --- |
| `[Mold]` | `rgl::standard::soa_array<Layout>`, un Layout généré par moule | `rgl::standard::array<T>` |
| `[Mold;N]`, `[Mold;N..M]`, `[Mold+]` | Tableau de structures pour l'instant (`T_SoA_Fixed<N>` en VBeta) | — |
| `[[Mold;N]]` | AoSoA (blocs de N), en VBeta | — |

**Manuel.** `soa mold` déclare un layout tel quel ; `|S|>` et `||S|>` passent chaque ligne à un gear dont les paramètres correspondent aux champs par nom. `|S|>` échoue si les tableaux n'ont pas la même taille, `||S|>` s'arrête au plus court.

```rgl
soa mold Particles {
    [float] vx;
    [float] vy;
}

gear damp(float vx, float vy): {float vx, float vy} => {vx: vx * 0.9, vy: vy * 0.9};

particles |S|> damp;
```

**Frontière native.** Un tableau en SoA ne passe pas tel quel à un gear `.rgl.cpp` : `pack()` le reconvertit en tableau de structures. Appeler `pack()` après suppression d'un champ est une erreur.

```rgl
nativeGear(pack(particles));
```

## 20. Moule — `mold`

Structure de données immuable, sans méthode autre que ses opérateurs, copiée à l'affectation. Les moules se déclarent dans un fichier `forge: mold` (plusieurs par fichier), ou dans le fichier d'un train ou d'une bibliothèque qui les utilise. `...` embarque les champs d'un autre moule, sans héritage : la compatibilité est structurelle (duck typing).

```rgl
// fichier Geometry.forge
forge: mold
description: "Vecteurs 2D et 3D"

mold Vec2 {
    float x;
    float y;
}

mold Vec3 { ...Vec2; float z: 0.0; }        // champ avec valeur par défaut

Vec2 v: {x: 1.0, y: 2.0};            // type fourni par la déclaration
var w: new Vec2 {x: 1.0, y: 2.0};            // new obligatoire sans type cible
var cfg: {name: "App", debug: false};        // moule anonyme

float x: 1.0;
float y: 2.0;
Vec2 p: {x, y};                      // raccourci de {x: x, y: y}
```

**Duck typing.** Une valeur `{f₁: T₁, …}` est acceptée là où un moule `M` est attendu si `M` a au moins ces champs, avec des types assignables. Les champs de `M` absents de la valeur prennent leur valeur par défaut de moule (`TYPE nom: valeur;`) ; un champ nullable (`T? nom;`) sans valeur par défaut vaut `null` et peut donc être omis ; sinon, c'est une erreur. Le duck typing ne retire jamais de champ en silence pour trouver un type commun : les bras d'un `match` ou les valeurs d'un ternaire vont directement dans le type attendu par le contexte (déclaration typée, `return`, affectation) ; sans type attendu, deux moules différents n'ont de type commun que si l'un contient tous les champs de l'autre. Une valeur qui entre dans une union va dans le membre qui garde tous ses champs.

**Génériques.** Un moule accepte des paramètres de type (pas de valeurs).

```rgl
// fichier Pair.forge
forge: mold
description: "Paire générique"

mold Pair<T, U> {
    T first;
    U second;
}

Pair<int, string> p: {first: 1, second: "un"};
```

**Surcharge d'opérateurs.** Un moule peut définir dans son corps une conversion implicite, une conversion explicite et les opérateurs `+`, `-` (binaire et unaire), `*`, `/`, `%`, `eq`, `<`, `>`, `bitand`, `bitor`, `bitxor`, `shl` et `shr`. Syntaxe : `[pure] Type nom(paramètres typés) { … }`, où `Type` est le type renvoyé et `nom` l'opérateur (`implicit` et `explicit` sont des mots contextuels). La conversion explicite s'utilise avec `as` ; la conversion implicite s'applique dès que le type cible est attendu.

```rgl
// fichier Geometry.forge
forge: mold
description: "Vecteurs 2D et 3D"

mold Vec2 {
    float x;
    float y;

    Vec2 +(Vec2 a, Vec2 b) { return {x: a.x + b.x, y: a.y + b.y}; }
    Vec2 -(Vec2 a, Vec2 b) { return {x: a.x - b.x, y: a.y - b.y}; }
    Vec2 -(Vec2 v)         { return {x: -v.x, y: -v.y}; }
    Vec2 *(Vec2 v, float k) { return {x: v.x * k, y: v.y * k}; }
    Vec2 *(float k, Vec2 v) { return v * k; }
    bool eq(Vec2 a, Vec2 b) { return a.x eq b.x and a.y eq b.y; }
    bool <(Vec2 a, Vec2 b)  { return a.x < b.x; }
    bool >(Vec2 a, Vec2 b)  { return a.x > b.x; }

    Vec3 implicit(Vec2 v)   { return {x: v.x, y: v.y, z: 0.0}; }
    string explicit(Vec2 v) { return `({v.x}, {v.y})`; }
}

Vec2 c: a + b;
Vec3 p: c;               // conversion implicite
var texte: c as string;          // conversion explicite
```

Règles :

- L'ordre des opérandes compte : `Vec2 *(Vec2 v, float k)` ne couvre pas `float * Vec2`, qui se déclare à part. Aucun opérateur n'est inversé automatiquement (`a - b` n'est pas `b - a`).
- Si les deux moules d'une opération surchargent le même opérateur pour le même ordre d'opérandes, c'est ambigu : **E009**.
- `not eq` se déduit de `eq` ; `<=` de `<` et `eq` ; `>=` de `>` et `eq`.
- `<` et `>` vont ensemble : définir l'un sans l'autre lève **E010**.
- Quand une valeur peut être acceptée à la fois par une conversion implicite déclarée et par le duck typing, **la conversion déclarée l'emporte toujours**.
- Un opérateur ou une conversion s'appelle comme un gear (il porte le nom `Moule.opérateur` dans les messages). Il est `pure` s'il l'écrit (`pure Vec2 +(…)`), et seul un opérateur `pure` s'appelle depuis un gear `pure` (**E057**).
- À l'appel, on cherche parmi les opérateurs des moules des deux opérandes : d'abord ceux dont les paramètres sont exactement les types des opérandes, sinon ceux où chaque opérande se convertit (`v * 2` convient à `Vec2 *(Vec2 v, float k)`, `v + {x: 1.0, y: 2.0}` à `Vec2 +(Vec2 a, Vec2 b)`). Si plusieurs conviennent, c'est **E009**. Sans opérateur déclaré, `eq` compare champ à champ et les autres opérateurs sont **E040**.
- `<=` et `>=` lisent chaque opérande deux fois : un opérande qui a un effet est refusé (écris-le d'abord dans une variable).
- `as` appelle la conversion `explicit` du moule, sinon son `implicit` ; `as?` et `as!` n'en appellent aucune.
- **E118** : déclaration invalide — mauvais nombre de paramètres (un ou deux pour `-`, un pour `implicit` et `explicit`, deux pour les autres), aucun paramètre du moule parmi les opérandes (une conversion reçoit un moule : le sien), `eq`, `<` ou `>` qui ne renvoient pas un `bool`, conversion vers son propre type, doublon (même opérateur et mêmes types d'opérandes, ou même conversion vers le même type), paramètre `mut`.
- Pas encore implémenté : un opérateur ou une conversion dans un moule générique.

## 21. Precast — `precast`

Instance nommée d'un moule, entièrement résolue à la compilation. Elle vit dans un fichier `forge: precast`, avec au plus un import de moule. Un precast sans moule importé déclare son moule à la volée (un seul par fichier).

```rgl
// fichier Vectors.forge
forge: precast
description: "Vecteurs remarquables"

import Geometry only {Vec2};
precast Vec2 Zero  { x: 0.0, y: 0.0 }
precast Vec2 UnitX { x: 1.0, y: 0.0 }

// fichier AppConfig.forge
forge: precast
description: "Configuration de l'application"

precast AppConfig {
    string name: "MonApp";
    bool debug: false;
}
```

Utilisation : `import Vectors;` puis `Vectors.Zero`, ou `import Vectors only {Zero};`. Un precast est une constante du fichier, de type mold, importable comme les autres.

- Les champs s'écrivent séparés par `;` ou `,`.
- Un fichier `forge: precast` ne contient qu'un import de mold (au plus un) et des precasts (**E104** sinon). Avec un import, chaque precast écrit son mold avant le nom ; sans import, le precast déclare son mold (par ses champs typés, ou par un type anonyme écrit) et c'est le seul du fichier (**E104** pour un second, ou pour un precast qui déclare son mold dans un fichier qui en importe un).
- Un precast hors d'un fichier `forge: precast` est **E104**. Un precast est une donnée résolue à la compilation : chaque champ a une valeur (**E121**), faite de littéraux, de constantes, de molds, de tableaux (`[string] data: ['a', 'b'];`), de dictionnaires et de tuples, sans appel (**E121**). Elle est `constexpr` quand le C++ le permet ; un tableau dynamique alloue et est construit une fois au démarrage. Un precast que rien ne lit n'est pas écrit dans le C++ ; un champ inconnu ou d'un autre type est **E003**.
- Pas encore implémenté : l'export des precasts en JSON.

Un precast sans moule importé peut aussi typer son moule explicitement :

```rgl
precast {string name, string version, bool debug} AppConfig {
    name: "MonApp";
    version: "1.0.0";
    debug: false;
}
```

**Données d'outillage.** Les precasts sont le format des données produites par les outils, comme l'éditeur de jeux 2D. Un fichier de precast généré doit être identique à la sortie du formateur officiel, pour ne créer aucune différence parasite dans Git. Le compilateur fournit un export des precasts en JSON, pour qu'un outil puisse les relire sans réimplémenter le parseur.

## 22. Énumération — `enum`

Variantes nommées, fermées à la compilation, sans coût à l'exécution. Le type sous-jacent est fixé après `:` (n'importe quel primitif : nombre, `bool`, `string`, `symbol`), et chaque variante a alors sa valeur, constante et unique ; sans type écrit, aucune variante n'a de valeur et le compilateur choisit le plus petit type qui les numérote. On ne mélange pas les deux.

```rgl
enum Direction { North, South, East, West }
enum Status: int { Idle: 0, Running: 1, Error: -1 }
enum Couleur: string { Rouge: "rouge", Vert: "vert" }
```

| Nombre de variantes | Type sous-jacent automatique |
| --- | --- |
| 2 au plus | `bool` |
| Jusqu'à 256 | `ubyte` |
| Jusqu'à 65 536 | `ushort` |
| Au-delà | `uint` |

Une variante se lit `Direction.North` ; dans un `case` ou un `goto` d'un `switch` sur l'enum, `North` seul suffit. Un `switch` sur un enum sans `default` qui ne couvre pas toutes les variantes avertit (**W012**) ; s'il les couvre toutes, un cas est toujours choisi. Un enum se compare avec `eq` et s'écrit en texte par le nom de sa variante.

**Génération C++.** Un enum devient un `enum class` sur son type sous-jacent (sur sa position pour un enum de flottants, de chaînes ou de symboles), en debug comme en release : le débogueur voit les noms, et le binaire optimisé ne contient que des entiers. Seuls les enums écrits en texte y gardent les noms de leurs variantes.

**`dyn_enum`** : énumération que d'autres fichiers ou paquets peuvent étendre, à la compilation uniquement. `dyn Nom.add(Variante)` n'est permis qu'en portée fichier ; la numérotation suit l'ordre de compilation. Deux paquets qui ajoutent la même variante déclenchent une erreur.

```rgl
// events.forge
dyn_enum AppEvent { Click, Hover }      // 0, 1

// authevents.forge
dyn AppEvent.add(Login)                 // 2

var e: AppEvent.Login;
```

## 23. Ingot — `ingot`

Donne une identité nominale à n'importe quel type (un nombre, une chaîne, un mold, un type borné…), sans coût : le C++ émis utilise directement le type de base. Un ingot ne se mélange ni avec sa base ni avec un autre ingot, même de même base. `°` retire la marque pour une expression ; `UserId(v)` marque une valeur de la base ; `°<-` affecte une valeur de la base sans la marquer d'abord. Seule une constante entre dans un ingot sans rien écrire.

```rgl
ingot UserId: int;
ingot Angle: float<0.0..360.0>;
UserId uid: 42;          // une constante entre seule

var brut: uid°;                  // int
uid °<- 43;                      // valeur de la base, sans marque
var suivant: uid + 1;            // UserId : le calcul garde la marque
var a: Angle(brut * 2.0);        // marquée, et contrôlée dans ses bornes
```

| Expression | Résultat |
| --- | --- |
| `uid + 1`, `uid * uid` | `UserId` — même ingot ou constante, le calcul garde la marque |
| `uid + brut` (`brut` de type `int`) | erreur — écrire `uid° + brut` ou `uid + UserId(brut)` |
| `uid eq UserId(42)` | `true` — même marque, même valeur |
| `uid eq autreId` (`ProductId`) | erreur — marques différentes |
| `joueur.nom` (`ingot Joueur: Personnage`) | les champs d'un ingot de mold se lisent directement |

Un ingot s'écrit en texte comme sa base. Il ne peut pas contenir sa base elle-même, même dans un tableau (E089), ni porter le nom d'un gear.

## 23 bis. Extension — `extend`

Ajoute à un type des constantes et des gears, un peu comme les méthodes d'extension de C#. Une extension n'existe que pour le compilateur : le C++ généré ne change pas (un gear d'extension est un gear ordinaire, une constante une constante ordinaire). Elle s'écrit dans un fichier `forge: gears` (**E104** ailleurs, dans le `.rgl` comme dans un `forge: train`).

```rgl
// fichier System.forge
forge: gears
description: "Système"

ingot ProgramValue: uint<0..255>;

extend ProgramValue {
    const ProgramValue Valid: 0;
    gear pure plus(this ProgramValue v, uint<0..255> n): ProgramValue => ProgramValue(v° + n);
    gear pure make(uint<0..255> n): ProgramValue => ProgramValue(n);
    emit Emit(this ProgramValue v): uint<0..255> => v°;
}
```

```rgl
import System;

System.ProgramValue p: System.ProgramValue.Valid;       // constante : Type.nom
var q: p.plus(3);                                // méthode : plus(p, 3)
var r: System.ProgramValue.make(10);             // gear sans this : sur le type
!train |> Test |> System.ProgramValue.Emit;      // emit d'extension, comme étape
```

- **Constantes.** `const T nom: valeur;` s'appelle `Type.nom`, où `Type` est un mold, un enum ou un ingot. Chacune a sa propre portée : elle ne gêne aucune constante du fichier, et se déclare dans l'ordre du fichier comme les autres constantes.
- **Méthodes.** Comme en C#, le premier paramètre d'un gear s'écrit `this Type nom` et doit être du type étendu. `v.f(a)` est exactement `f(v, a)` ; `Type.f(v, a)` marche aussi. Un gear sans `this` est statique : il s'appelle sur le type (`Type.f(a)`), comme étape de train, dans un pipeline (`v |> Type.f`) ou dans un parcours — jamais par son nom seul. Un `guard` ou un `emit` y est permis.
- **Quels types.** L'extension d'un type **déclaré dans le même fichier suit ce type** : tout fichier qui voit le type voit ses extensions (`Fichier.Type.nom` après `import Fichier;`, `Type.nom` après `import Fichier only {Type};`). L'extension d'un autre type (un type primitif, un tableau, un type importé) n'est vue que du fichier où elle est écrite ; sans nom de type, seules les méthodes y sont possibles (`extend int { gear pure double(this int n): int => n * 2; }`).
- **Précédence.** Un champ lambda du moule passe avant une extension du même nom ; les méthodes des collections (`push`, `add`, `has`) avant toute extension.
- **E119** : nom en double (les gears et les constantes d'un même type partagent leurs noms), `this` d'un autre type que le type étendu, gear sans `this` ou constante sur un type qui n'est pas nommé.
- Pas encore implémenté : un gear générique dans une extension, et les extensions dans un fichier `forge: train` ou dans le `.rgl`.

## 24. Résolution d'union — `match`

Une valeur de type union doit être résolue avant d'aller dans un paramètre typé. Seule exception : si tous les membres sont des moules qui ont un même champ, `valeur.champ` le lit directement ; il est du type de ce champ, ou de l'union de ses types s'ils diffèrent. Le compilateur exige que chaque type de l'union soit couvert, ou un bras `default`. Une union ne contient ni `null` ni nullable : une union qui peut être `null` s'écrit `(int | string)?`.

```rgl
match (val) {
    int i    |> doInt(i);
    string s |> doString(s);
}

var texte: match (val) {            // chaque bras donne une valeur : le match en est une
    int i |> `entier {i}`;
    default |> "autre";
};
```

Un bras est une expression suivie de `;`, un bloc `{ … }`, ou une instruction de sortie (`return`, `fail`, `break`, `goto`, `done`) ; avec un bloc, le match est une instruction.

**Match de valeurs.** Sur toute autre valeur (nombre, `bool`, `string`, `symbol`, enum, mold constant), `match` est un switch en ligne qui donne une valeur : `case` à la place du type, la flèche `|>` à la place de `:`.

```rgl
var taille: match (n) {
    case 1, 2       |> "petit";
    case eval $ > 10 |> "grand";     // $ est la valeur, comme dans un case eval de switch
    default         |> "moyen";
};

var cap: match (d) { case North |> 1; case South, East |> 2; };   // enum couvert : pas de default
```

- Mêmes valeurs de `case` que le switch : constantes (**E051**), uniques (**E052**), une variante nue sur un enum, du type de la valeur (**E040**) ; même `case eval`.
- Chaque bras est indépendant : pas de fallthrough, pas de `goto` entre bras ; les bras se testent dans l'ordre, `default` en dernier.
- Sans `default`, tous les cas sont couverts : toutes les variantes d'un enum, ou `true` et `false` ; sinon **E082**. Une condition `eval` ne prouve rien. Pour une valeur qui n'est ni un enum ni un `bool`, le `default` est obligatoire.
- Un `case` sur une union ou un résultat, ou un bras de type (`int i`) sur une autre valeur, est **E080**. Les bras de type et les bras `case` ne se mélangent pas.

**Résultat `T ! E`**, comme le `Result` de Rust : un succès de type T ou une erreur de type E, avec une étiquette (T et E peuvent être le même type). Il se construit par `ok(v)` et `err(e)`, ou par la valeur seule quand un seul côté l'accepte ; il se lit par `match` avec les bras `ok v` et `err e`, ou par `r ?? défaut`.

```rgl
gear diviser(int a, int b): int ! string {
    if (b eq 0) { return err("division par zéro"); }
    return a / b;                   // ok(a / b)
}

match (diviser(7, 2)) {
    ok v  |> v -> console;
    err e |> e -> console;
}
```

## 25. Cast — `as`, `as?`, `as!`

Conversion explicite entre deux nombres, ou lecture d'un nombre dans un texte. `as` contrôle la valeur, `as?` produit `T?` (`null` si elle ne tient pas), `as!` fait échouer le guard qui l'évalue (il n'existe que dans un guard).

```rgl
var n: rawFloat as int;                  // tronqué vers zéro
var peutEtre: grand as? byte;            // byte? — null si hors plage
var lu: texte as? int<0..10>;            // null si ce n'est pas un nombre de 0 à 10
```

| Conversion | `as` | `as?` / `as!` |
| --- | --- | --- |
| Valeur connue à la compilation | doit tenir dans le type (E086) | — |
| Élargissement (`byte` vers `int`, `int` vers `double`…) | sans contrôle | toujours réussi |
| Entier vers entier plus petit | contrôlé selon `overflow` : arrêt en `checked`, bits de poids fort perdus en `unchecked` | échoue hors plage |
| Flottant vers entier | tronqué vers zéro ; hors plage ou NaN arrête toujours le programme | échoue hors plage ou NaN |
| `double` vers `float` | arrondi ; au-delà du plus grand `float`, l'infini | — |
| Texte vers nombre | refusé (E085) | échoue si le texte n'est pas entièrement un nombre du type |
| Vers un type borné | les bornes sont contrôlées à leur tour | |
| Autre conversion implicite (duck typing, union, nullable) | permise, simplement écrite | refusé (E085) |

Un contrôle qui échoue arrête le programme avec l'endroit du cast ; dans un guard, il fait échouer le guard (Exception). `as` ne retire pas la marque d'un ingot (`°`) et ne résout pas une union (`match`). Un cast vers le type que la valeur a déjà est signalé inutile (W013).

**Type borné** `int<0..100>`, `float<0.0..360.0>`, `int<-5..5>` : un type numérique dont les valeurs restent entre deux bornes comprises, connues à la compilation. Une valeur connue est vérifiée à la compilation (E086) ; une autre est contrôlée à l'exécution : hors des bornes, le programme s'arrête, ou le guard échoue. Un calcul sur un type borné se fait sur sa base (`pct + 10` est un `int`), et le résultat est contrôlé quand il revient dans la variable bornée ; une valeur dont les bornes sont contenues dans celles visées passe sans contrôle.

## 26. Import et export — `import`, `export`

Amène le contenu d'un autre fichier. Un import n'a **pas de mot-clé** : le genre du fichier vient de son en-tête `forge:`. Seuls `box` et `tools` en ont un, ainsi que les formats de données (`file`, `json`, `xml` : pas encore implémentés).

**Où sont les fichiers.** Un import nomme un fichier **par son nom**, sans chemin ni extension : `import Commandes;` trouve `Commandes.forge` dans n'importe quel sous-dossier du projet (la casse compte). Deux fichiers `.forge` de même nom dans un même espace de fichiers sont **E107**, même si aucun n'est importé ; un fichier introuvable est **E107** aussi. Les dossiers `*.box` sont opaques (on n'y cherche pas), les dossiers cachés et ceux de la clé `exclude` de `rgl.json` (un tableau de chemins, `["out"]` par défaut, écrit par `rgl init`) ne sont pas parcourus. Chaque fichier est lié après ceux qu'il importe ; les imports circulaires sont **E108**.

```rgl
import Commandes;                // son train par défaut, Commandes(…), et tout ce qu'il exporte en accès qualifié (Commandes.ajouter)
import Commandes as cmd;         // le fichier sous un autre nom
import Geometry;                 // un fichier de moules : Geometry.Vec2
import Geometry only {Vec2};     // des noms choisis, en accès direct
import Commandes only {ajouter as add, retirer};   // « as » facultatif, noms séparés par des virgules
import [..] Util;                // depuis une box : cherche dans le projet, hors de la box
import box Database;             // l'index d'une box
import tools Vectors;            // un dossier Vectors.tools, noms à plat : Vectors.Vec2
import tools Vectors.Vec2;       // un seul fichier du dossier, noms en accès direct
import file(.toml) config;       // pas encore implémenté : config.toml
import json settings;            // pas encore implémenté : moule anonyme constant
import xml layout;               // pas encore implémenté : XmlNode constant
```

Un `only` donne un accès direct et pas d'accès qualifié ; un nom inconnu du fichier, ou qu'il n'exporte pas, est **E109**. Un import réserve son nom : voir « Un nom, une chose » plus bas.

**Exports (comme JS/TS).** Un fichier `.forge` qui n'écrit **aucun** `export` exporte tout son premier niveau : gears, trains nommés, moules, enums, ingots, constantes. Dès qu'il en écrit un, seuls les noms exportés sont visibles de ceux qui l'importent. L'export s'écrit devant la déclaration, ou en liste :

```rgl
// fichier Outils.forge
forge: gears
description: "Outils"

export const uint LIMITE: 7;
export mold Row { uint id; }
export gear ouvrir(uint n): uint => n;
gear secret(): uint => 1;        // invisible de l'extérieur

export {ouvrir, Row};            // ou en liste (un nom n'est exporté qu'une fois)
```

- `export` précède un `gear`, `guard`, `emit`, `mold`, `error`, `enum`, `ingot`, `const` ou un train nommé ; pas d'alias d'export, pas de `default`. Le train par défaut d'un fichier `forge: train` s'appelle toujours sous le nom du fichier.
- Le fichier garde accès à tous ses propres noms ; l'export ne joue que sur ce que voient les autres fichiers. Le C++ généré n'en sait rien.
- Un nom caché est introuvable en accès qualifié, et E109 dans un `only`. `export {inconnu};` (un nom que le fichier ne déclare pas) est **E113**, un nom exporté deux fois **E103**, un `export` dans le `.rgl` d'entrée **E104**.
- Un gear exporté peut renvoyer un type non exporté : l'importateur utilise la valeur sans pouvoir nommer le type.

**Box** (`Nom.box/`) : dossier opaque de trains, de moules et de gears, qui peut en contenir d'autres. Ses fichiers ne voient que la box : `import X;` y cherche dans la box, `import [..] X;` dans le projet. Seul `index.forge` (genre `index`, qui ne contient que des imports et des exports ; **E104** ailleurs que dans l'index d'une box) expose ce qui est public.

```rgl
// Database.box/index.forge
forge: index
description: "Accès base de données"

import Connexion only {withConnection};
import Lignes;
import Requetes;

export {withConnection, ...Lignes};   // withConnection, et tout ce que Lignes exporte, à plat
export Requetes;                      // le fichier tel quel : ses noms restent qualifiés

// utilisation
import box Database;                  // Database.withConnection, Database.Row, Database.Requetes.query
import box Database only {Requetes as R};   // un fichier exporté tel quel s'importe par son nom
```

Dans l'index, `export {a, b}` exporte des noms importés dans l'index, `export {...Fichier}` tous les noms exportés d'un fichier importé en entier (à plat), `export Fichier;` le fichier lui-même (ses noms restent qualifiés : `Database.Requetes.query`). Un nom ou un fichier qui n'a pas été importé dans l'index est **E113**, de même que `...Fichier` ou `export Fichier;` ailleurs que dans l'index d'une box.

**Tools** (`Nom.tools/`) : dossier ordinaire de fichiers importables un à un ou tous ensemble, sans restriction de type. Ses fichiers se trouvent aussi par un import simple et obéissent à la règle des noms uniques. `import tools T;` donne les noms exportés de tous ses fichiers, à plat, en accès qualifié `T.nom` (deux fichiers qui déclarent le même nom : **E103**) ; `import tools T.f;` ceux d'un seul fichier, en accès direct.

```rgl
import tools Vectors;            // tout le tools, accès qualifié
Vectors.Vec2 pos: {x: 0.0, y: 0.0};
import tools Vectors.Vec2;       // un seul fichier, accès direct
```

**Bibliothèque de gears** (`forge: gears`) : fichier de gears, guards et emits réutilisables, éventuellement accompagnés de moules, enums, ingots, constantes et extensions. Tout son contenu est exporté, sauf s'il écrit un `export`. Le train reste l'unité d'orchestration (rails, point d'entrée) ; la bibliothèque regroupe les petites transformations réutilisables. La bibliothèque standard est elle-même un ensemble de bibliothèques de gears.

```rgl
// fichier Math.forge
forge: gears
description: "Fonctions mathématiques"

gear pure clamp<T:number>(T v, T lo, T hi): T => v < lo ? lo : (v > hi ? hi : v);
gear pure lerp(float a, float b, float t): float => a + (b - a) * t;
```

```rgl
import Math;                     // accès qualifié
import Math only {clamp};        // import ciblé, accès direct

var x: Math.lerp(0.0, 10.0, 0.5);
var y: input |> clamp(0, 100);
```

Une box peut réexporter une bibliothèque de gears dans son index (`export {...Math};`).

**Contraintes structurelles.** Un gear de bibliothèque peut appeler un train (un gear `pure`, seulement un train sans effet). Avec l'absence de boucles impératives et le rail `!>` réservé aux trains, un traitement complexe se découpe naturellement en petits gears enchaînés dans un train.

**Un nom, une chose.** Dans un fichier, trains, gears, moules, enums, ingots, constantes et noms importés partagent un seul espace de noms : deux déclarations du même nom sont une erreur (**E103**, ou E055, E075, E044 pour deux déclarations de même nature), et `as` renomme un import qui gêne.

## 27. Entrées / sorties — `<-` et `->`

**Implémenté : `console` (écriture `->` et lecture `<-`). Les autres formats (`file`, `raw`, `json`, `xml`, `custom`) ne sont pas encore implémentés.**

`->` écrit vers une cible, `<-` lit depuis une source. C'est le seul cas où une déclaration s'initialise avec `<-` au lieu de `:`. Formats : `console`, `file`, `raw`, `json`, `xml`, `custom<T, Train>`.

```rgl
result -> console;
var ligne <- console;

var cfg <- json "config.json";               // moule anonyme
var typed <- json<ParseConfig> "config.json"; // via un train
cfg -> json "sortie.json";
```

| Format | Lecture produit | Écriture attend |
| --- | --- | --- |
| `file` | Flux ouvert (`FileStream`) | `FileStream` |
| `raw` | `string` (contenu entier) | `string` ou `bytes` |
| `json` | Moule anonyme, ou moule typé via `json<Train>` | Un moule |
| `xml` | `XmlNode` immuable, ou moule via `xml<Train>` | `XmlNode` ou moule |
| `custom<M, Train>` | `M`, produit par `Train` à partir des `bytes` | `M` |

`custom` sert aux formats non natifs (TOML, YAML, CSV…) : le train reçoit les octets bruts en lecture et renvoie des `bytes` en écriture. Si c'est un guard, ses échecs passent par le rail.

Tous les déclarateurs acceptent une lecture ; `const` avec `file` avertit, car un flux a un état interne mutable. Le parseur reconnaît une lecture au qualificateur de format placé juste après `<-` ; sans qualificateur, c'est une assignation.

```rgl
var config <- custom<TomlConfig, parseToml> "rgl.toml";
config -> custom<TomlConfig, serializeToml> "rgl.toml";
```

## 28. Processus enfant — `spawn`

**Pas encore implémenté.**

Lance un exécutable et renvoie `ProcessResult ! SpawnError` (`exitCode`, `stdout`, `stderr`). Chaque argument est une chaîne ou une variable `string` : un `--flag` nu est interdit. `spawn` est une instruction, jamais une expression de pipeline ou un argument. Ignorer son résultat avertit (**W060**, supprimable par `[ignore-error]`). Comme tout point de suspension, un `spawn` démarre immédiatement et n'est attendu qu'à la lecture de son résultat (§36) ; il n'existe pas de forme `async spawn`.

```rgl
spawn "cmake" "--build" ".";                         // W060

var r: spawn("timeout=5000,cwd=\"./build\"") "make" "-j4"
    !> SpawnError |> handleBuildError;

var flag: "--jobs=" + jobs as string;
spawn "ninja" flag;
```

Un processus RGL ne peut pas en relancer un autre depuis un `spawn` : la décision d'exécuter reste à l'humain. `spawn` est interdit en `--kit=light` (**E061**), une fois les kits introduits. Le C++ émis appelle `rgl::system::spawn_process`, `noexcept`, qui renvoie `std::expected<ProcessResult, SpawnError>` (runtime `rgl_process.hpp`).

Options : `timeout` (ms), `cwd`, `env` (`K=V,K=V`), `stdin` (`none`, `pipe`, `null`), `stdout` et `stderr` (`pipe`, `inherit`, `null`). Erreurs : `ExecNotFound`, `PermissionDenied`, `Timeout`, `IoError(msg)`.

## 29. Annotations `@` et métadonnées `[]`

**Pas encore implémenté.**

`@annotation` change ce que le compilateur génère ; la liste est fermée (`@deprecated`, `@tap`, `@async`, `@sync`, `@fire` — ces trois derniers au §36). `[métadonnées]` est ignoré par le compilateur et sert aux outils (IDE, doc, linter) ; toute clé est acceptée. Les deux se placent avant la déclaration.

```rgl
@deprecated("Utiliser newApi()")
[db, cache: "5min", description: "Charge le profil"]
gear fetchProfile(int id): Profile { ... }
```

**`@tap`** attache un effet de bord à un gear sans changer sa valeur. `$` y désigne la valeur courante : la sortie par défaut (`after`), l'entrée avec `before`. Les taps s'exécutent de haut en bas ; un `before` placé après un `after` avertit. La forme `closure` accepte une fonction quand une expression ne suffit pas.

```rgl
@tap(before, "→ {n}" -> console)
@tap("← {$}" -> console)
@tap(closure, after, (int val) => logger.log(val))
gear twice(int n): int => n * 2;
```

## 30. Bloc non sûr — `unsafe`

**Pas encore implémenté.**

Gear donnant accès à la mémoire brute : pointeurs `ptr<T>`, déréférencement `*expr`, prise d'adresse `&expr` (comme en C), libération `delete expr`, arithmétique de pointeurs et type `char`. Il n'utilise ni `done` ni `fail`. Un train qui contient un `unsafe` le devient implicitement.

```rgl
unsafe rawCopy(ptr<int> src): int {
    return *src;
}
```

## 31. Liaison native — `.rgl.cpp`

**Pas encore implémenté.**

Un fichier `.rgl.cpp` remplace un gear ou un train par du C++ écrit à la main. Il porte le nom exact du composant et n'implémente que lui ; le compilateur génère les en-têtes et transmet le code à Clang. C'est le niveau 2 de l'intégration C++ (§37), pour le code de colle que l'import automatique ne couvre pas. Les dépendances tierces sont des archives statiques (`.a`, `.lib`) ; `.so` et `.dll` sont interdits jusqu'à la V2.

```text
src/
    normalize.forge      // déclaration RGL
    normalize.rgl.cpp    // implémentation C++ du même composant
```

## 32. Wrappers et descriptors

**Pas encore implémenté.**

Liaisons entre API C++ et API RGL, résolues à la compilation, sans coût à l'exécution. Le C++ est à gauche de la flèche, le RGL à droite. `=>` ou `=rgl=>` introduit une expression RGL, `=cpp=>` une expression C++ où `$self` désigne l'instance et `$arg0…$argN` les arguments. Une entrée tient sur une ligne. Une méthode sans argument côté RGL devient une propriété.

**`wrapper rgl` / `wrapper std`** : API RGL sur un type intégré (`[T]`, `string`…) ou un type `std::`. Un seul wrapper par type et par niveau ; priorité : projet, puis paquet de wrappers (un seul par projet), puis stdlib. Un wrapper déclaré dans un paquet ordinaire reste interne à ce paquet.

```rgl
wrapper rgl [T] {
    push_back(T val) => push(val);           // arr.push(5)
    size() => len;                           // arr.len
    has(T val): bool =cpp=> std::find($self.begin(), $self.end(), $arg0) != $self.end();
}
```

**`descriptor`** : identité RGL complète pour un type C++ venant d'un `.rgl.cpp` ou d'un en-tête importé (constructeurs, champs, méthodes, statiques). C'est le niveau 3 de l'intégration C++ (§37) ; l'importeur peut en générer le squelette. Un `const field` interdit la réassignation côté RGL.

```rgl
descriptor rotomeca_chained_list<T> -> ChainedList<T> {
    constructor() => rotomeca_chained_list();
    field head: T? => _head;
    const field capacity: int => _capacity;
    Count(): int => length;
    static Empty(): ChainedList<T> => create();
}
```

## 33. Blueprint — `blueprint`

**Pas encore implémenté.**

Espace de noms d'opérations de pipeline, déplié à la compilation (aucune allocation ni indirection). Ses entrées sont uniquement des `=rgl=>` ou `=cpp=>`. Les paramètres génériques fixent le mode ; `#` (ou `#$`) désigne la valeur entrante du pipeline.

| Déclaration | Mode | `#` | Retour implicite |
| --- | --- | --- | --- |
| `blueprint AFan` | Statique | Interdit | Aucun |
| `blueprint AFan<Fan>` | Extension | Instance `Fan` | Aucun |
| `blueprint AFan<void, Fan>` | Fabrique | Interdit | `Fan` |
| `blueprint AFan<Fan, Fan>` | Fluide | Instance `Fan` | `Fan` |

```rgl
blueprint AFan<Fan, Fan> {
    updateSpeed(uint i) =rgl=> Fans.updateSpeed(#, i);
    boost() =rgl=> Fans.boost(#);
}

myFan |> AFan.updateSpeed(5) |> AFan.boost();
myFan |AFan> updateSpeed(10) |> boost |]> isRunning |> done;
```

`|AFan> … |]>` ouvre un bloc où les noms se résolvent d'abord dans le blueprint ; la fermeture `|]>` est obligatoire, et au-delà de 5 étapes le compilateur avertit. Si une étape change le type et que la suivante attend l'original via `#`, c'est une erreur. Interdits : macros, récursion, export depuis un paquet, déclaration de gear ou de train.

## 34. Opérateurs et précédence

Du plus lié au moins lié. Les comparaisons d'égalité s'écrivent en mots (`eq`, `not eq`), tout comme la logique. Le ternaire `cond ? a : b` se place entre la coalescence et le pipeline.

| Niv. | Opérateurs | Assoc. | Catégorie |
| --- | --- | --- | --- |
| 17 | `f()` `.` `[i]` | gauche | Appel, accès |
| 16 | `-` unaire, `not`, `invert`, `increment`, `decrement`, `post_increment`, `post_decrement` ; `*` et `&` unaires dans `unsafe` | droite | Unaires |
| 15 | `as` `as?` `as!` | gauche | Cast |
| 14 | `*` `/` `%` | gauche | Multiplicatifs |
| 13 | `+` `-` | gauche | Additifs |
| 12 | `shl` `shr` | gauche | Décalages |
| 11 | `bitand` | gauche | ET bit à bit |
| 10 | `bitxor` | gauche | OU exclusif bit à bit |
| 9 | `bitor` | gauche | OU bit à bit |
| 8 | `<` `>` `<=` `>=` | gauche | Comparaison |
| 7 | `eq` `not eq` | gauche | Égalité |
| 6 | `and` | gauche | ET |
| 5 | `xor` | gauche | OU exclusif |
| 4 | `or` | gauche | OU |
| 3 | `??` `?°` | droite | Coalescence |
| 2 | `\|>` `\|\|>` `\|!\|>` | gauche | Pipeline |
| 1 | `<-` `+<-` `-<-` `*<-` `/<-` | droite | Affectation |

```rgl
var ok: a > 0 and b not eq 3;
var nom: saisie ?? "anonyme";
score +<- 10;
```

**Coalescence** : la valeur de gauche si elle passe le test, sinon celle de droite.

| Opérateur | Garde la gauche si elle est… |
| --- | --- |
| `a ?? b` | non `null` |
| `a ?° b` | non vide (vide = `""`, `[]`, `null` ou un mold sans champ `{}`) |

`?:` n'existe pas : c'est un symbole réservé (**E076**), car `or`, `??` et `?°` le couvrent. `x?.champ` lit un champ d'un nullable : `null` si `x` l'est, et le résultat est nullable.

**Bit à bit** : `bitand`, `bitor`, `bitxor`, `shl` et `shr` s'écrivent en mots, comme la logique, car les symboles du C (`&`, `|`, `^`, `<<`, `>>`) ont déjà un sens en RGL. Ils sont plus prioritaires que les comparaisons : `a bitand b eq 0` se lit `(a bitand b) eq 0`, contrairement au C.

```rgl
var masque: flags bitand 0b0100;
var decale: 1 shl 4;               // 16
```

**Incrément et inversion** : `increment x` et `decrement x` modifient puis renvoient la nouvelle valeur ; `post_increment x` et `post_decrement x` renvoient l'ancienne valeur puis modifient. `++` et `--` sont réservés mais inutilisables (**E076**). `invert x` est une négation logique sur un `bool` et une inversion bit à bit sur un entier .

**`$`** : désigne la valeur courante dans `@tap` (§29) et dans un `case eval` (§7). En fin de train d'entrée, `$ valeur` fixe le code de sortie (§13).

```rgl
var nom: pseudo ?? "anonyme";
var age: profil?.age ?? 0;
var liste: resultats ?° [0];

increment compteur;
var ancien: post_increment compteur;
var masque: invert 0b1010;
```

## 35. Ligne de commande — `rgl`

| Commande | Effet |
| --- | --- |
| `rgl init` | Crée `rgl.json`, `main.rgl` et `src/first.forge` dans le dossier courant, sans rien écraser |
| `rgl compile <fichier>` | Compile le fichier `.rgl` indiqué : l'exécutable est écrit dans `<projet>/out/<profil>/<nom>` (`.exe` sous Windows) |
| `rgl exec <fichier>` | Compile puis lance le fichier `.rgl` indiqué ; `rgl` renvoie le code de sortie du programme |
| `rgl lsp` | Serveur de langage pour les éditeurs (JSON-RPC sur l'entrée et la sortie standard) : diagnostics, complétion avec import automatique, survol, aide aux appels, définition, indications `mut` |
| `rgl check` | **Pas encore implémenté** : bilan de l'intégration C++ du projet (§37) ; `--all` ajoutera les symboles inutilisés, `--strict` échouera s'il reste des symboles utilisés non couverts |
| `-d <dossier>` | Avec `compile` ou `exec` : prend le fichier `.rgl` du dossier (sans ses sous-dossiers) |
| `--emit-cpp` | Avec `compile` ou `exec` : s'arrête après la transpilation, le C++ est écrit dans le cache sans appel à Clang |
| `--emit-asm` | Avec `compile` ou `exec` : s'arrête après l'assembleur du C++ généré (syntaxe Intel sur x86), écrit dans le cache avec les optimisations du profil |
| `--release` | Avec `compile` ou `exec` : build de release, le plus rapide puis le plus léger ; sans lui, build de debug |
| `-- <arguments>` | Avec `exec` : arguments transmis au programme |
| `--version`, `-h`, `--help` | Version du compilateur, aide |

**Chaîne de build.** Le compilateur transpile le RGL en C++, que Clang compile : Clang vient de la chaîne Zig (`tools/zig`, installée par `dotnet run fetch-tools.cs` dans `tools/`, version fixée par `tools/toolchain.json`). Le **debug** (par défaut) compile en `-O0 -g` avec UBSan et ses messages ; la **release** privilégie la vitesse puis la taille (`-O2`, sections et code mort retirés, binaire strippé). Le C++ s'écrit sans exceptions ni RTTI, et seul le runtime que le programme utilise est compilé et lié. Une erreur de Clang n'est jamais montrée à l'utilisateur : c'est un bug du transpileur, et le détail est écrit dans `build.log` du cache.

**Traçabilité.** Le C++ généré contient des directives `#line` qui renvoient chaque instruction à sa ligne dans le fichier RGL d'origine. Une erreur de Clang, un débogueur ou un sanitizer pointe ainsi vers le source RGL et non vers le C++ intermédiaire.

Le cache se trouve dans `%LOCALAPPDATA%\rgl` (`AppData/Local/rgl`) sous Windows, pour ne pas être synchronisé avec un profil itinérant, et dans `~/.cache/rgl` sous Linux ; il ne garde que les fichiers intermédiaires (C++, assembleur, journal, runtime compilé), l'exécutable étant dans le projet.

```bash
rgl init
rgl compile main.rgl
rgl exec main.rgl
rgl exec -d src/
rgl compile main.rgl --release
rgl compile main.rgl --emit-cpp
rgl exec main.rgl -- premier second
```

`rgl init` produit :

```text
rgl.json
main.rgl
src/
    first.forge
```

```json
{
    "name": "mon-projet",
    "version": "1.0.0",
    "rgl": "0"
}
```

Clés de `rgl.json` : `name`, `version` et `rgl` sont obligatoires ; les autres sont facultatives.

| Clé | Rôle |
| --- | --- |
| `icon` | L'icône du programme compilé sous Windows : un `.ico` tel quel, ou un `.png` redimensionné ; sans clé, l'icône par défaut de RGL |
| `overflow` | `"checked"` (par défaut : un débordement d'entier arrête le programme) ou `"unchecked"` (les bits de poids fort sont perdus) |
| `recursion` | Profondeur maximale des gears récursifs : un entier (200 par défaut) ou `"infinite"` |
| `exclude` | Dossiers du projet où la recherche des fichiers `.forge` ne descend pas (`["out"]` par défaut) |

## 36. Asynchronisme — coopératif

**Implémenté en partie** : `@async`, `@fire`, `await`, `Promise<T ! E>`, `AsyncException`, `sleep`, `cancel`, `p.state`, `readAsync`, `writeAsync`, `spawnAsync`, `@sync`, `whenAll`, `whenAny` et `@task`, y compris sur des gears async génériques. Le code RGL asynchrone tourne sur un seul thread ; `@task` est le seul qui en sort.

L'asynchrone sert à ne pas bloquer le thread pendant qu'on attend (un délai, un processus, un fichier) ; ce n'est pas du parallélisme. Un seul thread exécute du code RGL : il n'y a aucune course de données, et un arrêt (débordement, index hors limites) reste déterministe. Un gear `@async` est une coroutine C++20 ; les autres gears restent des fonctions ordinaires, sans surcoût.

**`@async`.** Marque un gear (ou un emit) comme coroutine. L'appel démarre tout de suite, comme un appel ordinaire, et rend une promesse `Promise<T ! E>` ; la coroutine ne rend la main qu'à son premier `await` qui doit attendre.

```rgl
@async
gear lent(int a): int {
    await sleep(10ms);
    return a + 1;
}

@async
emit principal(): uint {
    var a: lent(1);                  // démarre ; a est une Promise<int ! AsyncException>
    var b: lent(10);                 // démarre aussi : les deux attendent en même temps
    int x: await a ?? 0;             // await donne le résultat T ! E, que ?? déballe
    int y: await b ?? 0;
    `{x} {y}\n` -> console;
    return 0;
}

!train |> principal;
```

**Le type de retour.** Le seul type visible est `Promise<T ! E>`. Un gear `@async` s'écrit avec `T`, `Promise<T>` ou `Promise<T ! E>` : `T` comme `Promise<T>` valent `Promise<T ! AsyncException>`, et l'éditeur affiche le type complet au survol. `T` peut être `void`. `E` est `AsyncException` ou un type d'erreur qui en a les champs (même nom, même type : `error Mienne { ...AsyncException; string detail; }`) ; `T` n'est pas nullable. Le corps renvoie le résultat de la promesse : `return 5;` y est `ok(5)`, `return err(…);` reste permis ; un gear sans valeur termine sans `return`.

**`AsyncException`.** Un `error` de la std (`use <Errors> only {AsyncException, AsyncKind};`) : `message`, `where` et un `kind` (`AsyncKind` : `Cancelled`, `Timeout`, `Aborted`, `IoError`), que le runtime remplit quand une promesse n'aboutit pas.

**`await`.** `await p` suspend le gear et donne le résultat de la promesse : `T ! E`, ou `E?` pour une promesse sans valeur (`null` veut dire que tout s'est bien passé). Il se lie plus fort que les opérateurs binaires (`await a + await b`) et se lit avec ses appels (`await f(x)`). Il n'est permis que dans le corps d'un gear `@async` (E128), et sur une promesse (E130). Une promesse terminée se relit : on peut attendre deux fois la même.

**Une promesse n'est jamais jetée.** Un appel `@async` écrit comme instruction, sans `await` ni variable pour la garder, est E129. Le résultat d'un `await` écrit comme instruction avertit (W063) : il peut être une erreur — sauf celui d'un `sleep`. Un `@async` qui n'attend jamais avertit (W053) : il n'a pas besoin d'être une coroutine. Une promesse que personne n'attend n'est pas détruite : la tâche continue, et le programme attend toutes les tâches avant de finir.

**`@fire`.** Un gear `void` dont l'appel lance une coroutine détachée et ne rend rien : lancer et oublier. Son type de retour est `void` (E017 sinon) ; le programme attend les tâches `@fire` encore actives avant de rendre son code de sortie.

**`cancel p;` et `p.state`.** `cancel p;` demande l'annulation de la tâche, sans l'attendre. L'annulation est coopérative : la tâche s'arrête à son prochain point de suspension (un `await`), sans exécuter une instruction de plus ; si elle attend déjà, elle s'arrête tout de suite. Ce qu'elle attendait est annulé aussi (annuler un gear qui attend un `sleep` annule le `sleep`), et celui qui l'attend reçoit `err(AsyncException{kind: Cancelled})` — ou, pour une promesse sans valeur, l'erreur seule. Annuler une tâche finie ne change rien. `cancel` est un mot contextuel : suivi d'un nom, il annule ; l'opérande n'est pas une promesse : E130. `p.state` se lit comme un champ en lecture seule et donne un `TaskState` de la std (`Ready`, `Running`, `Suspended`, `Completed`, `Cancelled`) ; une promesse n'a pas d'autre membre.

```rgl
var p: lent(1);
cancel p;
var r: await p;                      // err : kind vaut AsyncKind.Cancelled
`{p.state}` -> console;              // Cancelled
```

**`sleep(durée)`.** Une promesse sans valeur qui se termine une fois la durée écoulée, sans bloquer le thread : `await sleep(500ms);`. Un gear qui porte ce nom l'emporte, comme pour `ok` et `err`.

**`readAsync(chemin)` et `writeAsync(chemin, texte)`.** Comme `sleep`, ce sont des promesses : `var t: await readAsync('a.txt');` donne `string ! AsyncException`, `await writeAsync('a.txt', 'x');` donne `AsyncException?`. Le travail bloquant s'exécute sur un petit pool de threads du runtime, pendant que les autres tâches continuent ; le code RGL, lui, reste sur le thread de la boucle. Un échec du système est une `AsyncException` de genre `IoError`, dont `message` dit pourquoi. Pas dans un gear `pure` (E066). Un gear qui porte ce nom l'emporte. Annuler une tâche qui attend un de ces travaux l'arrête tout de suite ; le travail va à son terme, sans la reprendre. `readBytesAsync(chemin)` lit des octets (`bytes ! AsyncException`) et `writeAsync(chemin, valeur)` écrit un texte ou des `bytes`, selon ce qu'il reçoit. Les opérations sur le même fichier s'exécutent une par une, dans l'ordre où elles sont lancées (une lecture lancée après une écriture la voit, même sans `await` entre les deux), celles de fichiers différents en parallèle ; deux écritures de `a/./b.txt` et de `a\\b.txt` visent le même fichier, mais un chemin relatif et un chemin absolu ne se reconnaissent pas.

**`spawnAsync(exécutable, arguments…)`.** Comme `spawn`, mais sur le pool, sans bloquer les autres tâches : `var r: await spawnAsync('make', '-j4');` donne `ProcessResult ! AsyncException` (`use <Process> only {ProcessResult};`). Chaque élément est une chaîne ; un code de sortie non nul est un succès (`p.exitCode`) ; un exécutable introuvable ou refusé est une `AsyncException` de genre `IoError`, un délai de genre `Timeout`. Les règles de `spawn` restent : pas dans un gear `pure` (E066), pas de `rgl` (E124). `spawnAsyncWith('timeout=5000,cwd=./build', 'make', '-j4')` prend les options de `spawn` (une chaîne écrite dans le programme : `timeout`, `cwd`, `env`, `stdin`, `stdout`, `stderr`) avant l'exécutable ; un délai donne une `AsyncException` de genre `Timeout`.

**`@sync`.** Rend un gear synchrone : tout ce qui est asynchrone à l'intérieur devient synchrone, et le blocage est la responsabilité du développeur. Dans le corps d'un gear `@sync` (lambdas comprises), l'appel d'un gear `@async`, `sleep`, `readAsync`, `writeAsync`, `spawnAsync` et `whenAny` n'ont pas de promesse : ils attendent sur place et donnent directement leur résultat (`T ! E`, ce que `await` donnerait) ; un `await` y est permis et bloque de même ; une promesse déjà tenue dans une variable ou un paramètre reste une promesse, que `await` bloque. Le gear lui-même est un gear ordinaire, appelable de partout.

```rgl
@async
gear lent(int a): int { await sleep(10ms); return a + 1; }

@sync
gear bloque(int n): int {
    var r: lent(n);                  // attend ici : r est un int ! AsyncException, pas une promesse
    return r ?? 0;
}
```

L'attente fait tourner la boucle d'événements jusqu'à la fin de cette promesse : les autres tâches avancent pendant ce temps, mais le gear appelant ne reprend qu'ensuite. Appelé depuis une coroutine, il fait attendre toutes ses autres attentes : W064 le signale. `@sync` s'écrit sur un gear, un guard ou un emit ; jamais avec `@async` ni `@fire` (E125), jamais sur un gear `pure` (E018) ; le résultat d'une attente ignoré avertit (W063).

**Gears async génériques.** `@async` et `@fire` se posent sur un gear générique : `@async gear apres<T>(T valeur, duration d): T { await sleep(d); return valeur; }`. Chaque instance est une coroutine ; un paramètre `Promise<T>` ou `Promise<T ! E>` donne son `T` par déduction à partir de la promesse reçue.

**Plusieurs attendeurs.** Plusieurs tâches peuvent attendre la même promesse : la première reprend tout de suite à la fin de la tâche, les autres au tour suivant de la boucle.

**`whenAll` et `whenAny`.** `use <Async> only {whenAll};` : `whenAll<T>([Promise<T>] promises): [T ! AsyncException]` attend toutes les promesses et rend leurs résultats dans l'ordre ; une promesse qui échoue donne son erreur à sa place. `whenAny(promesses)` est connu du langage, comme `sleep` (un gear de ce nom l'emporte) : il prend un tableau de promesses de même type et rend la promesse du résultat de la première qui finit, sans annuler les autres ; un tableau vide donne une erreur de genre `Cancelled`.

**`@task`.** Un gear `@task` s'exécute sur le pool de threads à vol de travail du runtime — le seul endroit où du code RGL tourne en parallèle. Il s'écrit comme un `@async` (`T`, `Promise<T>` ou `Promise<T ! E>` ; le corps renvoie `T ! E`) : l'appel rend tout de suite une promesse, qu'on attend, annule (`cancel`), lit (`p.state`). Le corps est un gear ordinaire, sur un autre thread.

```rgl
@task
gear somme(int n): int { return n * (n + 1) / 2; }

@async
emit principal(): uint {
    var a: somme(1000);              // part sur le pool, en parallèle de la suite
    var b: somme(2000);
    `{await a ?? 0} {await b ?? 0}` -> console;
    return 0;
}
```

Règles de sûreté (E132) : les paramètres sont des copies — pas de `mut`, pas de promesse ni de flux de fichier (ils partagent un état entre threads), et une fonction seulement si son type est `pure` (une fonction qui n'est pas pure pourrait créer une promesse sur le thread du pool) ; le corps, et tout ce qu'il atteint par ses appels, ne peut ni attendre, ni lancer une coroutine, ni bloquer sur la boucle (`await`, `sleep`, `readAsync`…, un gear `@async`, `@fire`, `@sync` ou `@task`) : ils touchent à la boucle d'événements, qu'un seul thread manipule. Une tâche n'en lance donc pas d'autre : sépare le calcul dans un gear ordinaire et appelle-le depuis un gear `@async`. La console est partagée et verrouillée : deux tâches qui écrivent ne mêlent pas leurs écritures. Pas `pure` (E018), pas sur un guard ni un emit. Un `@task` n'est pas une étape de train. Un gear `@sync` peut l'appeler et attendre son résultat. Annuler une tâche déjà partie ne l'arrête pas : son travail va à son terme, sans que personne ne le reprenne.

**`task f(x)` à l'appel.** Pour exécuter un gear *ordinaire* sur le pool sans l'écrire `@task` : `var p: task carre(12);` évalue les arguments tout de suite, les copie, lance l'appel sur le pool et rend une `Promise<T ! AsyncException>` (`T` est ce que le gear renvoie). `task` est un mot contextuel : suivi d'un nom, il lance l'appel qui suit, et lie plus fort que les opérateurs binaires comme `await`. Le gear appelé ne peut être ni async, ni `@task`, ni `@sync` (« est déjà asynchrone » : appelle-le directement), ni un guard ou un emit ; il n'a pas de paramètre `mut` ; ses arguments et son résultat ne partagent pas d'état (pas de promesse ni de flux de fichier) ; et il n'atteint rien qui attende, lance ou bloque (E132, mêmes règles qu'un `@task`). Une promesse jetée est E129 ; un gear `@sync` qui l'écrit attend son résultat.

Le pool a un thread par cœur (au moins deux) ; chacun a sa file de travaux, prend les siens par le dernier arrivé et vole le plus ancien de la file d'un autre quand il n'a plus rien. `readAsync`, `writeAsync` et `spawnAsync` y passent aussi.

**Le train d'entrée.** L'emit async qui le termine fait tourner la boucle d'événements jusqu'à sa fin ; sa valeur est le code de sortie. Une erreur la termine avec le code 1, son message et sa position sur stderr.

**Les paramètres.** Une coroutine survit à l'appel qui la lance : ses paramètres sont copiés dans son cadre, jamais pris par référence (un temporaire serait détruit à la fin de l'instruction).

**Ce qui n'est pas encore permis** (« pas encore pris en charge ») : un guard async, un paramètre `mut`, `await` dans une lambda, un gear async comme étape d'un train ou d'un parcours (hors l'emit qui termine le train d'entrée), `@tap` sur un gear async. Un gear async n'est jamais `pure` (E018) : attendre est un effet.

**`@fire` et ses erreurs (E133).** Un `@fire` est détaché : personne n'attend son résultat, donc personne ne lirait l'erreur d'un `await` ignoré. Dans son corps, le résultat d'un `await` qui peut échouer (tout sauf `sleep`) doit être lu — `var r: await f(x);` puis `match` ou `??` — sinon E133. Un `@fire` n'a pas de poignée : il ne s'annule pas.

**Décisions du conseil (2026-10-26).** Fermés tant qu'aucun programme réel ne les réclame : `await` dans une lambda ou un parcours `||>` (une lambda qui attend est un gear `@async` sans nom : écris le gear) et comme étape de train — s'il fallait ouvrir quelque chose, `|> await g` serait le plus petit pas, jamais `||>` (séquentiel ou concurrent ?) ; la forme `async f()` à l'appel est abandonnée (elle ne ferait que différer l'exécution d'un tour de boucle : `async` reste réservé, avec une erreur qui renvoie vers `@async` et `task`) ; une tâche qui en lance une autre reste interdite (les promesses ne sont pas thread-safe : le parallélisme se compose depuis un gear `@async` avec plusieurs `task f(x)` puis `whenAll`), à rouvrir si un banc de `bench/` en montre le besoin ; pas de `Shared<T>` ni de `Lock<T>` (tout est copie ou déplacement : c'est ce qui rend E132 vérifiable), `Shared<T>` en lecture seule serait le premier ajout si la copie de gros tableaux vers plusieurs tâches coûtait trop cher, les canaux plutôt qu'un mutex si la communication devenait nécessaire.

**Lisibilité.** `async` s'écrit : la signature dit si un gear se suspend. Le LSP affiche le type complet d'une promesse au survol.

## 37. Intégration C++ — `forge: native`

**Pas encore implémenté : `forge: native` est refusé et `rgl check` n'existe pas encore.**

L'intégration suit trois niveaux. Le premier couvre la grande majorité des cas sans écrire de liaison ; on ne descend d'un niveau que si c'est nécessaire.

| Niveau | Mécanisme | Pour… |
| --- | --- | --- |
| 1 | Import automatique des en-têtes | Bibliothèques C, fonctions libres et structures simples du C++ |
| 2 | `.rgl.cpp` (§31) | Code de colle : templates, callbacks, API à adapter |
| 3 | `descriptor` (§32) | Classes C++ complètes : constructeurs, méthodes, champs |

**Le fichier `forge: native`** décrit une bibliothèque en un seul endroit : quels en-têtes lire, quoi lier, pour quelle plateforme.

```rgl
// fichier Raylib.forge
forge: native
description: "Raylib 5"

header: "vendor/raylib/include/raylib.h"
link: static
library: {
    linux:   "vendor/raylib/lib/libraylib.a",
    windows: "vendor/raylib/lib/raylib.lib"
}
```

**Import automatique.** Le compilateur lit les en-têtes avec le Clang embarqué et génère les liaisons RGL lui-même.

```rgl
import Raylib;

InitWindow(800, 600, "Jeu");
var pos: Vector2 {x: 10.0, y: 20.0};
```

| C / C++ | RGL |
| --- | --- |
| Types numériques, `bool` | Équivalents RGL (`int32_t` → `int`…) |
| Structure sans méthode | Moule, disposition C garantie |
| `enum` | `enum` |
| `const char*` | `string`, copiée à la frontière |
| Pointeur | `ptr<T>`, utilisable seulement dans `unsafe` |

**Règles à la frontière.**

- **Exceptions** : chaque appel natif est enveloppé par le compilateur ; une exception C++ devient une erreur `NativeError` sur le rail `!>`. Le code RGL reste sans exception.
- **Propriété mémoire** : un pointeur possédé renvoyé par une bibliothèque passe par un descriptor qui définit sa libération.
- **Asynchronisme** : un appel natif est synchrone, sauf s'il renvoie une tâche C++ ou s'il est marqué `@async` (§36).
- **Paquets** : un paquet qui utilise du natif demande la permission `cpp`.

**`rgl check`** dresse le bilan de l'intégration C++ du projet :

- **erreurs** : symboles utilisés dans le code RGL, ni convertibles automatiquement, ni couverts par un `.rgl.cpp` ou un descriptor (**E019**) ;
- **informations** (avec `--all`) : symboles non convertibles mais inutilisés.

Chaque symbole non convertible indique sa raison : template, surcharge ambiguë, fonction variadique, union, champ de bits, pointeur de fonction, propriété mémoire inconnue. Par défaut, le rapport n'échoue pas ; avec `--strict`, il renvoie un code d'erreur s'il reste des symboles utilisés non couverts (utile en intégration continue).

**Génération à la volée.** Aucune commande de génération n'est nécessaire : le serveur LSP génère les liaisons pendant l'édition et les place en cache. Ce cache est partagé avec `rgl compile`, pour que l'éditeur et la compilation voient exactement les mêmes liaisons. Il est indexé par le contenu des en-têtes, la version de Clang et les options du fichier `forge: native` ; tout changement les régénère. « Aller à la définition » sur un symbole importé ouvre sa signature RGL générée.

**Bibliothèques dynamiques : V2.** Jusque-là, seule la liaison statique est permise : `link: dynamic` lève **E020**. La V2 ajoutera la liaison dynamique (bibliothèque copiée à côté du binaire, avec un avertissement puisque le binaire n'est plus autonome), puis le chargement à l'exécution pour les plugins.

## Diagnostics

| Code | Niveau | Déclenché par | Message |
| --- | --- | --- | --- |
| E001 | Erreur | `var` | Inférence de type impossible |
| E002 | Erreur | `<-` | La variable n'existe pas |
| E003 | Erreur | `<-` | Type de la valeur différent de celui de la cible |
| E004 | Erreur | `nom: T <-` | L'indice ne correspond pas au type de la variable |
| E005 | Erreur | `<-` sur `const` | Assignation à une constante |
| E006 | Erreur | déclaration sans valeur, usage après `undefine` | Variable sans valeur (non initialisée ou libérée) |
| W001 | Avertissement | redéclaration après `undefine` | Redéclaration d'une variable libérée |
| E007 | Erreur | goto | Le cas ciblé n'existe pas |
| E008 | Erreur | goto | goto vers un case eval interdit |
| W002 | Avertissement | break N | N dépasse le nombre de switch englobants |
| E009 | Erreur | surcharge d'opérateur | Opérateur surchargé par les deux types de l'opération (ambiguïté) |
| E010 | Erreur | surcharge d'opérateur | < défini sans >, ou > sans < |
| E011 | Erreur | identificateur | Un nom de type primitif ne peut pas servir d'identificateur |
| W003 | Avertissement | `switch` | Cas non vide sans `break` : poursuite dans le cas suivant |
| W004 | Avertissement | `!>` | Rail jamais pris : derrière un gear, ou un guard qui ne peut pas échouer |
| W041 | Avertissement | SoA | Champ `[T]` imbriqué non aplatissable |
| W060 | Avertissement | `spawn` | Résultat ignoré — erreur non gérée *(pas encore implémenté)* |
| W005 | Avertissement | en-tête | `description:` absent |
| E016 | Erreur | fichier `.forge` | En-tête `forge:` absent |
| E017 | Erreur | `@fire` | Type de retour différent de `void` |
| E018 | Erreur | `@async`, `@fire` | `@async` ou `@fire` sur un gear `pure` : attendre est un effet |
| E019 | Erreur | intégration C++ | Symbole natif utilisé, non convertible et non couvert par un `.rgl.cpp` ou un descriptor *(pas encore implémenté)* |
| E020 | Erreur | `forge: native` | `link: dynamic` indisponible avant la V2 *(pas encore implémenté)* |
| E021 | Erreur | lexique | Chaîne, template ou regex non fermé (`"…"` et `'…'` s'arrêtent à la fin de ligne) |
| E022 | Erreur | lexique | Commentaire de bloc `/* … */` non fermé |
| E023 | Erreur | lexique | Caractère invalide |
| E024 | Erreur | lexique | Flottant incomplet : `1.` au lieu de `1.0` |
| E025 | Erreur | lexique | Littéral `0x`, `0b` ou `0o` sans chiffres |
| E026 | Erreur | lexique | Identificateur de plus de 255 caractères |
| E027 | Erreur | lexique | Suffixe inconnu après un nombre (durées : `ms`, `s`, `min`, `h`) |
| E028 | Erreur | fichier | Octet UTF-8 invalide |
| E029 | Erreur | fichier | Fichier encodé en UTF-16 ou UTF-32 (marque BOM détectée) : seul l'UTF-8 est accepté |
| E030 | Erreur | syntaxe | Token attendu absent : `;`, `)`, `train`, `|>`, nom de paramètre… |
| E031 | Erreur | syntaxe | Étape de pipeline attendue après `|>` ou `!>` : gear, `done`, `fail` ou `$ valeur` |
| E032 | Erreur | syntaxe | Expression attendue |
| E033 | Erreur | syntaxe | Type attendu : type primitif, nom de type ou `[T]` |
| E034 | Erreur | fichier | Contenu après le `;` du train par défaut, qui est toujours le dernier du fichier (le seul dans un `.rgl`) |
| E035 | Erreur | appel | Gear introuvable |
| E036 | Erreur | fichier d'entrée | Aucun train d'entrée dans le fichier `.rgl` |
| E037 | Erreur | train | Train sans guard non préfixé par `!` (sauf `train |> done;`) |
| E038 | Erreur | train, rail | Chemin qui ne se termine ni par `done`, ni par `fail`, ni par `$ valeur`, ni par un emit |
| E039 | Erreur | train, rail | Étape après la terminaison du chemin : jamais exécutée |
| E040 | Erreur | types | Types incompatibles (opérandes, code de sortie qui n'est pas un `uint`, entier trop grand pour un `int`) |
| E041 | Erreur | nom | Nom inconnu |
| E042 | Erreur | chaîne | Séquence d'échappement inconnue |
| E044 | Erreur | déclaration | Nom déjà déclaré dans le même bloc |
| E045 | Erreur | template | Accolade mal formée : `{` sans `}`, ou `}` seule (accolade littérale : `\{` et `\}`) |
| E046 | Erreur | `increment<X>` | Pas invalide : X doit être un entier ≥ 1 qui tient dans le type de la variable |
| E047 | Erreur | `/`, `%` | Division ou reste entier par un zéro connu à la compilation |
| E048 | Erreur | `$` | `$` hors d'un `case eval` |
| E049 | Erreur | `break`, `goto` | `break` ou `goto` hors d'un `switch` |
| E050 | Erreur | `switch` | La valeur du `switch` modifie une variable : elle doit être pure |
| E051 | Erreur | `case`, `goto` | Valeur de `case` ou cible de `goto` non connue à la compilation |
| E052 | Erreur | `switch` | Deux `case` de même valeur, ou deux `default` |
| E053 | Erreur | fichier | Instruction au niveau du fichier : variables, `if`, `switch`… s'écrivent dans un gear |
| E054 | Erreur | `const` | Constante de niveau fichier après un gear ou le train : les constantes viennent en tête |
| E055 | Erreur | `gear` | Deux gears de même nom |
| E056 | Erreur | `gear` | Un chemin du gear se termine sans `return` de valeur |
| E057 | Erreur | `pure` | Un gear `pure` appelle un gear qui ne l'est pas |
| E058 | Erreur | appel | Mauvais nombre d'arguments |
| E059 | Erreur | `return` | `return` hors d'un gear |
| E060 | Erreur | `fail`, `done`, `as!` | Hors d'un guard : seul un guard peut échouer |
| E062 | Erreur | appel | Guard ou emit appelé dans une expression : il ne s'appelle que comme étape d'un train (un guard aussi depuis un autre guard) |
| E063 | Erreur | emit | Étape après un emit : un emit termine toujours le train ou le rail |
| E064 | Erreur | train | Train préfixé par `!` alors qu'il appelle un guard |
| E065 | Erreur | `done` | Le type de retour du guard n'est pas celui de son premier paramètre |
| E066 | Erreur | `pure` | Entrée ou sortie (`-> console`, `<- console`) dans un gear `pure` |
| E067 | Erreur | emit | Emit qui termine le train d'entrée sans renvoyer `void` ni `uint` |
| E068 | Erreur | `pure` | `pure` sur un guard ou un emit |
| E069 | Erreur | `!>` | Rail hors d'un train : un gear laisse remonter les erreurs de ses appels |
| E070 | Erreur | mot réservé | Mot réservé (`continue`, `for`, `class`…) utilisé comme nom, valeur ou instruction |
| E071 | Erreur | `mold` | Deux champs de même nom (mold, type anonyme ou valeur) |
| E072 | Erreur | `mold` | Valeur par défaut d'un champ qui n'est pas connue à la compilation |
| E073 | Erreur | `mold` | Mold qui se contient lui-même, directement, par d'autres molds ou par ses spreads |
| E074 | Erreur | `.` | Champ inconnu, ou `.` sur une valeur qui n'est pas un mold |
| E075 | Erreur | `mold` | Deux molds de même nom, ou un nom de type réservé (`Exception`) |
| E076 | Erreur | symbole réservé | `++`, `--` ou `?:` : utiliser `increment`, `decrement`, `??`, `?°` ou `or` |
| E077 | Erreur | `T?` | Type nullable mal formé : `T??`, `void?`, nullable d'un nullable |
| E078 | Erreur | `T \| U`, `T ! E` | Union ou résultat mal formé : `null`, nullable ou `void` comme membre |
| E079 | — | `match` | Retiré : un match sur une autre valeur se lit comme un switch |
| E080 | Erreur | `match` | Bras qui ne correspond pas : type absent de l'union, `ok`/`err` sur une union, type sur un résultat |
| E081 | Erreur | `match` | Deux bras pour le même cas, ou deux `default` |
| E082 | Erreur | `match` | Cas non couvert, sans `default` |
| E083 | Erreur | `enum` | Enum vide, variante en double, type sous-jacent qui n'est pas un primitif |
| E084 | Erreur | `enum` | Valeurs : toutes si le type est écrit, aucune sinon ; constantes du type, sans doublon |
| E085 | Erreur | `as` | Cast impossible : types sans relation, texte vers nombre avec `as`, ingot ou union |
| E086 | Erreur | `as`, type borné | Valeur connue qui ne tient pas dans le type du cast, ou hors des bornes |
| E087 | Erreur | type borné | Type non numérique, borne inconnue à la compilation, du mauvais type ou hors du type, bornes inversées |
| E088 | Erreur | `°`, `°<-` | Appliqué à une valeur qui n'est pas un ingot |
| E089 | Erreur | `ingot` | Ingot qui repose sur lui-même, ou qui porte le nom d'un gear |
| W013 | Avertissement | `as` | Cast inutile : la valeur a déjà le type visé |
| W014 | Avertissement | `::`, `<-\|` | Déplacement impossible (constante, paramètre, variable capturée, valeur convertie) : la valeur est copiée |
| E090 | Erreur | `!>` | Rail sans crochets (`!> [ … ]`), ou après une fin de chemin |
| E091 | Erreur | `fail` | Valeur qui n'est ni un texte ni une erreur (moule avec `message` et `where`) |
| E092 | Erreur | `[T;N]`, `[T;N..M]` | Taille qui n'est pas un entier connu à la compilation, trop petite, ou bornes inversées |
| E093 | Erreur | `[K:V]` | Type de clé qui ne se compare pas ou ne se range pas (flottant, moule…) |
| E094 | Erreur | dictionnaire écrit | Deux clés égales |
| E095 | Erreur | `*` | Boucle sur autre chose qu'un guard |
| E096 | Erreur | `*` | Guard bouclé qui ne renvoie pas le type de sa première entrée |
| E097 | Erreur | `^N` | Point de reprise déjà posé dans le train, ou devant autre chose qu'un gear ou un guard à un seul paramètre |
| E098 | Erreur | `^N` | Reprise vers un point inconnu ou pas encore passé (une reprise ne revient qu'en arrière) |
| E099 | Erreur | `*<N>`, `^N<M>` | Limite de tours ou de reprises nulle |
| E100 | Erreur | intervalle | Borne ou pas qui n'est pas un entier, bornes sans type commun, pas nul, ou pas dans l'autre sens que des bornes connues |
| E101 | Erreur | `mut` | Sur une constante, sur un type qui n'est ni un tableau, ni un moule, ni un dictionnaire, sur un paramètre du train ou sur un paramètre de lambda hors d'un parcours ; gear à paramètre `mut` passé comme une lambda |
| E102 | Erreur | argument `mut` | Argument d'un paramètre `mut` qui n'est pas une variable `mut` du même type, ou passé deux fois au même appel ; parcours qui modifie sur place autre chose qu'une variable `mut`, ou après sa première étape |
| E103 | Erreur | déclaration | Un nom désigne déjà autre chose dans le fichier (trains, gears, moules, enums, ingots, constantes et imports partagent un espace de noms), plusieurs fichiers d'un `tools` déclarent le même nom, ou un nom est exporté deux fois |
| E104 | Erreur | genre de fichier | Déclaration que le genre du fichier n'accepte pas : train nommé hors d'un `forge: train`, extension hors d'un `forge: gears`, `export` dans le `.rgl`, `index` hors d'une box… |
| E105 | Erreur | fichier `.rgl` | En-tête `forge:` dans le fichier `.rgl` d'entrée |
| E106 | Erreur | en-tête | Genre de fichier inconnu après `forge:` |
| E107 | Erreur | import | Fichier introuvable, ou deux fichiers `.forge` de même nom dans un même espace de fichiers |
| E108 | Erreur | import | Imports circulaires |
| E109 | Erreur | `only` | Nom que le fichier importé ne déclare pas, ou n'exporte pas |
| E110 | Erreur | train | Appel d'un train écrit plus bas dans le fichier, ou depuis les déclarations communes |
| E111 | Erreur | train | Fins du train sans type commun |
| E112 | Erreur | train | Fichier `forge: train` sans train par défaut, ou bloc de train sans train |
| E113 | Erreur | `export` | Nom que le fichier ne déclare pas (ou que l'index d'une box n'a pas importé), ou `...Fichier` et `export Fichier;` hors de l'index d'une box |
| E114 | Erreur | générique | Argument qui ne respecte pas la contrainte de son paramètre (`number`, `error`, `{TYPE champ}`) |
| E115 | Erreur | générique | Arguments génériques en trop, paramètre qu'aucun argument ne permet de déduire, conflit de déduction, ou plus de 512 instances |
| E116 | Erreur | générique | Argument d'un paramètre de valeur qui n'est pas connu à la compilation, ou valeur là où un type est attendu (et inversement) |
| E117 | Erreur | générique | Déclaration invalide : nom de paramètre en double, valeur dans un moule, contrainte inconnue |
| E118 | Erreur | surcharge d'opérateur | Opérateur ou conversion mal déclaré : nombre de paramètres, moule absent des opérandes, retour d'`eq`, `<` ou `>` qui n'est pas un `bool`, conversion vers son propre type, doublon, paramètre `mut` |
| E128 | Erreur | `await` | `await` hors du corps d'un gear `@async` |
| E129 | Erreur | `@async` | Promesse jetée : appel d'un gear `@async` écrit comme instruction, sans `await` ni variable |
| E130 | Erreur | `await` | `await` d'autre chose qu'une promesse |
| E131 | Erreur | `Promise` | Type `Promise<…>` mal écrit : arguments, valeur nullable, ou erreur qui n'a pas les champs d'`AsyncException` |
| W063 | Avertissement | `await` | Le résultat d'un `await` est ignoré : il peut être une erreur (sauf un `sleep`) |
| W064 | Avertissement | `@sync` | Une coroutine appelle un gear `@sync` : il bloque toute la boucle d'événements |
| E132 | Erreur | `@task`, `task` | Un `@task` ou un `task f(x)` partage un état entre threads : paramètre ou argument promesse, flux de fichier ou fonction non pure, ou corps qui atteint un gear qui attend, lance ou bloque sur la boucle |
| E134 | Erreur | `params` | Paramètre `params` mal placé ou mal écrit : pas le dernier, pas un tableau `[T]` dynamique, avec `mut`, `this` ou une valeur par défaut, ou dans un train ou un opérateur |
| E133 | Erreur | `@fire` | Le résultat d'un `await` qui peut échouer est ignoré dans un `@fire` : personne ne lirait l'erreur |
| E119 | Erreur | `extend` | Nom en double, `this` d'un autre type que le type étendu, gear sans `this` ou constante sur un type qui n'est pas nommé |
| W012 | Avertissement | `switch` | Switch sur un enum, sans `default`, qui ne couvre pas toutes les variantes |
| W011 | Avertissement | `??`, `?.`, `?°` | Coalescence ou accès sûr inutile : la valeur n'est jamais `null`, jamais vide ou toujours vide |
| W010 | Avertissement | `gear` | Gear récursif : profondeur limitée par la clé `recursion` de `rgl.json` |
| W009 | Avertissement | déclaration | Variable qui masque une variable de même nom d'un bloc englobant |
| I001 | Info | conversion | Conversion numérique implicite sans perte (`int` vers `double`…) |
| W008 | Avertissement | expression | `<-` collé dans une expression : `a<-5` est lu comme `a < -5` |
| W052 | Avertissement | `@async` | Réservé : sans objet depuis que `@async` s'écrit (plus rien n'est inféré) |
| W053 | Avertissement | `@async`, `@fire` | Le gear n'attend jamais (aucun `await`, aucun `sleep`) : il n'a pas besoin d'être une coroutine |
| W054 | Avertissement | `@sync` | Le gear se serait suspendu : le thread courant est bloqué *(pas encore implémenté)* |
| W055 | Avertissement | `@sync` | Redondant : le gear était déjà synchrone *(pas encore implémenté)* |
| W006 | Avertissement | fichier | Fichier source vide |
| W007 | Avertissement | guard | Fin de corps sans `done`, `fail` ni `return` : `done` implicite |
| E015 | Erreur | train d'entrée | Signature différente de `([string] args): uint` |
| E061 | Erreur | `spawn` | `spawn` indisponible en `--kit=light` *(pas encore implémenté)* |
| E013 | Erreur | `<-` sur un champ ou un élément | Modification en place du contenu d'une variable qui n'est pas `mut`, ou de ce qui n'est pas un contenu (`a.len`) |
| E014 | Erreur | ajout dans un tableau | Ajout dans un tableau reçu en paramètre sans `mut` |


## À retravailler

Ces blocs existent dans la spec v15 mais seront repris avant d'entrer dans ce document.

- **Contrats** : interface qu'une box doit respecter (`forge: contract`, `train<Contrat>`), avec l'annotation de cycle de vie `: T -> train` qui appelle automatiquement un train quand la valeur sort de portée.
