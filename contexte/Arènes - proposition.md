# Arènes — proposition (à valider)

État : **proposition de Claude, rien n'est codé.** La slice B.3 de l'atelier demande des arènes « d'abord par frame et explicites, puis temporaires de pipeline via l'analyse d'échappement » sans en fixer la syntaxe ni les règles.

## Ce que c'est

Une arène est une zone de mémoire dont on avance un pointeur pour allouer, et qu'on libère **d'un coup**. Allouer coûte quelques instructions (au lieu d'un `malloc`), libérer ne coûte rien, et ce qui a été créé ensemble est voisin en mémoire.

Dans le runtime, `array`, `mstring` et `string` allouent par une `AbiAllocator` (trois pointeurs de fonctions) : une arène est une autre `AbiAllocator`.

## Syntaxe proposée

```rgl
emit frame(): uint {
    arena {
        var vivants: particules ||> avancer;      // le tableau est dans l'arène
        var texte: `{vivants.length} particules`;  // le texte aussi
        dessiner(vivants, texte);
    }                                              // arène vidée ici
    return 0;
}
```

- `arena { … }` : un bloc. `arena(64kb) { … }` fixe la taille du premier bloc de mémoire (64 Ko par défaut).
- Pas de nom, pas de variable d'arène : rien à passer, rien à oublier de libérer.
- `return`, `break`, `goto`, `fail`, `done` dans le bloc le quittent normalement : l'arène se vide dans tous les cas.
- Dans une boucle, l'arène **garde son premier bloc de mémoire d'un tour à l'autre** : le coût d'une frame est zéro allocation du système.

## La règle de sûreté : rien ne sort de l'arène sans être copié

Pas d'analyse d'échappement à écrire pour la première étape. Les valeurs de RGL se copient déjà à l'affectation ; on ajoute **une règle dans le runtime** : un tableau ou un texte qui vit dans une arène, quand on le copie **ou le déplace** vers autre chose, devient un tableau ou un texte **du tas** (copie profonde).

Conséquences :

- `dehors <- vivants;`, `return vivants;`, `liste.push(texte);`, capture par une lambda : tout passe par une copie ou un déplacement, donc donne une valeur du tas. Aucune valeur ne peut garder l'adresse d'une arène vidée.
- Une valeur qui reste dans le bloc n'est jamais copiée : c'est là que le gain se fait.
- Le compilateur n'a aucune erreur d'échappement à produire. Le prix d'une valeur qui sort est une copie, que le langage faisait déjà (`LastUses` la remplaçait par un déplacement ; pour une valeur d'arène ce déplacement redevient une copie).
- Option (à décider) : une **notice** quand une valeur d'arène sort (« cette valeur est copiée hors de l'arène »), pour que le coût se voie.

## Ce qui n'est pas permis

- `await`, `task f(x)` ou un appel `@sync` qui attend **dans** un bloc `arena` : l'arène suit le thread, une attente peut reprendre ailleurs. Erreur de compilation, avec le chemin pour sortir (« mets l'attente hors du bloc »).
- Les types qui se partagent par compteur et pourraient garder l'arène en vie par une copie (`function`, `FileStream`) n'utilisent **jamais** l'arène : ils restent sur le tas.

## Qui alloue dans l'arène

Option A (recommandée) — **ambiante** : tant que le bloc est ouvert, toute allocation de `array`, `mstring` et `string` longue faite sur ce thread va dans l'arène, y compris dans les gears appelés depuis le bloc. Les valeurs rendues par ces gears sortent donc de l'arène par la règle ci-dessus (copie à la sortie du gear).

Option B — **lexicale** : seul ce qui est écrit dans le bloc alloue dans l'arène ; les gears appelés allouent sur le tas comme aujourd'hui. Plus prévisible, mais l'arène ne sert presque plus dès que le travail est dans un gear.

## Deuxième étape : les temporaires de pipeline

Je recommande de **l'abandonner ou de la reporter** : `a ||> f ||> g |+> 0 with h` est déjà fusionné en une seule boucle, sans tableau intermédiaire. Reste le cas d'un tableau intermédiaire écrit en variable, que l'utilisateur peut déjà mettre dans un bloc `arena`. L'analyse d'échappement ne gagnerait que sur des cas rares ; à rouvrir si un banc le montre.

## Réalisation (si validé)

- Runtime : composant `Arena` (`arena.hpp`/`arena.cpp` : blocs chaînés, réutilisés d'un tour à l'autre, une pile d'arènes par thread) ; `default_allocator()` rend l'arène courante ; copie et déplacement de `array`, `mstring` et `string` qui repèrent une allocation d'arène (par le pointeur de libération de l'`AbiAllocator`) et recopient sur le tas ; `function` et les autres types partagés appellent `heap_allocator()`.
- Parser : `arena` mot-clé ou contextuel suivi de `{` ou `(` ; AST `ArenaBlock` ; binder : le bloc est une portée ordinaire, E pour une attente dedans ; IR `IrArena` ; émetteur : un objet RAII `rgl::standard::arena_scope` en tête de bloc C++.
- Tests : valeur copiée hors de l'arène, boucle de frames sans allocation du système (compteur d'allocateur), arène imbriquée, `return` et `fail` dans le bloc, attente refusée, banc `particules` avec et sans arène.

## Questions à trancher

1. Syntaxe : `arena { … }` (proposé) ou un nom d'arène (`arena frame { … }`) ?
2. Sûreté : copie à la sortie par le runtime (proposé), ou erreurs d'échappement du compilateur (plus strict, bien plus de travail) ?
3. Allocation : ambiante (A, proposé) ou lexicale (B) ?
4. Notice quand une valeur d'arène est copiée hors du bloc : oui ou non ?
5. Temporaires de pipeline : reporter (proposé) ou faire ?
