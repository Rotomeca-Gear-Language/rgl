# RGL — Guide du langage et de la bibliothèque standard

> État du projet au 27 octobre 2026.
>
> Ce document décrit les fonctionnalités présentes dans le compilateur et la bibliothèque standard. Les fonctionnalités prévues sont regroupées dans la dernière partie.
>
> La correction des intentions de déplacement — consommation de la source même lorsqu’une copie est nécessaire — est disponible sur `fix/consume-move-source`, mais n’est pas encore fusionnée dans `main`.

## 1. Présentation

RGL est un langage à typage statique, compilé vers du C++, puis vers un exécutable natif.

Son compilateur est écrit en C# et utilise actuellement .NET 10. La compilation native passe par la chaîne Zig et son compilateur C++.

Le langage s’organise autour de plusieurs éléments :

|Élément|Rôle|
|---|---|
|`gear`|Fonction réalisant une transformation ou une opération|
|`guard`|Transformation pouvant échouer|
|`emit`|Étape terminale d’un train|
|`train`|Enchaînement d’étapes|
|`mold`|Type composé de champs|
|`error`|Type représentant une erreur|
|`ingot`|Type distinct construit sur un autre type|
|`dataset`|Table de données dont les entrées possèdent des identifiants typés|
|`precast`|Donnée constante résolue à la compilation|

RGL permet notamment de développer des programmes en ligne de commande, de manipuler des collections, de traiter des fichiers et d’exécuter des tâches asynchrones.

Il possède dans la bibliothèque standard un serveur HTTP/1.1 concurrent, un client HTTP/HTTPS, TLS et des routes WebSocket.

## 2. Premier programme

Un programme complet peut se terminer par un `emit` qui renvoie son code de sortie :

```
emit principal(): uint {
    std.writeLine('Bonjour RGL !');
    return 0;
}

!train |> principal;
```

Dans cet exemple :

- `principal` est l’étape terminale ;
- `std.writeLine` écrit une ligne sur la console ;
- `0` indique que le programme s’est terminé correctement ;
- `!train` déclare un train sans guard.

Les exemples des sections suivantes sont généralement des extraits à placer dans le corps d’un gear ou d’un emit. Les déclarations de types et de fonctions restent au niveau du fichier.

### Commandes principales

```
rgl init
rgl init --web
rgl compile main.rgl
rgl exec main.rgl
rgl check
rgl compile main.rgl --release
```

|Commande ou option|Effet|
|---|---|
|`rgl init`|Crée un projet (`rgl.json`, fichier d’entrée)|
|`rgl init --web`|Crée un serveur HTTP composé par pipeline et une page `src/Home.paper` ; `rgl exec -d .` l'écoute sur le port 8080|
|`rgl compile <fichier>`|Compile et écrit l’exécutable dans `out/debug` ou `out/release`|
|`rgl exec <fichier>`|Compile puis exécute, en propageant le code de sortie|
|`rgl check`|Vérifie le projet|
|`rgl doc check` / `rgl doc build`|Vérifie ou génère la documentation (section 24)|
|`rgl lsp`|Serveur de langage utilisé par l’extension VS Code|
|`-d <dossier>`|Dossier du projet|
|`--release`|Profil Release (vitesse puis taille) ; Debug par défaut|
|`--emit-cpp`|S’arrête après l’écriture du C++|
|`--emit-asm`|S’arrête après la génération de l’assembleur|

Pour transmettre des arguments au programme :

```
rgl exec main.rgl -- premier second
```

## 3. Syntaxe générale

Les instructions se terminent par `;` et les blocs utilisent des accolades.

```
gear exemple(): void {
    int nombre: 5;

    if (nombre > 0) {
        std.writeLine('Positif');
    }
}
```

### Commentaires

```
// Commentaire sur une ligne.

/*
   Commentaire sur plusieurs lignes.
*/

/// Documentation de la déclaration suivante.
gear exemple(): void {}
```

Contrairement à JavaScript ou C#, RGL utilise :

- `:` pour initialiser une variable ;
- `<-` pour réaffecter une variable ;
- `eq` pour tester l’égalité.

```
int nombre: 5;
nombre <- 10;

bool identique: nombre eq 10;
bool different: nombre not eq 5;
```

## 4. Variables et constantes

### Déclaration avec type explicite

```
int age: 25;
string nom: 'Alice';
bool actif: true;
```

### Déclaration avec inférence du type

`var` demande au compilateur de déduire le type depuis la valeur initiale.

```
var age: 25;
var nom: 'Alice';
var actif: true;
```

L’inférence ne rend pas la variable dynamique : son type reste déterminé à la compilation.

```
var nombre: 10;

// Erreur : nombre est un int.
// nombre <- 'bonjour';
```

Certaines valeurs ne suffisent pas à déduire un type :

```
// Erreur E001 : type impossible à déduire.
// var inconnu: null;

// Le type explicite résout le problème.
int? nombre: null;

// Un tableau vide nécessite aussi un type.
[int] valeurs: [];
```

### Réaffectation

Une variable ordinaire peut recevoir une nouvelle valeur du même type :

```
int compteur: 0;

compteur <- 1;
compteur +<- 2;
compteur *<- 3;
```

Pour incrémenter ou décrémenter :

```
increment compteur;
decrement compteur;
increment<5> compteur;
```

Les opérateurs `++` et `--` sont réservés et ne remplacent pas ces instructions.

### Constantes

Une constante ne peut pas être réaffectée.

```
const int MAXIMUM: 100;

// Erreur E005.
// MAXIMUM <- 200;
```

Une constante ne peut pas être déclarée `mut` :

```
// Erreur E101.
// mut const [int] VALEURS: [1, 2, 3];
```

### Libération d’un nom

`undefine` rend une variable inutilisable :

```
var valeur: 42;

undefine valeur;

// Erreur E006.
// var copie: valeur;
```

Le nom peut ensuite être redéclaré, éventuellement avec un autre type. Cette redéclaration émet W001.

```
string valeur: 'nouvelle variable';
```

## 5. Types et valeurs

Le langage possède notamment les types suivants :

|Famille|Types|
|---|---|
|Entiers signés|`byte`, `short`, `int`, `long`|
|Entiers non signés|`ubyte`, `ushort`, `uint`, `ulong`|
|Flottants|`float`, `double`|
|Booléens|`bool`|
|Texte|`string`|
|Symboles|`symbol`|
|Octets|`bytes`, correspondant à un tableau de `ubyte`|
|Temps|`duration`, `datetime`|
|Expressions régulières|`regex`|
|Flux de fichier|`FileStream`|
|Absence de résultat|`void`|

Des types composés complètent ces types primitifs : tableaux, dictionnaires, tuples, molds, enums, ingots, unions, résultats et promesses.

### Chaînes de caractères

Les chaînes peuvent utiliser des guillemets simples ou doubles :

```
string premier: 'Bonjour';
string second: "Bonjour";
```

Les templates utilisent des accents graves et permettent l’interpolation :

```
string nom: 'Alice';
int age: 25;

string message: `{nom} a {age} ans.`;
```

La longueur d’une chaîne compte des caractères Unicode, pas des octets :

```
var taille: 'bonjour'.length;
var premiereLettre: 'bonjour'[0];
var derniereLettre: 'bonjour'[-1];
```

Un accès à un caractère produit une chaîne d’un caractère.

### Symboles

Le littéral `§nom` produit un `symbol` interné, comparé par identité et sans allocation.

### Conversions

```
int entier: 42;
double decimal: entier as double;
```

Trois formes de conversion existent :

|Forme|Comportement|
|---|---|
|`as`|Conversion autorisée entre les types concernés|
|`as?`|Conversion avec résultat nullable|
|`as!`|Conversion contrôlée pouvant faire échouer un guard|

```
int? nombre: '123' as? int;
int valeur: nombre ?? 0;
```

Une conversion forcée avec `as!` s’emploie dans un guard. Une conversion texte → nombre s’écrit uniquement `as?` ou `as!`. Un flottant converti en entier est tronqué vers zéro et arrête le programme s’il sort de l’intervalle.

