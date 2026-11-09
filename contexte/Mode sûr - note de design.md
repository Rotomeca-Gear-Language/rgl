# Mode sûr des trains — note de design

Statut : **proposition, rien n'est implémenté.** Issue de trois conseils (2026-10-26) et de ta précision d'intention : les trains existent pour la sûreté, la gestion d'erreur est le centre de RGL. À valider avant tout code.

## Le constat

- La garantie visée : *si une chaîne ne renvoie pas d'erreur, elle n'en cache aucune*.
- Elle ne dépend pas de la forme gear → guard → emit. Elle découle de l'analyse d'erreurs que le compilateur fait déjà (`GearSymbol.Errors`, un guard sans rail fait échouer le train).
- Mais le mode libre ne **dit** rien : un train dont des erreurs de guard ne sont pas gérées compile sans aucun avertissement et s'arrête avec le code 1. Vérifié au compilateur le 2026-10-26 :

```
train |> wallet |> pay(5) |> take("sword") |> done;
```

avec `pay` qui peut échouer (`NotEnoughGold`) et `take` (`OutOfStock`) : aucune erreur, aucun avertissement.

## Décision proposée

- **Le design libre reste la base.** Gear, guard et emit restent des capacités, pas des étapes obligatoires d'une chaîne (une chaîne imposée forcerait des trains quasi identiques pour des fonctions quasi identiques).
- On ajoute **un mode opt-in** : un train (ou un fichier, ou une clé de `rgl.json` — à choisir) marqué `safe`. Le compilateur y vérifie un *certificat* ; il ne change ni la syntaxe des pas ni le C++ généré.
- Gear → guard → emit devient un idiome documenté, au plus un avertissement.

## Ce que le mode sûr refuse (à valider)

1. **Erreur non gérée.** Un pas peut échouer et aucun rail ne traite chacune de ses erreurs. Faire échouer le train s'écrit explicitement ; le silence n'est plus permis.
2. **Chemin sans fin.** Un chemin, rails compris, ne se termine pas par `done`, un emit ou un échec explicite.
3. **Résultat jeté.** Le résultat d'un guard est ignoré (même esprit que W060 pour `spawn`, ici une erreur).
4. **Appel non sûr.** Le train appelle un train qui n'est pas lui-même `safe`.
5. **Boucle sans borne** (optionnelle). Un `*` ou un `^N` sans limite. La boucle de jeu `playRound*` de la démo serait à réécrire ou à exempter.

### Exemple refusé (compile aujourd'hui)

```
train |> wallet |> pay(5) |> take("sword") |> done;
```

Message visé (code à attribuer) :

```
erreur : ce train peut échouer avec NotEnoughGold, OutOfStock
  --> main.rgl:18:7
   |  pay(5)        peut échouer : NotEnoughGold    ← non gérée
   |  take("sword") peut échouer : OutOfStock       ← non gérée
   = aide : ajoute un rail : pay(5) !> [explain |> done]
```

### Exemple accepté (compile aujourd'hui, syntaxe actuelle)

```
gear explain(NotEnoughGold | OutOfStock e): int {
    return match (e) {
        NotEnoughGold n |> 1;
        OutOfStock s |> 2;
    };
}

train |> wallet |> pay(5) !> [explain |> done]
      |> take("sword") !> [explain |> done] |> done;
```

Le `match` couvre les deux erreurs (E082 le vérifie déjà). Le mode sûr n'ajoute donc que la présence des rails et la fin des chemins.

## Limites à écrire noir sur blanc

- **Un guard peut ne rien garder.** Un guard qui valide mal reste « sûr » : la garantie porte sur les erreurs routées, pas sur la qualité des tests.
- **Les arrêts hors garantie.** Dans un guard, un index hors limites, un débordement contrôlé et `as!` deviennent des erreurs du guard ; dans un gear ordinaire ils arrêtent le programme. Le certificat doit dire que ces arrêts sont hors de son périmètre.
- **Ce que restaure `^N`.** Le point de reprise stocke la valeur qui entrait dans l'étape ; une reprise rejoue l'étape avec cette valeur, sans annuler les effets déjà faits (console, fichier). Les valeurs à cycle de vie sont refusées devant un `^N` (E137). Ces règles sont d'après la documentation du dépôt, à relire dans le code avant de les afficher.
- **Un handler qui échoue.** Quelle règle pour `fail` dans un rail, et où la chaîne s'arrête-t-elle ? À trancher.
- **Frontières.** Comment les erreurs traversent `||>`, `@task` et les promesses ; comment l'ensemble d'erreurs d'un train public se déclare à travers fichiers et exports.

