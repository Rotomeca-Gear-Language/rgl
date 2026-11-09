# Intégration C++ — A2.3

Le C++ du composant est compilé dans une unité séparée avec les exceptions.
Le programme RGL reste compilé sans exceptions. Pour un guard, une exception devient une
`std.NativeError` contenant `message`, `where` (l’appel RGL) et `callee`.
Les guards et les trains appelants traitent cette erreur sur leurs rails.

## Un guard écrit en C++

`Calcul.forge` :

```rgl
forge: gears
description: "Calcul natif"
guard doubler(int n): int;
```

`doubler.rgl.cpp`, dans le même dossier :

```cpp
namespace rgl_native {
std::int32_t doubler(std::int32_t n) { return n * 2; }
}
```

Le compilateur déclare la fonction et fournit les en-têtes du runtime utilisés
par sa signature. Les inclusions propres au composant restent dans son fichier.
Une déclaration invalide produit E145 ; un C++ refusé par Clang produit E146.

## Un train écrit en C++

Le train déclare explicitement son résultat, puisque son corps n’est pas du RGL.
`Calcul.forge` :

```rgl
forge: train
description: "Train natif"
train(int n): int;
```

Son implémentation est `Calcul.rgl.cpp`, fonction `rgl_native::Calcul`.
Un train nommé s’écrit `train doubler { train(int n): int; }` et utilise
`doubler.rgl.cpp`. Le train par défaut reste obligatoire à la fin du fichier.
Les exceptions deviennent les mêmes erreurs natives que celles d’un guard.
`!train`, `mut`, `this`, `params` et les valeurs par défaut sont refusés pour
ces déclarations natives.

## Décrire une classe

```rgl
forge: descriptor
description: "Boîte C++"
header: "lib/box.hpp"
link: static

descriptor demo::Box<T> -> Boite<T> {
    constructor(T start) => demo::Box<T>(start);
    field value: T => value;
    add(T n): T => add;
    add(T n, T m): T => add;
}
```

L’en-tête contient ici une classe template avec un constructeur, un champ public
`value` et les deux méthodes `add`. Le descriptor peut également être non générique.
Les paramètres génériques de type sont remplacés à chaque instanciation.

```rgl
import Cpp;

guard calcul(): int {
    Cpp.Boite<int> b: Cpp.Boite<int>.create(5);
    b.set_value(12);
    int a: b.add(3);
    return a + b.add(2, 4) + b.value();
}
```

`create` construit l’objet. Un champ est exposé par deux guards : `value()` et
`set_value(T value)`. `const field` expose seulement la lecture. Une expression
personnalisée de champ modifiable doit désigner une lvalue C++ assignable ; pour
une conversion, écrire explicitement les méthodes de lecture et d’écriture.
Les méthodes statiques se déclarent avec `static` et s’appellent sur le type.

L’objet RGL est opaque, possède son objet C++, se déplace et ne se copie pas.
Son destructeur C++ est appelé une seule fois quand le propriétaire sort de portée.
Les exceptions d’un destructeur sont ignorées à cette frontière.

Les surcharges sont départagées par le nombre d’arguments et leurs types : une
correspondance exacte précède une conversion implicite. Une ambiguïté est E145 ;
un cast explicite peut la résoudre. Le retour seul ne distingue pas deux signatures.
Les placeholders `$self` et `$arg0`, `$arg1`, etc. permettent des expressions C++
complètes. Les chaînes C++ se convertissent sans perdre les octets nuls avec
`rgl::standard::string::from_std_string(value)` et `value.to_std_string()`.
`from_cstr(ptr)` copie un texte C terminé par zéro, tandis que `view()` donne une
`std::string_view` sans copie. Le constructeur `rgl::standard::string(std::string)`
est également disponible.

## Types à la frontière

Les signatures natives acceptent les entiers, flottants, `bool`, `string`, les
enums, les ingots et les molds sans cycle de vie, ainsi que leurs tableaux.
Les objets décrits traversent les appels de descriptor sous forme de poignées
possédées par RGL ; les collections de ces objets ne sont pas acceptées.
Les dictionnaires, fonctions et références retournées restent exclus. Les
pointeurs sont disponibles dans les gears `unsafe` décrits dans [Unsafe.md](Unsafe.md).

Les molds ordinaires conservent leurs champs générés (`rgl_f_nom`), et les enums
leur type C++ généré. Par exemple, `Point` déclaré dans `Calcul.forge` devient
`rgl_m_Calcul_d_Point`. Les tableaux dynamiques utilisent
`rgl::standard::array<T>` ; `pack(tableau)` fournit explicitement la représentation
par éléments à une frontière native, tout en conservant la représentation SoA
de la source. Voir [unsafe et pointeurs](Unsafe.md).
Les paramètres composites sont reçus par référence constante. Cette interface
C++ générée doit être recompilée avec le programme ; elle n’est pas une ABI publique
stable entre versions du compilateur.

## Squelette, éditeur et cache

```sh
rgl descriptor lib/counter.hpp --class demo::Counter --name Compteur -o Cpp.forge
```

La commande lit les membres publics avec Clang, conserve les surcharges dont les
signatures RGL diffèrent et commente les cas non représentables avec leur raison.
Elle n’écrase pas un fichier existant. Les champs convertis modifiables deviennent
une paire de méthodes avec les conversions nécessaires. Le squelette d’un template
de classe reste à écrire manuellement.

Le serveur de langage utilisé par VS Code expose les types, les membres et leurs
signatures, et conserve les positions des déclarations originales pour la navigation.

Les unités C++ compilées sont mises en cache sous `native-units`. L’empreinte couvre
le compilateur RGL, Zig, la cible, les options du profil, la source et ses inclusions
transitives. Un changement d’en-tête invalide l’objet. Un cache indisponible laisse
la compilation fonctionner ; `rgl cache clear` le vide avec les autres caches.

Le niveau `wrapper` reste mis de côté selon la décision de l’atelier.