La clé `overflow` de `rgl.json` (`checked` par défaut, ou `unchecked`) choisit le comportement des débordements entiers.

## 6. Mutabilité

`mut` autorise la modification du **contenu** d’un tableau, d’un dictionnaire ou d’un mold.

```
[int] lectureSeule: [0, 1, 2, 3];
mut [int] modifiable: [0, 1, 2, 3];

// Erreur E013.
// lectureSeule[0] <- 5;

modifiable[0] <- 5;
```

Sans `mut`, une variable reste réaffectable :

```
[int] valeurs: [1, 2];
valeurs <- [3, 4];
```

Le remplacement de la valeur entière et la modification de son contenu sont deux opérations différentes.

Dans l’implémentation actuelle, `push` et `add` sur une variable locale ne nécessitent pas non plus `mut`. Les règles sont plus restrictives pour les paramètres et les contenus imbriqués.

### Copie

Une variable `mut` peut être copiée :

```
mut [int] original: [1, 2, 3];
mut var copie: original;

copie[0] <- 99;

// original reste [1, 2, 3].
```

Les types à cycle de vie suivent une règle particulière : leur copie est interdite.

### Paramètres `mut`

Un paramètre `mut` permet de modifier la variable de l’appelant :

```
gear remplacerPremier(mut [int] valeurs): void {
    valeurs[0] <- 99;
}
```

Appel :

```
mut [int] nombres: [1, 2, 3];

remplacerPremier(nombres);
```

L’argument doit être une variable `mut` du type exact attendu (E102 sinon, et aussi si la même variable est passée deux fois). Un gear ayant un paramètre `mut` ne peut pas être `pure`. L’éditeur affiche `mut` devant l’argument.

## 7. Intentions de déplacement

RGL possède deux syntaxes exprimant une intention de déplacement :

| Syntaxe | Usage                               |
| ------- | ----------------------------------- |
| `::`    | Déclaration d’une nouvelle variable |
| `<-\|`  | Assignation                         |

### À la déclaration

```
mut [int] source: [1, 2, 3];
var destination:: source;

// source n’est plus utilisable.
```

La destination n’a pas besoin d’être `mut`. Sa mutabilité détermine seulement les modifications autorisées ensuite.

```
mut [int] source: [1, 2, 3];
mut var destination:: source;

destination[0] <- 10;
```

### À l’affectation

```
[int] source: [1, 2, 3];
[int] destination: [];

destination <-| source;
```

La syntaxe suivante n’est pas une déclaration valide :

```
// Erreur E030.
// var destination <-| source;
```

### Lorsque le déplacement nécessite une copie

La règle corrigée conserve l’intention de transfert : si le compilateur doit copier la valeur, il émet W014, mais le nom source est tout de même libéré.

```
const int source: 5;

var destination:: source; // W014 : copie nécessaire.

// Erreur E006.
// var encore: source;
```

Pour conserver l’accès à la source, utiliser `:` ou `<-`.

## 8. Conditions et sélection

### `if` et `else`

```
gear categorie(int age): string {
    if (age < 18) {
        return 'mineur';
    } else {
        return 'majeur';
    }
}
```

Les conditions doivent être des booléens.

### Expression conditionnelle

```
string categorie: age < 18 ? 'mineur' : 'majeur';
```

### `switch`

```
switch (niveau) {
    case 1:
        std.writeLine('Débutant');
        break;

    case 2, 3:
        std.writeLine('Intermédiaire');
        break;

    default:
        std.writeLine('Autre');
        break;
}
```

Un cas non vide qui continue dans le suivant sans `break` ni `goto` produit W003.

`break N`, `break all` et `goto` vers un cas sont également disponibles. `case eval $ > 10:` teste une condition où `$` est la valeur.

### `match`

`match` produit une valeur :

```
string nom: match (niveau) {
    case 1 |> 'débutant';
    case 2, 3 |> 'intermédiaire';
    case eval $ > 10 |> 'grand';
    default |> 'autre';
};
```

Sans `default`, tous les cas doivent être couverts : toutes les variantes d’un enum, ou `true` et `false` (E082 sinon).

Il permet aussi de sélectionner selon le type d’une union ou l’état d’un résultat.

## 9. Gears

Un gear correspond au rôle d’une fonction dans d’autres langages.

```
gear additionner(int a, int b): int {
    return a + b;
}
```

Une forme courte existe :

```
gear additionner(int a, int b): int => a + b;
```

Appel :

```
int resultat: additionner(2, 3);
```

### Gears purs

Un gear `pure` n’effectue pas d’entrée-sortie et n’appelle pas de gear impur.

```
gear pure doubler(int valeur): int => valeur * 2;
```

### Paramètres par défaut

```
gear saluer(string nom, string prefixe: 'Bonjour'): string =>
    `{prefixe} {nom}`;
```

```
var premier: saluer('Alice');
var second: saluer('Alice', 'Salut');
```

La valeur par défaut est une expression évaluée dans le gear appelé : elle peut lire les paramètres qui la précèdent. Les valeurs spéciales `__CALLER__`, `__WHERE__`, `__FILE__` et `__LINE__` sont remplies chez l’appelant et décrivent son contexte.

### Paramètres multiples

`params` rassemble les arguments supplémentaires dans un tableau :

```
gear compter(params [int] valeurs): uint => valeurs.length;
```

```
var aucun: compter();
var plusieurs: compter(1, 2, 3);
var tableau: compter([4, 5]);
```

Le paramètre `params` doit être le dernier (E134 sinon).

### Génériques

```
gear pure identite<T>(T valeur): T => valeur;
```

Les paramètres génériques peuvent avoir des contraintes :

```
gear pure somme<T: number>(T a, T b): T => a + b;
```

Les contraintes disponibles sont `number`, `error` et un mold anonyme (`<T: {int x}>`). Un paramètre peut aussi être une valeur (`<int max>`).

Le compilateur peut déduire les arguments génériques depuis les arguments de l’appel, ou ils s’écrivent explicitement : `identite<int>(5)`. Chaque combinaison d’arguments est compilée séparément ; un générique jamais utilisé n’est pas lié.

`T is X` (types égaux) et `T accept X` (au moins les champs de `X`) sont des tests de compilation : un `if` dont la condition n’est faite que de tels tests ne compile que la branche prise.

Seuls les `gear` et les `mold` sont génériques : un `guard` ou un `emit` générique n’est pas encore pris en charge.

## 10. Trains, guards et emits

Un train enchaîne des opérations avec `|>`.

```
gear pure doubler(int valeur): int => valeur * 2;

emit afficher(int valeur): uint {
    std.writeLine(valeur);
    return 0;
}

!train 21 |> doubler |> afficher;
```

Le programme affiche `42`.

Une valeur transmise par un pipeline devient le premier argument de l’étape suivante. Des arguments supplémentaires peuvent être fournis à cette étape.

Une lambda peut remplacer directement un gear dans un pipeline. Elle reçoit exactement la valeur courante :

```rgl
gear calcul(): int => 10 |> (n) => n * 4 |> (n) => n + 2;
!train 20 |> (n) => n * 2 |> afficher;
```

Cette étape a la sémantique d’un `gear` : elle transforme la valeur et la transmet à la suite. Elle n’est ni un `guard` ni un `emit`, et ne porte donc pas de rail `!>` ni de terminaison du train.

Un train peut aussi être nommé et appelé comme un gear depuis un autre fichier (`forge: train`, section 21).

### Guards

Un guard est une transformation pouvant échouer :

```
guard verifier(int valeur): int {
    if (valeur < 0) {
        fail 'La valeur doit être positive ou nulle.';
    }

    done;
}
```

Ici, `done;` renvoie la valeur reçue. `fail;`, `fail 'texte';` et `fail valeur;` (une valeur d’erreur) font échouer le guard.

### Rails de traitement des erreurs

Un rail `!> [ ... ]` prend en charge l’échec d’une étape :

