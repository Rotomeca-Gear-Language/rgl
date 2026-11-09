# C++ intégré et frontières de confiance — A2.4B

Un bloc `cpp` contient du C++ source. Il est extrait dans une unité séparée,
compilée avec les exceptions, puis appelé depuis le programme RGL.
Il s’écrit dans un gear `unsafe`, ou dans une fonction annotée `@trusted`.

```rgl
@trusted
gear pure racine(double x): double? {
    if (x < 0.0) { return null; }
    return cpp {
        #include <cmath>
        double rgl(double x) { return std::sqrt(x); }
    }(double x: x): double;
}

gear afficher(): int {
    std.writeLine(racine(81.0) ?? 0.0);
    return 0;
}
!train |> afficher |> $ 0;
```

`std::sqrt` ne renvoie pas `null` pour une valeur négative ; le contrôle RGL
conserve donc explicitement le résultat nullable de cette API.
`std.sqrt` utilise désormais cette frontière et reste une API `pure` sûre.

## Arguments et résultat

La forme complète est :

```rgl
cpp {
    int rgl(int a, int b) { return a + b; }
}(int a: expressionA, int b: expressionB): int
```

Les expressions RGL s’évaluent une seule fois, dans l’ordre écrit. Les noms
de la liste ne capturent pas les variables locales dans le C++ : seules les
valeurs explicitement passées franchissent la frontière. Le bloc s’exécute
à chaque évaluation de l’expression RGL. Un retour `void` est permis.

Une unique fonction C++ `rgl` doit avoir la signature annoncée. Les types
scalaires suivent les conventions natives existantes : par exemple `int`
RGL correspond à `std::int32_t`, `long` à `std::int64_t`. Les paramètres
composites sont reçus par référence constante, les pointeurs par adresse brute.
La signature peut être `noexcept` ; cela ne prouve pas sa pureté.

Pour éviter de réécrire la représentation C++, le compilateur fournit dans
chaque bloc les alias `rgl_arg_NOM` et `rgl_return` :

```rgl
@trusted
gear pure identite<T>(T x): T => cpp {
    rgl_return rgl(rgl_arg_x x) { return x; }
}(T x: x): T;
```

Les scalaires natifs, strings, enums, ingots, molds et tableaux acceptés par
A2.3/A2.4 sont disponibles, ainsi que leurs nullables. Les types à cycle de vie
restent soumis aux restrictions existantes. `pack()` reste nécessaire pour
passer un tableau de molds stocké en SoA ; aucun empaquetage implicite n’est ajouté.
Pour les pointeurs, les règles de propriété et de déplacement d’A2.4 s’appliquent.

Une fonction RGL peut aussi être passée comme callback lorsque tous ses paramètres
et son retour sont compatibles avec cette frontière. Le C++ la reçoit sous la
forme annoncée par l’alias `rgl_arg_NOM` :

```rgl
@trusted
gear appliquer((int) => int callback, int value): int => cpp {
    int rgl(const rgl_arg_callback& callback, int value) {
        return callback(value);
    }
}((int) => int callback: callback, int value: value): int;
```

Le callback reste une fonction RGL : ses contrôles et sa représentation ne sont
pas remplacés par une fonction C++ arbitraire. Le code natif ne doit pas conserver
une référence au callback au-delà de l’appel si sa durée de vie n’est pas garantie.
Le serveur HTTP de la std utilise ce passage synchrone pour appeler son routeur RGL.

Les en-têtes sont inclus au niveau global ; les déclarations de chaque bloc
sont isolées dans un namespace interne distinct. Des blocs différents peuvent
ainsi avoir chacun un helper de même nom. Les chaînes C++, y compris les raw
strings, et les commentaires ne ferment pas le bloc RGL.
Le texte doit garder des accolades lexicalement équilibrées : le découpage
RGL ne réalise pas le prétraitement C++.

`#include <rgl::standard>` est un raccourci pour l’en-tête de types de valeur
`rgl/standard.hpp`. Les inclusions ordinaires C++ restent disponibles ; les
chemins relatifs partent du dossier du fichier RGL ou forge d’origine.

## unsafe, trusted et pure

