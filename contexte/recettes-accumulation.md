# Recettes : de « ce que je veux » vers `|+>`

La valeur de départ doit avoir un type connu : `null` et `[]` s'écrivent dans une variable typée d'abord (`T? none: null;`).

Une accumulation `tableau |+> départ with étape` replie un tableau en une seule valeur. L'étape reçoit
`(acc, x)` ou `(acc, x, i)` : l'accumulateur, l'élément, son rang (un `int`). `done valeur;` arrête le
parcours avec cette valeur ; `with {…}` copie un mold en changeant quelques parties.

La std écrit déjà les plus courantes (`std/extentions/collections_ext.forge`) : on les appelle comme des
méthodes (`xs.sum()`), sans rien importer.

| Je veux… | Dans la std | Écrit à la main |
| --- | --- | --- |
| la somme | `xs.sum()` | `xs \|+> 0 with (acc, x) => acc + x` |
| compter ce qui vérifie un test | `xs.count((x) => x > 3)` | `xs \|+> 0 with (acc, x) => x > 3 ? acc + 1 : acc` |
| savoir si un élément vérifie un test | `xs.any((x) => x > 8)` | `xs \|+> false with (acc, x) => { if (x > 8) { done true; } return acc; }` |
| savoir si tous vérifient un test | `xs.all((x) => x > 0)` | `xs \|+> true with (acc, x) => { if (not (x > 0)) { done false; } return acc; }` |
| le premier qui vérifie un test (`T?`) | `xs.find((x) => x > 3)` ou `xs.firstWhere(…)` | `T? none: null;` puis `xs \|+> none with (acc, x) => { if (x > 3) { done x; } return acc; }` |
| le rang d'un élément (`index?`) | `xs.indexOf(x)` | `index? none: null;` puis `xs \|+> none with (acc, x, i) => { if (x eq cherche) { done index(i as long); } return acc; }` |
| les éléments qui vérifient un test | `xs.filter((x) => x > 3)` | `[T] none: [];` puis `xs \|+> none with (acc, x) => x > 3 ? [...acc, x] : acc` |

## Changer une partie d'une valeur : `with`

`p with {chemin: valeur, …}` donne une copie de `p` ; `p` ne bouge pas. Un chemin part d'un champ de `p`
et peut descendre par `.champ` et `[index]` : `p with {pos.x: 3, cells[i]: State.J1}`. Les changements
s'appliquent dans l'ordre écrit, et leurs valeurs lisent les variables d'avant, pas la copie
(`p with {hp: p.hp - 1}`). Un index hors des limites arrête le programme, ou fait échouer le guard.

`a.x <- v` reste réservé aux variables `mut` (E013 sinon) : `with` est l'alternative quand la valeur n'est pas `mut`.

Dans une accumulation, `with` met à jour le mold qui sert d'accumulateur :

```rgl
gear niveau(Hero h, [int] gains): Hero =>
    gains |+> h with (acc, gain) => acc with {xp: acc.xp + gain, hp: acc.hp + 1};
```

## Arrêter proprement : `panic`

`panic "message";`, `panic erreur;` ou `panic;` arrête le programme (code 101) en écrivant le message et la
position sur stderr, après avoir vidé la console. C'est pour un état qui ne devrait jamais arriver ;
une erreur normale se signale par `fail` dans un guard. `panic` est permis partout, même dans un gear `pure`.

## Lire le reste d'une union : `default x`

```rgl
gear nom(int | string | bool v): string => match (v) {
    int i |> 'entier';
    default reste |> match (reste) { string s |> s; bool b |> 'bool'; };
};
```

`reste` reçoit l'union des types que les autres bras ne prennent pas (le type seul s'il n'en reste qu'un).
`default |> …` sans nom reste permis ; les deux ensemble dans un même match sont E081.

## Un handler plus étroit que les erreurs reçues

Un rail reçoit l'union des erreurs de son guard. Un handler `onError(Exception e)` suffit si chaque membre de
l'union a au moins les champs de `Exception` : il reçoit alors chaque erreur réduite à ces champs.