```
use <Errors> only {Exception};

guard verifier(int valeur): int {
    if (valeur < 0) {
        fail 'Valeur négative.';
    }

    done;
}

emit signaler(Exception erreur): uint {
    std.writeLine(erreur.message);
    return 1;
}

emit afficher(int valeur): uint {
    std.writeLine(valeur);
    return 0;
}

train -2
    |> verifier !> [signaler]
    |> afficher;
```

Un chemin se termine par `done`, `fail`, `$ valeur` ou un emit. Un `fail` seul qui suit une valeur d’erreur (dans un rail) renvoie cette erreur.

Le langage possède aussi :

- les **boucles de guard** : `g*` (sans limite, on sort par `done` ou `fail`), `g*<3>` (au plus 3 tours, passe la dernière valeur) et `g*<!3>` (échoue avec `LoopError` au-delà) ;
- les **points de reprise** : `^1 g` marque l’entrée d’une étape, `^1`, `^1<3>` et `^1<!3>` y reviennent, jamais vers l’avant.

### Arrêt immédiat avec `panic`

```
gear verifierConfiguration(bool valide): void {
    if (not valide) {
        panic 'Configuration invalide.';
    }
}
```

`panic` arrête le programme. Contrairement à `fail`, il ne passe pas par un rail de récupération.

## 11. Molds et valeurs structurées

Un mold ressemble à une structure ou à un type de données composé :

```
mold Point {
    int x;
    int y;
}
```

Création :

```
Point position: {x: 10, y: 20};
```

Accès :

```
int horizontal: position.x;
```

Modification :

```
mut Point position: {x: 10, y: 20};
position.x <- 15;
```

Un mold suit un typage structurel : une valeur qui a plus de champs que prévu est acceptée et ne garde que les champs attendus ; un champ manquant doit avoir un défaut (un champ nullable vaut `null`). Un type anonyme s’écrit `{int x, int y}`.

### Copie avec modifications

`with` construit une nouvelle valeur sans modifier l’original :

```
Point origine: {x: 0, y: 0};
var deplace: origine with {x: 10};
```

### Composition

Un mold peut reprendre les champs d’un autre mold :

```
mold Position {
    int x;
    int y;
}

mold Personnage {
    ...Position;
    string nom;
}
```

Les champs peuvent également avoir des valeurs par défaut connues à la compilation (`int niveau: 1;`).

### Molds génériques

```
mold Paire<T, U> {
    T premier;
    U second;
}

Paire<int, string> p: {premier: 1, second: 'a'};
```

Les arguments peuvent se déduire du type attendu ou des champs donnés (`new Paire {…}`).

### Opérateurs, conversions et index

Un mold déclare dans son corps des opérateurs et des conversions :

```
mold Vec2 {
    double x;
    double y;

    pure Vec2 +(Vec2 a, Vec2 b) { return {x: a.x + b.x, y: a.y + b.y}; }
    pure Vec2 *(Vec2 a, double k) { return {x: a.x * k, y: a.y * k}; }
    implicit Vec2(double v) { return {x: v, y: v}; }
}
```

- opérateurs : `+ - * / % eq < > bitand bitor bitxor shl shr` (et `-` unaire) ;
- `not eq` se déduit de `eq`, `<=` de `<` et `eq`, `>=` de `>` et `eq` ; `<` sans `>` est E010 ;
- `implicit` s’applique automatiquement partout où un type est attendu, `explicit` seulement avec `as` ;
- un opérateur n’est `pure` que s’il l’écrit ;
- un mold peut déclarer `pure T [P i] { … }` (lecture par `m[i]`) et `mut T [P i] { … }` (écriture `m[i] <- v`, qui exige une variable `mut`).

Plusieurs opérateurs applicables sont E009.

### Erreurs personnalisées

```
error NonTrouve {
    string nom;
}
```

Un `error` est un mold qui reçoit les champs `message` et `where`. Tout mold qui possède `string message` et `string where` est une erreur.

## 12. Enums et ingots

### Enums

```
enum Element {
    Feu,
    Eau,
    Terre,
}
```

```
Element element: Element.Feu;
```

Les enums servent à représenter un ensemble fini de variantes nommées. Elles peuvent avoir un type sous-jacent et des valeurs (`enum Code: int { A: 1, B: 2 }`, valeurs pour toutes les variantes ou aucune). Une variante peut s’écrire seule dans un `case` d’un switch sur l’enum.

### Ingots

Un ingot crée un type distinct, fondé sur un autre type :

```
ingot UserId: int;
```

```
UserId utilisateur: UserId(42);
```

L’opérateur `°` donne accès à la valeur sous-jacente :

```
int valeur: utilisateur°;
```

Seule une constante entre implicitement dans un ingot. Un calcul entre ingots garde la marque.

### Types bornés

```
int<0..100> pourcentage: 75;
```

Une valeur hors des limites est refusée à la compilation lorsqu’elle est connue (E086), sinon contrôlée à l’exécution.

## 13. Nullables, unions et résultats

### Nullables

`T?` représente une valeur de type `T` ou `null`.

```
int? nombre: '123'.toInt();
int valeur: nombre ?? 0;
```

`??` fournit une valeur de remplacement, `?°` une valeur « vide » (chaîne, tableau).

L’accès sûr `?.` permet de consulter un membre lorsque la valeur peut être nulle.

Le compilateur suit les tests : après `if (x not eq null)`, `x` s’utilise comme un `T`.

### Unions

Une union accepte plusieurs types :

```
int | string valeur: 'bonjour';
```

```
string description: match (valeur) {
    int nombre |> `Nombre : {nombre}`;
    string texte |> `Texte : {texte}`;
};
```

Un `match` doit couvrir tous les cas possibles ou fournir une branche par défaut adaptée.

### Résultats

`T ! E` représente une réussite ou une erreur :

```
int ! string resultat: ok(42);
int ! string echec: err('Impossible');
```

```
string message: match (resultat) {
    ok valeur |> `Résultat : {valeur}`;
    err erreur |> `Erreur : {erreur}`;
};
```

## 14. Tableaux, dictionnaires et tuples

### Tableaux

```
[int] nombres: [10, 20, 30];

var premier: nombres[0];
var dernier: nombres[-1];
var taille: nombres.length;
```

Les indices négatifs comptent depuis la fin.

Plusieurs formes de tailles sont disponibles :

|Type|Sens|
|---|---|
|`[T]`|Tableau de taille variable|
|`[T;N]`|Tableau de taille fixe|
|`[T;N..M]`|Tableau dont la taille est bornée|
|`[T+]`|Tableau non vide|

Un accès hors limites n’est pas silencieusement accepté : il arrête le programme, ou fait échouer le guard.

### Dictionnaires

```
mut [string:int] scores: [
    'Alice': 10,
    'Bob': 20
];

scores['Alice'] <- 15;
scores.add('Charlie', 30);

bool present: scores.has('Alice');
int score: scores.getOr('Inconnu', 0);
```

Les clés doivent posséder un type dont l’ordre est pris en charge (entier, bool, string, symbol, enum, ingot de ceux-ci).

### Tuples

```
(int, string) resultat: (42, 'réponse');

int nombre: resultat.Item1;
string texte: resultat.Item2;
```

Les éléments peuvent aussi être nommés : `(int x, int y)`.

### Décomposition par étalement

```
[int] debut: [1, 2];
[int] suite: [...debut, 3, 4];
```

Le même principe d’étalement existe pour les valeurs structurées.

## 15. Lambdas, parcours et accumulations

Une lambda est une fonction écrite comme une expression :

```
var doubler: (int valeur) => valeur * 2;
```

Son type s’écrit `(int) => int`, avec `pure` si elle est pure. Elle capture par copie. Lorsqu'un parcours `mut` modifie le contenu de cette copie, la closure conserve la modification entre ses appels ; la variable extérieure reste indépendante.

### Transformer une collection

```
[int] nombres: [1, 2, 3];
var doubles: nombres ||> (valeur) => valeur * 2;
```

Une lambda de parcours peut également recevoir le rang :

```
var resultats: nombres ||> (valeur, rang) => valeur + rang;
```

Le langage possède également la variante de parcours stricte `|!|>`, dont les échecs se propagent.