- `unsafe f(...)` expose une API unsafe ; ses appelants héritent de ce caractère.
- `unsafe pure f(...)` garde une API unsafe tout en faisant vérifier la pureté
  de son corps RGL. Un C++ pur exige également un contrat `@trusted` explicite.
- `@trusted gear f(...)` expose une interface sûre autour d’unsafe ou de C++.
  L’auteur garantit les invariants que le compilateur ne peut pas vérifier.
- `@trusted gear pure f(...)` atteste aussi la pureté des blocs C++ du gear.
  La partie RGL reste vérifiée : sorties console, appels impurs et effets
  connus ne deviennent pas autorisés par l’annotation.

`@trust` est accepté comme alias de `@trusted`. Un seul contrat se pose sur une
fonction. Le survol de l’éditeur signale explicitement la confiance native.

`@trusted` arrête la propagation unsafe à la frontière publique. Il ne supprime
pas les restrictions des types de signature, les contrôles d’erreurs des trains,
les restrictions de boucles ou l’interdiction des tâches détachées en mode safe.
Une fonction ne devient pas pure simplement parce qu’elle est trusted.

Une exception C++ non traitée dans un bloc arrête le programme, comme dans un
composant unsafe d’A2.4. Elle ne devient pas automatiquement `NativeError`.
Pour une erreur récupérable, traiter l’exception dans le C++ et renvoyer une
valeur adaptée, ou utiliser un guard natif A2.3 avec son rail.

## Configuration du projet

Dans `rgl.json` :

```json
{
  "name": "exemple",
  "version": "1.0.0",
  "rgl": "0",
  "safe": true,
  "no-trust": true,
  "no-cpp": true,
  "cpp-trust": ["native/Calcul.forge"]
}
```

Les deux interdictions valent `false` par défaut. `cpp-trust` vaut `[]`.

- `no-trust` interdit d’appeler une fonction `@trusted` non autorisée, ou d’en
  prendre une référence. Une déclaration non utilisée peut rester présente.
- `no-cpp` interdit les blocs C++ et les liaisons chargées du projet : imports
  `forge: native`, descriptors et implémentations `.rgl.cpp` de gears/trains.
  Les opérations unsafe écrites entièrement en RGL ne sont pas désactivées.
- `cpp-trust` autorise exactement les fichiers `.forge` listés, malgré ces deux
  interdictions. Les chemins sont relatifs à la racine du projet. Les chemins
  absolus, les remontées hors du projet et les liens pointant hors du projet
  ne donnent pas d’autorisation. Un fichier autorisé n’autorise pas ses imports.
- La std embarquée authentique bénéficie de l’exception aux deux interdictions.
  Appeler un fichier utilisateur `std` ne lui donne aucun privilège.

Un `@trusted` utilisateur ne contourne donc pas `no-cpp`. Il faut retirer
l’interdiction ou autoriser explicitement son fichier forge.

Ces options contrôlent les fonctionnalités et fichiers admis par le compilateur.
Elles ne constituent pas un bac à sable pour un programme C++ arbitraire.

## Limites et diagnostics

Cette construction n’ajoute pas de chargement ou de configuration de bibliothèques
DLL, SO ou archives. Les bibliothèques restent le rôle des liaisons forge.
Les inclusions directes de binaires et de wrappers RGL générés sont refusées.
Un fichier C/C++ déjà lié par un forge du programme ne peut pas être réinclus
directement par un bloc : appeler son interface RGL. Ce contrôle identifie les
fichiers liés ; il ne prétend pas reconnaître du code copié ou ses variantes.

- E147 : bloc hors contexte unsafe ou trusted.
- E149 : bloc incomplet, arguments invalides, inclusion interdite ou type natif
  non pris en charge.
- E150 : utilisation interdite par `no-trust` ou `no-cpp`.
- E151 : annotation trusted mal placée ou dupliquée.
- E146 : C++ utilisateur invalide, y compris une signature `rgl` incompatible.
- E148 : tableau nécessitant `pack()` à la frontière native.

La compilation des unités utilisateur reste séparée du C++ généré par RGL.
`--emit-cpp` permet d’inspecter les unités sans les compiler.
Les blueprints restent différés après la v1.
