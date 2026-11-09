# Surcharges de gears

Plusieurs gears peuvent partager un nom lorsque leurs paramètres permettent de
choisir une seule signature :

```rgl
gear pure parse(string text): int? => text as? int;
gear pure parse(int value): string => `{value}`;

int? n: parse('42');
string text: parse(42);
```

La signature d'appel comprend le nombre et le type des paramètres, leur ordre,
les paramètres génériques et `params`. Le nom des paramètres, le retour,
`pure`, `unsafe`, `@trusted` et la différence entre `gear`, `guard` et `emit`
ne créent pas une signature distincte. `mut` seul ne suffit pas non plus :
l'appel n'écrit pas `mut`, il ne pourrait donc pas choisir sans surprise.
Deux signatures identiques restent E055.

## Choix d'une signature

Le binder conserve les candidates qui acceptent le nombre d'arguments et leurs
types, puis préfère dans cet ordre :

1. le moins de conversions implicites ;
2. le moins de paramètres omis et remplis par défaut ;
3. une liste de paramètres ordinaire avant `params` ;
4. à rang égal, une signature non générique avant une générique.

Une instance générique exacte peut donc gagner contre une signature ordinaire
qui demanderait une conversion :

```rgl
gear pure show<T>(T value): string => 'generic';
gear pure show(string value): string => 'string';

show('x'); // surcharge string
show(2);   // instance show<int>
```

Le type de retour attendu n'est jamais utilisé pour départager un appel. Si
plusieurs signatures ont le même rang, E152 les liste ; une conversion `as`
explicite permet de choisir :

```rgl
gear f(long value): int => 1;
gear f(double value): int => 2;

f(1 as long); // choisit f(long)
```

Les mêmes règles s'appliquent aux imports qualifiés, à `only`, aux pipelines,
aux étapes de train et aux gears d'extension. Une référence de gear surchargé
demande un type de fonction précis :

```rgl
pure (int) => int operation: calcul;
```

L'aide de signature du serveur de langage expose toutes les surcharges.