## Pas dans cette note

- **Routage par type d'erreur** (`!> [NotEnoughGold |> …; OutOfStock |> …]` avec exhaustivité) : décision de design ouverte, indépendante du mode sûr. Sans lui, le handler fait un `match`, qui vérifie déjà l'exhaustivité.
- **Trains génériques ou paramétrés** (pour éviter la duplication) : n'existent pas aujourd'hui, seuls les gears et les molds sont génériques. Un train appelable comme étape existe.
- **Carte des échecs** (`rgl check --errors` — la commande `check` n'est pas encore disponible — et indices dans l'éditeur) : complément visible du mode sûr, à décider à part.
- Pas de nouvelle syntaxe avant le gel.

## Critère d'abandon

Si, une fois les règles écrites sur deux petits trains (un accepté, un refusé) puis sur la démo ETL, le mode sûr ne rejette rien d'utile que le mode libre accepte, on l'abandonne et on garde seulement la carte des échecs.

## Prochaines étapes proposées

1. Valider ou ajuster les cinq règles ci-dessus.
2. Écrire la démo ETL (JSON → validation avec rails par élément → accumulation → écriture) en mode sûr, sur le papier d'abord.
3. Seulement ensuite : implémentation des diagnostics (peu de code, au-dessus de `GearSymbol.Errors`).

## Ce qui est implémenté (2026-10-26) et les limites du certificat

Clés de `rgl.json` : `"safe": true` (erreurs) et `"loop": "safe"` (boucles bornées, indépendante). Diagnostics : E139 (erreur de pas non traitée par un rail, guard d'un rail compris, `||>` qui écarte ses éléments en silence), E140 (`^N<M>` sans `!` dans un rail), E141 (résultat de `spawn` ou `await` ignoré), W065 (rail qui finit en succès sans lire l'erreur ; `[ignoreError]` sur le handler le fait taire), E142 (`g*` ou `^N` sans limite, `goto` ; avec `loop`), E143 (un `@fire` atteint par un pas), W066 (rail qui fait échouer le train avec une autre erreur sans lire celle reçue), I002 (le certificat : trains et rails vérifiés, rails qui n'ont pas lu leur erreur). E137 sur un mold `error` à cycle de vie, dans tous les modes.

**Ce que le certificat garantit** : dans un train, chaque pas qui peut échouer a un rail qui traite ses erreurs, ou le train échoue par un `|> fail` écrit. Une chaîne qui ne renvoie pas d'erreur n'en a donc caché aucune *au sens de l'analyse du compilateur* (`GearSymbol.Errors`).

**Ce qu'il ne garantit pas** :
- Un guard qui valide mal reste « sûr » : la garantie porte sur les erreurs routées, pas sur la qualité du guard.
- Un handler qui avale l'erreur (`done`, emit sans échec) est un traitement écrit par un humain ; W065 n'attrape que l'oubli de lire l'erreur, et se contourne (`[ignoreError]`, ou lire le paramètre pour rien).
- Les arrêts hors guard (index hors limites, débordement contrôlé, `as!` dans un gear ordinaire) arrêtent le programme et sont hors du périmètre ; dans un guard ils deviennent des erreurs du guard, donc routées.
- Un tap `@tap(after, …)` qui échoue laisse un état à moitié fait : le gear a déjà agi.
- Une reprise `^N` rejoue l'étape avec la valeur qui y entrait, sans défaire les effets déjà faits (console, fichier) ; les valeurs à cycle de vie sont refusées devant un `^N` (E137).
- Le mode sûr ne regarde que les trains du projet ; les gears ordinaires et les trains de la std ne sont pas vérifiés. Un `@fire` atteint par un pas de train est refusé (E143) ; un gear ordinaire qui en lance un reste libre.
- `fail "texte"` dans un rail perd l'erreur reçue (W066) ; `|> fail` seul, ou après un gear qui la renvoie, l'envoie.

**Critère d'abandon** : sur les huit bancs du dépôt, le mode sûr (`safe` et `loop`) ne rejette rien, parce que le seul qui utilise un guard (`json`) a déjà ses rails. Il rejette en revanche le cas que la note donnait au départ (démo ETL sans rail sur `charger`, E139). Le corpus du dépôt ne tranche donc pas : il faudra la démo textual arena, ou un autre programme écrit sans y penser, pour savoir si le mode sûr attrape de vrais oublis.