Les parcours à la suite se combinent en une seule boucle. Une lambda de parcours peut modifier sur place le contenu d’une variable `mut` qu’elle capture : `liste ||> (mut p) => …`.

### Accumuler

```
int total: [1..5] |+> 0 with (acc, valeur) => acc + valeur;
```

L’intervalle `[1..5]` inclut ses deux bornes : le résultat est `15`. `[1..<5]` exclut la fin.

Un pas peut être précisé :

```
var intervalle: [0..10 by 2];
```

Dans une lambda d’accumulation, `done valeur;` permet d’arrêter le parcours avec un résultat. L’accumulation prend `(acc, x)` ou `(acc, x, rang)`.

### Utiliser les extensions de la std

```
[int] nombres: [1, 2, 3, 4, 5];

var pairs: nombres.filter((valeur) => valeur % 2 eq 0);
var premierGrand: nombres.find((valeur) => valeur > 3);
var total: nombres.sum();
```

## 16. Extensions

Une extension ajoute des opérations à un type sans modifier sa définition. Le C++ généré ne change pas : un gear d’extension est un gear ordinaire.

```
extend Point {
    const Point ORIGINE: {x: 0, y: 0};

    gear pure longueurManhattan(this Point point): int =>
        std.abs(point.x) + std.abs(point.y);
}
```

Appel :

```
Point position: {x: 3, y: 4};
int longueur: position.longueurManhattan();
```

- `this Type nom` désigne la valeur ; `v.f(a)` est exactement `f(v, a)` ;
- un gear sans `this` et une constante s’appellent sur le type : `Point.ORIGINE` ;
- une extension d’un type déclaré dans le même fichier suit ce type ;
- les extensions génériques sont prises en charge : `extend<T: number> [T] { gear pure somme(this [T] v): T … }` ;
- les extensions d’un fichier `forge: extend` s’appliquent à tout le programme, sans import ; dans un fichier `forge: gears`, elles restent locales ;
- un namespace (`namespace Mon/Espace;`, un par fichier) départage des extensions de même nom : `v.<Mon/Espace>f()` ;
- deux extensions qui donnent le même nom au même type sont E119 à l’appel.

## 17. Datasets

Un dataset est une table de données dont les entrées ont un nom et un identifiant typé.

```
mold Classe {
    int attaque;
    int defense;
}

dataset Classes of Classe {
    guerrier: {attaque: 50, defense: 40},
    mage: {attaque: 20, defense: 10},
}
```

Utilisation :

```
Classes.id choix: Classes.guerrier;

var fiche: choix.get();
var nom: choix.name();
var nombreDeClasses: Classes.length;
```

Le type `Classes.id` évite de confondre un identifiant de classe avec un identifiant d’un autre dataset. Un identifiant est la position de l’entrée ; un identifiant faussé par un calcul arrête le programme à `get()`.

Le langage permet également :

- de lire les entrées comme un mold déclaré (`of Classe`, vérifié) ou de créer le mold depuis la première entrée (`as Classe`) ;
- de charger les données depuis un fichier JSON avec `from` : `dataset Classes from "classes.json" of Classe;` (le fichier se trouve par son nom ; objet de noms d’entrées ou tableau d’objets avec un champ `id`) ;
- de rechercher une entrée par son nom avec `Classes.find('mage')`, qui renvoie `Classes.id?` ;
- de référencer un autre dataset par un champ `Classes.id job;` (un texte du fichier JSON prend alors le sens du type du champ, y compris une variante d’enum) ;
- de donner un identifiant dans un champ de mold, un tableau `[Moves.id]`…

Les entrées ne peuvent pas porter le nom d’un membre (`id`, `length`, `get`, `find`, `name`).

L’extension VS Code possède un éditeur de dataset sous forme de tableau (CodeLens « Éditer le dataset »).

## 18. Console, fichiers et formats

### Console

```
std.write('Nom : ');
string nom: std.readLine();

std.writeLine(`Bonjour {nom}`);
```

La syntaxe d’entrée-sortie directe existe aussi :

```
'Bonjour' -> console;
var ligne <- console;
```

### Fichiers texte et octets

Les lectures et écritures synchrones de fichiers s’effectuent dans un guard (E060 ailleurs) ; un échec est une `IoError` :

```
guard chargerTexte(string chemin): string {
    var texte <- raw chemin;
    return texte;
}
```

```
guard sauvegarderTexte(string texte): string {
    texte -> raw 'sortie.txt';
    done;
}
```

Pour des données binaires, dans un guard également :

```
bytes contenu <- raw 'image.bin';
```

`texte as bytes` est toujours possible ; `octets as? string` renvoie `null` si l’UTF-8 est invalide et `octets as! string` fait échouer le guard.

### Formats pris en charge

|Format|Usage|
|---|---|
|`raw`|Texte UTF-8 ou octets|
|`json`|Lecture et écriture JSON typées|
|`xml`|Lecture et écriture via `XmlNode`|
|`custom<M, transformation>`|Format personnalisé par une transformation RGL|
|`file`|Flux de fichier|

### JSON

```
Config c <- json 'a.json';
var d <- json<Config> 'a.json';
var p <- json<{int x, string nom}> 'a.json';
c -> json 'b.json';
```

Un champ absent prend le défaut du mold, un champ en trop est ignoré. Une valeur qui ne convient pas est une `ParseError` avec le chemin de la valeur (`$.chemin[1].y`). L’écriture est indentée de deux espaces.

### XML

```
XmlNode n <- xml 'a.xml';
n -> xml 'b.xml';
var c <- xml<lireConfig> 'a.xml';
```

`xml<train>` appelle un train de conversion entre `XmlNode` et votre type.

### Format personnalisé

`custom<M, train>` est du sucre pour un appel ordinaire : en lecture, le train reçoit les `bytes` du fichier et renvoie un `M` ; en écriture il reçoit `M` et renvoie des `bytes`.

### Données importées à la compilation

```
import json reglages;
import xml mise_en_page;
import file "page.html" as page;
```

Les fichiers sont lus à la compilation et cherchés par leur nom. JSON et XML produisent une constante structurée dont le type est déduit. `import file` accepte un nom complet avec son extension et place son contenu UTF-8 exact dans une constante `string` portant le nom écrit après `as`. La constante n’est écrite dans le C++ que si le programme la lit.

### Flux de fichier

```
var f <- file 'a.txt';
var g <- file<write> 'b.txt';
var h <- file<append> 'c.txt';
```

Le type de flux est `FileStream`. Les opérations, dans un guard (sauf `close`), sont :

- `readLine()` : `string?`, `null` en fin de fichier ;
- `readBytes(n)` : jusqu’à `n` octets, aucun en fin de fichier ;
- `write(texte)` ou `write(octets)` ;
- `close()`.

Il n’existe pas de propriété `eof` : testez le `null` de `readLine()`.

Le flux se ferme tout seul quand sa dernière copie disparaît, ou tout de suite avec `close()` pour toutes les copies. Les copies d’un flux partagent sa position : ce comportement diffère de celui d’un tableau ordinaire. Un `const` de flux avertit (W016).

## 19. Durées, dates et regex

### Durées

```
duration attente: 250ms;
duration delai: 2s;
duration total: attente + delai;
```

Les suffixes disponibles sont `ms`, `s`, `min` et `h`. Une durée est un entier de millisecondes sur 64 bits.

Les opérations comprennent les comparaisons, l’addition et la soustraction, la multiplication et la division par un entier, et la division de deux durées (un `long`). Un débordement arrête le programme. `d as int` lit les millisecondes.

### Dates

Une `datetime` représente un instant en UTC, sans fuseau et sans littéral.

```
var maintenant: std.now();
var date: std.parseDateTime('2026-10-27T10:30:00Z');
```

Une date peut être construite depuis ses composants :

```
var rendezVous: std.dateTime(2026, 10, 27, 10, 30);
```

Les entrées invalides produisent `null`. `date ± durée` et `date - date` (une durée) sont permis, jamais `date + 5` (écrire `5ms`). Une date s’écrit en texte en ISO 8601 UTC. `<- clock` lit l’instant présent hors d’un gear `pure`.

### Expressions régulières

