# Mémoire et frontière SoA — A2.4

Les blueprints sont différés après la v1, selon les besoins.

## Pointeurs

Les opérations mémoire se placent dans un gear `unsafe`, qui ne peut utiliser
ni `done` ni `fail`. Les appels propagent ce caractère aux gears et trains
appelants. Les liaisons natives utilisant des pointeurs sont également non sûres. Le mode projet `safe: true` refuse ces chemins.

```rgl
unsafe calcul(): int {
    ptr<int> origine: 5;
    uptr<int> manuel:: origine;
    ptr<int> final:: manuel;
    *final <- 8;
    return *final;
}
```

La déclaration alloue directement la valeur : aucun `new` RGL n’est ajouté.
`ptr<T>` possède son objet et le libère quand il sort de portée. Il est
move-only : une copie est E136. `uptr<T>` n’a pas de libération automatique.
`::` et `<-|` transfèrent dans les deux sens et invalident la source (E006
si elle est relue). Les paramètres pointeurs sont empruntés pendant l’appel.

```rgl
unsafe nettoyer(): void {
    uptr<int> a: 5;
    a.free;
    uptr<int> b: 8;
    undefine b;
}
```

`p.free()` est aussi accepté. `free`, `delete p` et `undefine p` libèrent
l’objet et invalident le nom, pour les deux types. Un `ptr` ne le libère pas
une deuxième fois lors de sa destruction. Une copie de `uptr` crée un alias
brut ; elle ne crée pas un deuxième objet.

W068 signale une variable locale contenant un `uptr` pouvant sortir de portée sans libération ni
transfert. L’analyse distingue les branches et retours anticipés ; elle reste
conservatrice face aux alias et aux appels externes.

## Adresses et accès

```rgl
unsafe adresse(): int {
    int x: 5;
    uptr<int> p: &x;
    *p <- 9;
    return x;
}
```

`&x` emprunte une variable existante, sans allocation supplémentaire. Cette
adresse connue comme empruntée ne peut ni être libérée ni être adoptée par un
`ptr` propriétaire. Elle n’a pas de W068 : `x` gère sa propre durée de vie.
La prise d’adresse porte actuellement sur une variable locale non constante.

`p + n` et `p - n` donnent des adresses empruntées, sans nouveau propriétaire.
Deux pointeurs de même type se comparent avec `eq` ; leur soustraction donne
un `long`. Les bornes et la validité des accès relèvent du code `unsafe`.
`null` initialise un pointeur vide.

`char` correspond au `char` C++ de la cible, sur 8 bits. Les conversions avec
les entiers sont explicites (`65 as char`, `c as int`). Les chaînes RGL gardent
leur comportement Unicode.

Les compositions copiant des propriétaires restent soumises à E137, comme
les autres valeurs à cycle de vie. Un objet RGL ayant déjà un cycle de vie
n’est pas encore une cible de pointeur dans cette première version.

## Interop C++

Un gear `unsafe` sans corps dans `forge: gears` est implémenté dans
`nom.rgl.cpp`. Le C++ reçoit un `T*` ; RGL garde son enveloppe propriétaire
ou manuelle. Un résultat `ptr<T>` adopte l’objet renvoyé, qui doit pouvoir être
libéré avec le `delete` C++ correspondant à son type.

Un guard natif envoie toujours ses exceptions au rail comme `NativeError`.
Un composant déclaré `unsafe` n’a pas de rail : une exception C++ non traitée
arrête le programme.

## `pack(tableau)`

Un tableau de molds peut être rangé en colonnes (SoA). `pack` produit une
nouvelle valeur rangée par éléments, utilisable par une liaison C++ :

```rgl
guard calcul(): int {
    [Cpp.Point] points: [{x: 3, y: 4}, {x: 6, y: 8}];
    return Cpp.somme(pack(points));
}
```

La source conserve sa représentation et reste utilisable. Le résultat peut
être conservé (`var contigus: pack(points);`). La conversion parcourt aussi
les tableaux imbriqués. E148 signale un tableau passé directement au natif
alors qu’il nécessite cet empaquetage.

La suppression dynamique de colonnes n’existe pas dans cette version.
Un champ retiré du type à la déclaration n’est plus accessible au binder.