```
regex chiffres: /\d+/g;

bool contientNombre: chiffres.test('RGL 42');
var nombres: chiffres.find('10 puis 20');
```

Flags : `g` (toutes les occurrences), `i` (casse ASCII), `m` (lignes). Méthodes disponibles :

|Méthode|Résultat|
|---|---|
|`test(texte)`|Présence d’une correspondance|
|`match(texte)`|Correspondance et groupes (`[string]?`), ou `null`|
|`find(texte)`|Correspondances trouvées|
|`replace(texte, remplacement)`|Texte transformé (`$0` à `$9`, `$$`)|

Les regex sont écrites comme des littéraux, et non construites depuis une chaîne connue seulement à l’exécution. Le motif est vérifié à la compilation (E127). Le texte est lu en UTF-8 ; `\w`, `\d`, `\s` et `i` ne connaissent que l’ASCII.

## 20. Processus enfants

```
spawn 'make' '-j4';
var r: spawn('timeout=5000,cwd=./build') 'make';
```

`spawn` lance un exécutable, attend sa fin et donne un `ProcessResult ! SpawnError` (section 25.8). Chaque élément est une chaîne, jamais un drapeau nu (`'-j4'`). Les options sont une chaîne littérale `clé=valeur` : `timeout` (ms), `cwd`, `env`, `stdin`, `stdout`, `stderr`.

`spawn` est une instruction ou la valeur d’une déclaration, d’une affectation ou d’un `return` ; jamais un argument ni une étape de pipeline (E124). Ignorer son résultat avertit (W060). Il est interdit dans un gear `pure` (E066). Un programme RGL ne relance pas `rgl`.

## 21. Asynchrone et tâches

`@async` déclare une opération asynchrone.

```
@async
gear calculerPlusTard(int valeur): int {
    await sleep(100ms);
    return valeur * 2;
}
```

Une utilisation complète :

```
@async
emit principal(): uint {
    var promesse: calculerPlusTard(21);
    int resultat: await promesse ?? 0;

    std.writeLine(resultat);
    return 0;
}

!train |> principal;
```

Un seul thread exécute le code RGL ; l’asynchrone sert à ne pas bloquer pendant une attente, ce n’est pas du parallélisme. L’appel d’un gear `@async` démarre tout de suite et rend une `Promise<T>`. Une promesse peut être écrite `Promise<T>` ; son attente par `await` donne un résultat `T ! AsyncException` (ou `E?` si elle ne renvoie rien). `await` n’est permis que dans un gear async (E128), et une promesse jetée est E129.

### Modes disponibles

|Élément|Rôle|
|---|---|
|`@async`|Coroutine pouvant attendre|
|`@fire`|Coroutine détachée, sans résultat récupéré|
|`@sync`|Contexte bloquant sur les opérations asynchrones concernées|
|`@task`|Travail exécuté sur le pool de threads|
|`task fonction(...)`|Envoi d’un appel compatible vers le pool|
|`cancel p;`|Annulation d’une promesse (`p.state` donne un `TaskState`)|

Opérations asynchrones reconnues : `sleep`, `whenAny`, `readAsync`, `readBytesAsync`, `writeAsync`, `spawnAsync`, `spawnAsyncWith` ; la std fournit `whenAll`.

Les tâches exécutées sur le pool sont soumises à des restrictions : elles ne touchent ni à la boucle d’événements, ni à une promesse, ni à un flux de fichier (E132).

L’attente dans les lambdas et dans les étapes de parcours n’est pas ouverte de manière générale.

## 22. Annotations et métadonnées

Se placent **avant** une déclaration (gear, guard, emit, mold, error, enum, ingot, constante), après un `export` aussi.

```
@deprecated('Utiliser nouvelleApi()')
[db, cache: '5min']
gear ancienne(int id): int => id;

@tap(before, `entrée {$}` -> console)
@tap(`sortie {$}` -> console)
gear doubler(int n): int => n * 2;
```

- La liste des annotations est fermée : `@deprecated`, `@tap`, `@async`, `@sync`, `@fire` ; toute autre est E125.
- `@deprecated("message")` se pose sur un gear : chaque appel avertit (W061).
- `@tap` attache un effet de bord (une écriture, un appel ou une lambda) à un gear ou un guard, sans changer sa valeur. `after` (par défaut) s’exécute ensuite et `$` est la valeur renvoyée ; `before` s’exécute avant et `$` est le premier paramètre. Une lambda implique `closure` et une lambda sans paramètre est simplement appelée. Pas sur un emit ni un gear `pure`.
- `[métadonnées]` est ignoré par le compilateur et sert aux outils ; toute clé est acceptée. Une exception : `[ignoreError]` sur un handler d’erreur en mode sûr (section 23).

## 23. Organisation du code

### Fichiers `.rgl` et `.forge`

Un fichier `.rgl` sert de point d’entrée (sans en-tête).

Les fichiers `.forge` possèdent un en-tête décrivant leur rôle :

```
forge: gears
description: 'Fonctions utilitaires'

export gear pure doubler(int valeur): int => valeur * 2;
```

|Genre|Contenu|
|---|---|
|`forge: gears`|Tout sauf des trains (gears, molds, constantes, extensions…)|
|`forge: mold`|Molds, enums, ingots et imports|
|`forge: train`|Gears communs, trains nommés puis le train par défaut ; appelable comme un gear|
|`forge: extend`|Extensions et namespace, appliquées à tout le programme|
|`forge: precast`|Données précalculées (section 24)|

Un nom de fichier `.forge` doit être unique dans le projet (E107).

### Importer un fichier

```
import Outils;
```

Ou sélectionner des noms :

```
import Outils only {doubler};
```

Un alias est possible :

```
import Outils only {doubler as multiplierParDeux};
```

Les fichiers se trouvent par leur nom, jamais par un chemin. Un dossier `Nom.box` est opaque : ses fichiers ne se voient qu’entre eux, et `import box Nom;` lit son `index.forge`. `import tools Dossier;` donne accès, à plat, aux noms de tous les fichiers d’un dossier. Les cycles d’import sont E108.

Un fichier ordinaire peut être incorporé comme texte UTF-8 constant :

```rgl
import file "index.html" as page;
```

La recherche choisit le premier fichier de ce nom dans un ordre déterministe. Dans une box, elle reste dans la box. La forme `import [..] file "index.html" as page;` cherche dans le projet qui contient la box. Le nom respecte la casse et le contenu devient une constante `string`.

Les fichiers qui doivent rester modifiables après compilation se déclarent dans `rgl.json` :

```json
{
  "assets": ["front/html/**", "front/css/site.css"]
}
```

À chaque build, ils sont copiés dans `out/debug` ou `out/release` en conservant leur chemin relatif. Un programme peut donc construire leur chemin depuis `std.executableDirectory()`. Un motif qui ne correspond à aucun fichier fait échouer le build afin de détecter les fautes de chemin.

### Composants Paper

Un fichier `.paper` mêle HTML et blocs RGL. Il s'importe comme un module ordinaire et expose `render()` :

```paper
@rgl { reactive int count: 0; }
<p>@@ count @@</p>
```

Les interpolations ordinaires sont échappées. `@@ html(valeur) @@` insère l'ingot `html` sans échappement. `import file` existe comme expression seulement entre `@@` dans Paper. Chaque interpolation est compilée comme un fragment indépendant ; les handlers WebSocket renvoient les seuls fragments invalidés. Voir [Paper](../Docs/Paper.md).

### Exports

Un fichier qui n’écrit aucun `export` exporte tout son premier niveau. Dès qu’il en écrit un, seuls les noms exportés sont visibles : `export gear f() { … }` ou `export {a, b};`. L’index d’une box peut réexporter (`export {...Fichier, A};`).

### Utiliser la std

Accès qualifié :

```
var valeur: std.abs(-5);
```

Import sélectif :

```
use <Math> only {abs, clamp};
```

`import` ne cherche pas dans la std : un nom de la std s’obtient par `use <Nom>`. Un nom que vous déclarez masque celui de la std avec W015.

## 24. Precasts

Un precast est une donnée constante résolue à la compilation, dans un fichier `forge: precast` :

```
forge: precast
description: 'Valeurs de départ'

precast Vec2 Zero { x: 0.0, y: 0.0 }
precast {string name, bool debug} Config { name: 'x'; debug: false; }
```

Il s’importe, s’exporte et se qualifie comme une constante. Seules des données sont permises (littéraux, constantes, molds, tableaux, dictionnaires, tuples) : un appel est E121. Un precast que le programme ne lit pas n’est pas écrit dans le C++.

## 25. Cycle de vie

Un mold peut déclarer un gear de fin :

```
mold Ressource -> fermer {
    int identifiant;
}

gear fermer(Ressource ressource): void {
    std.writeLine(`Fermeture de {ressource.identifiant}`);
}
```

Le gear de fin s’exécute lorsque la valeur termine son cycle de vie, notamment à la sortie de sa portée, à `undefine`, au remplacement par une affectation ou à l’échec d’un guard.

Ces valeurs ont un propriétaire unique :

- une copie interdite produit E136 ;
- un déplacement transfère la valeur ;
- le gear de fin ne peut pas échouer (E135 pour un guard, un emit, un train ou un gear asynchrone) ;
- certaines compositions contenant ces types ne sont pas encore prises en charge et produisent E137.

Ce mécanisme ne doit pas être confondu avec `mut`.

## 26. Mode sûr

Le manifeste `rgl.json` peut activer le mode sûr :

```
{
  "name": "mon-projet",
  "version": "1.0.0",
  "rgl": "0",
  "safe": true,
  "loop": "safe"
}
```

Les deux réglages sont distincts :

|Réglage|Effet|
|---|---|
|`"safe": true`|Vérifie que les trains ne cachent pas certaines erreurs|
|`"loop": "safe"`|Refuse les boucles et reprises non bornées ainsi que les `goto` (E142)|

Le mode sûr contrôle notamment :

- les erreurs d’étapes sans rail (E139) ;
- certaines reprises silencieuses (E140) ;
- les résultats de `spawn` et `await` ignorés (E141) ;
- les appels atteignant un `@fire` (E143) ;
- les rails qui finissent en succès sans lire leur erreur (W065, W066 ; `[ignoreError]` sur le handler les supprime).

L’acceptation peut produire la notice I002, le certificat du mode sûr. Les autres clés de `rgl.json` sont `recursion` (200 par défaut ou `"infinite"`), `overflow`, `icon`, `exclude` et `assets`.

## 27. Documentation et éditeur

Les commentaires documentaires utilisent `///`.

```
/// <summary>Double un entier.</summary>
/// <param name="valeur">Le nombre à doubler.</param>
/// <returns>Le double du nombre.</returns>
gear pure doubler(int valeur): int => valeur * 2;
```

Des balises personnalisées sont acceptées :

```
/// <attention>Cette opération modifie son argument.</attention>
```

### Documentation multilingue

Chaque bloc `<lang>` englobe une documentation complète :

```
/// <lang name="fr">
/// <summary>Double un entier.</summary>
/// <param name="valeur">Le nombre.</param>
/// <returns>Le double.</returns>
/// </lang>
/// <lang name="en">
/// <summary>Doubles an integer.</summary>
/// <param name="valeur">The number.</param>
/// <returns>Twice the number.</returns>
/// </lang>
gear pure doubler(int valeur): int => valeur * 2;
```

La sélection utilise la langue du système sur lequel tourne le compilateur, puis l’anglais, puis le premier bloc disponible.

Les blocs `<lang>` se placent à la racine du commentaire, pas à l’intérieur de `summary`, `param` ou `returns`.

### Commandes documentaires

```
rgl doc check main.rgl
rgl doc check main.rgl --strict
rgl doc build main.rgl
```

La génération produit une référence en JSON et XML dans `out/docs`.

### Extension VS Code

L’extension du dépôt propose notamment :

- coloration syntaxique et sémantique ;
- diagnostics (y compris des fichiers importés) ;
- autocomplétion et imports automatiques, extensions et std comprises ;
- survol des symboles ;
- aide aux paramètres ;
- accès aux définitions ;
- plan du document ;
- éditeur de datasets ;
- snippets documentaires `doclang` et `doccustom`.

## 28. Diagnostics

Chaque diagnostic a un code : `E` pour une erreur, `W` pour un avertissement, `I` pour une notice. Ils s’affichent à la manière de Rust (position, ligne source soulignée, lignes `aide :`). Quelques erreurs courantes :

|Code|Sens|
|---|---|
|E001|Type impossible à déduire|
|E003|Valeur du mauvais type|
|E005|Réaffectation d’une constante|
|E006|Usage après `undefine`|
|E013|Modification sans `mut`|
|E035|Gear inconnu|
|E060|`fail`, `done`, `as!` ou lecture de fichier hors d’un guard|
|E086|Valeur connue hors des bornes|
|E103|Nom déjà utilisé dans le fichier|
|E136|Copie d’une valeur à cycle de vie|

Un message du type « pas encore pris en charge » signale une construction reconnue mais non implémentée. Une erreur de Clang ne doit jamais atteindre l’utilisateur : c’est un bug du transpileur, détaillé dans `build.log` du cache.

## 29. Référence de la bibliothèque standard

La std est écrite principalement en RGL. Ses commentaires documentaires disposent de versions française et anglaise.

Les listes suivantes présentent les éléments destinés à l’utilisation. Les helpers d’implémentation sont distingués en fin de section.

### 29.1 Console

|Élément|Fonction|
|---|---|
|`write<T>(what)`|Écrit sans ajouter de fin de ligne|
|`writeLine<T>(what)`|Écrit puis ajoute une fin de ligne|
|`readLine()`|Lit une ligne|
|`readInt()`|Lit un entier ou renvoie `null`|

### 29.2 Mathématiques

**Constantes et type :**

|Élément|Description|
|---|---|
|`PI`|Nombre π|
|`TAU`|Un tour complet en radians|
|`E`|Nombre d’Euler|
|`LN2`|Logarithme naturel de 2|
|`SQRT2`|Racine carrée de 2|
|`percent`|Pourcentage entier entre 0 et 100|

**Gears :**

|Élément|Fonction|
|---|---|
|`abs`|Valeur absolue|
|`nAbs`|Opposé de la valeur absolue|
|`unsignedAbs`|Valeur absolue entière non signée|
|`sign`|Signe : −1, 0 ou 1|
|`min`, `max`|Minimum et maximum de deux nombres|
|`clamp`|Ramène une valeur entre deux bornes|
|`lerp`|Interpolation linéaire|
|`pow`|Surcharges `pow(int, uint)`, `pow(double, int)` et `pow(double, double)`|
|`powInt`|Alias historique de `pow(int, uint)`|
|`gcd`|Plus grand commun diviseur|
|`lcm`|Plus petit commun multiple|
|`isEven`, `isOdd`|Test de parité|
|`toRadians`, `toDegrees`|Conversion d’angle|
|`floor`|Arrondi entier vers le bas|
|`ceil`|Arrondi entier vers le haut|
|`trunc`|Troncature vers zéro|
|`round`|Entier le plus proche, moitiés vers le haut|
|`fract`|Partie fractionnaire non négative|
|`hypot`|Longueur de l’hypoténuse|
|`sqrt`|Racine carrée ou `null`|
|`cbrt`|Racine cubique, y compris d’un nombre négatif|
|`approxEqual`|Comparaison avec tolérances absolue et relative|
|`inverseLerp`, `remap`|Position dans un intervalle et transposition entre intervalles|
|`smoothstep`|Interpolation cubique bornée entre 0 et 1|
|`factorial`|Factorielle|
|`sin`, `cos`, `tan`|Fonctions trigonométriques|
|`asin`, `acos`, `atan`, `atan2`|Fonctions trigonométriques inverses et angle d’un point|
|`exp`|Exponentielle|
|`ln`|Logarithme naturel|
|`log`|Logarithme dans une base donnée|
|`log2`, `log10`|Logarithmes en bases 2 et 10|

Plusieurs fonctions transcendantes utilisent actuellement des approximations par séries. Certaines opérations renvoient `null` lorsque leurs arguments sortent du domaine accepté.

### 29.3 Chaînes

**Constantes du module String :**

- `SPACE` ;
- `END_OF_LINE` ;
- `TAB`.

Le module fournit aussi `isEmpty`.

**Extensions sur `string` :**

|Élément|Fonction|
|---|---|
|`isEmpty`|Test de chaîne vide|
|`substring`|Extraction entre deux positions|
|`indexOf`|Position de la première occurrence|
|`lastIndexOf`|Position de la dernière occurrence|
|`contains`|Présence d’un texte|
|`startsWith`, `endsWith`|Test de préfixe ou suffixe|
|`repeat`|Répétition|
|`reverse`|Inversion des caractères|
|`toUpper`, `toLower`|Changement de casse des lettres ASCII|
|`isSpace`|Test d’un caractère blanc|
|`isDigit`|Test d’un chiffre décimal|
|`isLetter`|Test d’une lettre ASCII|
|`trimStart`, `trimEnd`, `trim`|Suppression des blancs|
|`isBlank`|Texte vide ou composé uniquement de blancs|
|`removePrefix`, `removeSuffix`|Retrait conditionnel d’un préfixe ou suffixe|
|`capitalize`|Première lettre ASCII en majuscule|
|`padStart`, `padEnd`|Complément jusqu’à une longueur donnée|
|`split`|Découpage selon un séparateur|
|`splitOnce`|Découpage à la première occurrence avec indicateur de présence|
|`lines`|Découpage en lignes|
|`replace`|Remplacement des occurrences|
|`replaceAll`|Nom explicite du remplacement de toutes les occurrences non chevauchantes|
|`toInt`|Conversion vers un entier nullable|
|`toDouble`|Conversion vers un flottant nullable|

L’extension générique `toString()` produit une représentation textuelle d’une valeur.

### 29.4 Collections

**Type :**

- `index`, un ingot fondé sur `long`.

**Opérations sur les tableaux :**

Les gears génériques `map`, `flatMap` et `zip` appartiennent au module
`Collections`. Les autres opérations ci-dessous sont des extensions.

|Élément|Fonction|
|---|---|
|`isEmpty`|Test de tableau vide|
|`first`, `last`|Premier ou dernier élément, nullable|
|`contains`|Présence d’un élément|
|`indexOf`|Position de la première occurrence|
|`any`|Au moins un élément satisfait le test|
|`all`|Tous les éléments satisfont le test|
|`join`|Représentation textuelle avec séparateur|
|`concat`|Concaténation|
|`slice`|Extraction d’une portion|
|`reverse`|Inversion en place|
|`reversed`|Nouvelle collection inversée|
|`filter`|Sélection des éléments satisfaisant un test|
|`find`, `firstWhere`|Premier élément satisfaisant un test|
|`count`|Nombre d’éléments satisfaisant un test|
|`take`|Premiers éléments|
|`drop`|Éléments après les premiers|
|`distinct`|Suppression des doublons|
|`flatten`|Aplatissement d’un tableau de tableaux|
|`map`|Transformation de chaque élément|
|`flatMap`|Transformation en tableaux puis aplatissement|
|`zip`|Combinaison de deux tableaux jusqu’à la fin du plus court|
|`partition`|Séparation entre éléments acceptés et refusés|
|`takeWhile`, `dropWhile`|Conservation ou retrait d’un préfixe selon un test|
|`chunk`|Découpage en blocs|
|`enumerate`|Association des éléments à leur rang|
|`sortedWith`|Copie triée avec un comparateur, tri fusion stable en O(n log n)|
|`sortWith`|Tri en place d’un tableau mutable|

**Opérations sur les tableaux numériques :**

|Élément|Fonction|
|---|---|
|`sum`|Somme|
|`product`|Produit|
|`min`, `max`|Valeur minimale ou maximale|
|`average`|Moyenne nullable|
|`sorted`|Nouvelle collection triée|

**Opérations sur les dictionnaires :**

|Élément|Fonction|
|---|---|
|`isEmpty`|Test de dictionnaire vide|
|`getOr`|Valeur d’une clé ou valeur de remplacement|

Le compilateur fournit également les opérations natives `length`, `push`, `add` et `has`, selon le type de collection.

### 29.5 Nombres pseudo-aléatoires

**Type :**

- `Random`, contenant l’état du générateur.

**Opérations :**

|Élément|Fonction|
|---|---|
|`Random.seed`|Construction depuis une graine|
|`next`|Nombre suivant|
|`nextInt`|Entier entre deux bornes inclusives|
|`nextDouble`|Nombre entre 0 inclus et 1 exclu|
|`nextBool`|Booléen pseudo-aléatoire|
|`choice`|Élément choisi dans un tableau, nullable si le tableau est vide|
|`shuffled`|Copie mélangée par Fisher–Yates|
|`Random.range`|Entier dans une plage sans gérer explicitement un générateur|

Le générateur utilise Park-Miller. Une même graine reproduit une même suite.
`nextInt` emploie un rejet pour ne pas introduire de biais modulo.

Les constantes techniques `MODULUS` et `MULTIPLIER` sont également présentes.

Ce générateur ne doit pas être assimilé à un générateur cryptographique.

### 29.6 Chemins et fichiers

Le module `Path` fournit `join`, `normalize`, `isAbsolute`, `filename`,
`basename`, `extension` et `dirname`. Il utilise les règles de la plateforme
cible et manipule des chemins UTF-8. La normalisation est lexicale : elle ne
consulte pas le disque.

Le module `File` fournit les guards `readText`, `readBytes`, `writeText`,
`writeBytes` et `appendText`. Ils propagent les `IoError` des primitives de
fichiers du langage. `exists` teste sans erreur si un fichier ou dossier existe.

La référence détaillée, avec signatures, cas limites et exemples, se trouve
dans [`Docs/StandardLibrary.md`](../Docs/StandardLibrary.md).

### 29.7 HTTP

Le module `Http` fournit `HttpServer`, `HttpClient`, les requêtes et réponses,
les routes HTTP et WebSocket ainsi que `HttpError`. Il prend en charge les routes exactes et les composants
`:paramètre`, les méthodes GET et POST, les query parameters, les corps avec
`Content-Length` et les réponses texte, HTML ou JSON.

`HttpServer.listen` utilise un pool de workers ; `listenTls` ajoute HTTPS et WSS
avec OpenSSL. `HttpClient` appelle les URL HTTP et HTTPS en validant les certificats.
Les erreurs de transport deviennent `HttpError`. Le transport reste bloquant par
worker et ne fournit pas encore keep-alive, chunked encoding ou frames binaires.

Voir [`Docs/Http.md`](../Docs/Http.md) pour l’API, un exemple complet et les limites.

### 29.8 Temps

|Élément|Fonction|
|---|---|
|`now`|Instant présent en UTC|
|`isLeapYear`|Test d’année bissextile|
|`daysInMonth`|Nombre de jours d’un mois|
|`dateTime`|Construction d’une date depuis ses composants|
|`parseDateTime`|Lecture d’une date ISO 8601|

**Extensions de `datetime` :**

- `year()` ;
- `month()` ;
- `day()` ;
- `hour()` ;
- `minute()` ;
- `second()` ;
- `millisecond()` ;
- `dayOfWeek()` ;
- `dayOfYear()` ;
- `startOfDay()`.

`dayOfWeek()` utilise les valeurs 1 pour lundi à 7 pour dimanche.

### 29.9 Erreurs et asynchrone

|Type|Rôle|
|---|---|
|`errorCode`|Code d’erreur|
|`typeName`|Nom d’un type|
|`StandardError`|Erreur standard avec code|
|`Exception`|Erreur générale|
|`CastError`|Échec de conversion contrôlée|
|`LoopError`|Limite de boucle ou de reprise atteinte|
|`NotFound`|Élément introuvable|
|`ParseError`|Texte impossible à interpréter|
|`IoError`|Échec d’entrée-sortie|
|`AsyncException`|Échec d’une tâche asynchrone|

Les types déclarés avec `error` reçoivent les champs `message` et `where`.

**Constantes de `errorCode` :**

- `NONE` ;
- `NOT_FOUND` ;
- `INVALID` ;
- `IO`.

**États de tâche — `TaskState` :**

- `Ready` ;
- `Running` ;
- `Suspended` ;
- `Completed` ;
- `Cancelled`.

**Genres d’erreur asynchrone — `AsyncKind` :**

- `Cancelled` ;
- `Timeout` ;
- `Aborted` ;
- `IoError`.

**Gear asynchrone de la std :**

|Élément|Fonction|
|---|---|
|`whenAll`|Attend toutes les promesses et conserve l’ordre de leurs résultats|

### 29.10 Processus

|Type|Rôle|
|---|---|
|`ProcessResult`|Code de sortie, sortie standard et sortie d’erreur|
|`SpawnError`|Raison et message d’un échec de lancement|
|`SpawnFailure`|Catégorie d’échec du processus|

`ProcessResult` expose :

- `exitCode` ;
- `stdout` ;
- `stderr`.

`SpawnFailure` possède les variantes :

- `ExecNotFound` ;
- `PermissionDenied` ;
- `Timeout` ;
- `IoError`.

Le lancement lui-même passe par `spawn` ou les opérations asynchrones reconnues par le compilateur.

### 29.11 Journaux

**Types :**

- `logMessage` ;
- `LogLevel`.

**Niveaux et gears correspondants :**

|Niveau|Gear|
|---|---|
|`Trace`|`trace`|
|`Debug`|`debug`|
|`Info`|`info`|
|`Warning`|`warning`|
|`Error`|`error`|
|`Fatal`|`fatal`|

Les fonctions de journalisation peuvent recevoir automatiquement le nom de l’appelant grâce à `__CALLER__`.

### 29.12 Système

Le type `programValue` représente un code de sortie de 0 à 255.

|Élément|Fonction|
|---|---|
|`programValue.SUCCESS`|Réussite|
|`programValue.FAILURE`|Échec|
|`programValue.from`|Construction depuis un code|
|`programValue.fromCode`|Conversion depuis un code d’erreur|

### 29.13 Couleurs

|Type|Description|
|---|---|
|`colorUnit`|Composante entière entre 0 et 255|
|`Color`|Champs `r`, `g`, `b`|
|`ColorAlpha`|Couleur avec une opacité `a` en pourcentage|

### 29.14 XML

`XmlNode` expose :

|Champ|Contenu|
|---|---|
|`name`|Nom de la balise|
|`attributes`|Dictionnaire d’attributs|
|`children`|Tableau des éléments enfants|
|`text`|Texte directement contenu dans l’élément|

Les commentaires XML, les instructions de traitement et l’ordre des attributs ne sont pas conservés. Les déclarations DOCTYPE sont refusées.

### 29.15 Géométrie

La box `Geometry` regroupe les vecteurs et les types géométriques.

|Élément|Description et opérations|
|---|---|
|`Vector2`|Champs `x`, `y` ; addition, soustraction, multiplication scalaire, comparaisons, conversion textuelle|
|`Vector3`|Champs `x`, `y`, `z` ; opérations analogues|
|`normalizedVec3`|Ingot représentant un vecteur 3D de longueur 1, sans vérification automatique à la construction|
|`Plane`|Normale et distance à l’origine|
|`Ray`|Origine et direction|
|`AABB3`|Boîte alignée sur les axes, définie par `min` et `max`|
|`Sphere`|Centre et rayon|
|`Quaternion`|Composantes `x`, `y`, `z`, `w` et composition de rotations|

**Vecteurs :**

- `Vector2.ZERO`, `Vector3.ZERO` ;
- `dot` ;
- `lengthSquared` ;
- `distanceSquared` ;
- `cross` pour `Vector3`.

**Plans :**

- `signedDistance` ;
- `contains`.

**Rayons :**

- `pointAt` ;
- `intersectPlane`.

**Boîtes :**

- `contains` ;
- `center` ;
- `size` ;
- `intersects` ;
- `merge` ;
- `closestPoint`.

**Sphères :**

- `contains` ;
- `intersects` ;
- `intersectsBox`.

**Quaternions :**

- `IDENTITY` ;
- produit de Hamilton ;
- égalité ;
- `dot` ;
- `lengthSquared` ;
- `conjugate`.

**Autres éléments :**

- `EPSILON` ;
- `degToRad` ;
- `radToDeg`.

### 29.16 Opérations reconnues par le compilateur

Ces opérations complètent la std, mais ne sont pas toutes définies comme des gears ordinaires dans un fichier `.forge`.

|Élément|Fonction|
|---|---|
|`ok`, `err`|Construction d’un résultat|
|`sleep`|Attente asynchrone|
|`whenAny`|Attente de la première promesse terminée|
|`readAsync`|Lecture asynchrone de texte|
|`readBytesAsync`|Lecture asynchrone d’octets|
|`writeAsync`|Écriture asynchrone|
|`spawnAsync`|Lancement asynchrone d’un processus|
|`spawnAsyncWith`|Lancement asynchrone avec options|

Les méthodes des regex, des flux et des datasets sont également prises en charge par le compilateur.

### 29.17 Helpers d’implémentation

La std contient aussi des éléments techniques qui ne constituent pas son interface principale :

- `_gather` pour `whenAll` ;
- `_log` pour les journaux ;
- `_turn` pour la réduction des angles ;
- `_daysFromCivil`, `_digits`, `_digitRun` pour les dates ;
- `_timeDays`, `_timeMilliseconds`, `_timeCivil` pour les extensions temporelles ;
- `_swapCase`, `LOWER_LETTERS`, `UPPER_LETTERS`, `DIGITS` pour les chaînes ;
- `_serve`, `_dispatch`, `_request`, `_parametersAt` et `_routeParametersAt` pour HTTP ;
- les precasts `VEC2_ZERO` et `VEC3_ZERO` utilisés pour les vecteurs nuls.

## 30. À faire

L’atelier en ligne fait foi pour la slice en cours ; ce guide décrit le comportement livré dans le dépôt.

### Finaliser le langage actuel

- Déclencher chaque code d’erreur `E` et chaque avertissement `W` dans au moins un test.
- Comparer la référence du langage au comportement réel du compilateur.
- Corriger les écarts de syntaxe, de diagnostics et de comportement.
- Fusionner la correction des intentions de déplacement.
- Maintenir le gel fonctionnel annoncé jusqu’à V1 : corrections et intégration C++ plutôt que nouvelles fonctionnalités.

### Développer l’intégration C++

Les étapes prévues comprennent :

- `forge: native` et l’import d’en-têtes ;
- la finalisation de `rgl check` ;
- le code de liaison et les classes ;
- `unsafe` et la frontière avec la représentation SoA ;
- les bornes dépendant des paramètres ;
- l’achèvement de la std.

La présence de mots-clés ou d’une commande dans le compilateur ne signifie pas que toute l’intégration native est déjà disponible.

### Optimiser les programmes générés

- Compléter les possibilités SoA.
- Stabiliser la disposition mémoire.
- Améliorer les coûts mémoire et les temps de compilation.
- Optimiser les copies, déplacements et le générateur C++.
- Poursuivre les datasets : mutabilité, extensions et émission de leurs données.

### Publier la documentation

- Construire un site de documentation.
- Séparer le guide d’apprentissage de la référence d’API.
- Générer les fiches de la std depuis les docblocks.
- Publier les versions française et anglaise.
- Ajouter des exemples exécutables et vérifier leur comportement.
- Documenter systématiquement les limites et les différences avec les autres langages.

### Préparer l’écosystème et V1

- Finaliser l’outillage.
- Mettre en place le site du langage.
- Préparer le système de modules et son gestionnaire.
- Organiser la publication.
- Réexaminer les contrats après le gel, conformément à l’atelier.

Le transport web (serveur concurrent, client HTTP/HTTPS, TLS et WebSocket texte)
est livré. Le playground « Essayer RGL » et un framework web plus complet restent
à construire au-dessus de ce socle.
