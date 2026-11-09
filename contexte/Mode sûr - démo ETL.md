# Mode sûr — démo ETL sur le papier

Statut : brouillon ; les cas 2, 3 et 5 sont implémentés (E139, E140, clé `safe` de `rgl.json`, 2026-10-26), les messages ci-dessous sont ceux du compilateur à peu de choses près. Les programmes « acceptés » ont été **compilés et exécutés en mode libre** le 2026-10-26 (sortie : `ignoré : b` puis `total : 7`) ; les messages du mode sûr sont visés, leurs codes ne sont pas attribués.

Règles retenues par le conseil du 2026-10-26 pour un handler de rail :
- un `fail` explicite est une fin légitime du chemin ;
- un guard appelé dans un handler porte son propre rail ;
- `Errors` du train = erreurs non gérées du chemin normal ∪ erreurs des `fail` explicites des handlers ;
- un `^N<M>` sans `!` est refusé dans un rail ;
- pas de `resume`.

## 1. Le programme accepté (ETL : JSON → validation par élément → accumulation → affichage)

`ventes.json` : `[{"nom":"a","quantite":3},{"nom":"b","quantite":-1},{"nom":"c","quantite":4}]`

```
mold Ligne { string nom; int quantite; }
error QuantiteInvalide { string nom; }

guard charger(string chemin): [Ligne] {
    var lignes <- json<[Ligne]> chemin;
    return lignes;
}

guard valider(Ligne l): Ligne {
    if (l.quantite < 0) {
        fail new QuantiteInvalide {nom: l.nom};
    }
    done;
}

gear ajouter(int total, Ligne l): int => total + l.quantite;

emit signaler(Exception e): uint {
    `erreur : {e}` -> console;
    return 2;
}

emit ignorer(QuantiteInvalide e): void {
    `ignoré : {e.nom}\n` -> console;
}

emit afficher(int total): void {
    `total : {total}\n` -> console;
}

train "ventes.json" |> charger !> [signaler] ||> valider !> [ignorer] |+> 0 with ajouter |> afficher;
```

Le mode sûr l'accepte : `charger` a un rail qui termine (`signaler` renvoie un code), `valider` a un rail par élément (`ignorer`), le fold ne peut pas échouer (`ajouter` est un gear), `afficher` termine le train.

Le fichier `ventes.json` est introuvable : `charger` échoue avec `IoError | ParseError`, `signaler` les reçoit comme `Exception` (duck typing) et le train rend 2.

## 2. Refusé : erreur non gérée (règle 1, déjà dans la note)

On retire le rail de `charger` :

```
train "ventes.json" |> charger ||> valider !> [ignorer] |+> 0 with ajouter |> afficher;
```

```
erreur : ce train peut échouer avec IoError, ParseError
  --> main.rgl:28:26
   |  charger   peut échouer : IoError, ParseError    ← non gérée
   = aide : ajoute un rail : charger !> [signaler]
```

## 3. Refusé : un handler dont le guard n'a pas de rail (nouveau, règle du conseil)

Le handler écrit dans un fichier de journal avant de terminer. `ecrireJournal` est un guard (une écriture peut échouer). Le mode libre l'accepte sans rien dire (compilé et exécuté le 2026-10-26) :

```
guard ecrireJournal(Exception e): Exception {
    `{e}` -> raw "journal.txt";
    done;
}

train "ventes.json" |> charger !> [ecrireJournal |> signaler] ||> valider !> [ignorer] |+> 0 with ajouter |> afficher;
```

```
erreur : le rail de « charger » peut échouer lui-même avec IoError
  --> main.rgl:33:41
   |  ecrireJournal  peut échouer : IoError    ← non géré, dans un rail
   = aide : donne-lui son rail : ecrireJournal !> [signaler] |> signaler
```

Version acceptée : `charger !> [ecrireJournal !> [signaler] |> signaler]`. Chaque rail termine : le rail interne par `signaler` (code 2), le rail externe par `signaler` (code 2).

## 4. Accepté : un handler qui fait échouer le train explicitement

```
gear garder(Exception e): Exception => e;

train "ventes.json" |> charger !> [garder |> fail] ||> valider !> [ignorer] |+> 0 with ajouter |> afficher;
```

Compilé en mode libre le 2026-10-26 : code de sortie 1. (Un guard dont le corps n'est que `fail e;` ne termine pas un rail : E038 « le rail ne se termine ni par done, ni par fail… » ; la terminaison s'écrit `|> fail`.) Le `|> fail` est une fin légitime. `Errors` du train contient les erreurs de ce `fail` (ici le type d'`e`, `Exception`) et **pas** `IoError` ni `ParseError`, que le handler a traités. Le code de sortie est 1.

Si le handler écrit `fail "échec"` sans utiliser l'erreur reçue, le mode sûr avertit : la cause est perdue.

## 5. Refusé : reprise épuisée qui continue en silence

```
train "ventes.json" |> ^1 charger !> [reparer |> ^1<3>] |> …
```

```
erreur : la reprise « ^1<3> » d'un rail s'épuise en silence
  --> main.rgl:37:42
   = aide : écris ^1<!3> pour échouer à l'épuisement, ou termine le chemin après la reprise
```

## Ce que la démo montre et ce qu'elle ne montre pas

- Le mode sûr rejette des programmes que le mode libre accepte sans rien dire : les cas 2, 3 et 5. Le critère d'abandon n'est donc pas atteint sur cette démo.
- Cas 3 et 5 à vérifier dans le code : `GearSymbol.Errors` d'un guard dans un rail est-il déjà calculé par `_CollectErrors` (le conseil le dit pour les rails) ; `^1<3>` dans un rail est-il bien un `IrResume` avec `Fallback`.
- Points tranchés par le second conseil du 2026-10-26 (à valider) :
  - **`fail` dans une reprise** (`lire !> [reparer |> ^1<3>]`, `reparer` échoue) : la reprise s'arrête, ce n'est pas un tour ; `Errors` reçoit l'erreur de `reparer` seule (celle de `lire` est remplacée), `LoopError` seulement pour `^1<!3>` épuisé ; `reparer` porte son propre rail.
  - **Valeurs à cycle de vie** : le déroulement C++ ferme avant le rail et E137 refuse déjà `^N` devant elles ; une seule règle en plus : un mold `error` ne contient pas de valeur à cycle de vie.
  - **`@tap`** : les erreurs d'un tap `after` sont celles du gear, la règle 1 s'applique ; un tap `after` qui échoue laisse un état à moitié fait (hors garantie). **`@fire`** : refusé dans un train sûr (ni rail ni suivi) ; E133 reste une erreur. `task f(x)` et `spawn` à résultat ignoré : erreur en mode sûr (règle 3).
  - **Handler qui avale l'erreur** (`done` ou emit sans échec) : hors garantie, écrit dans les limites du certificat (« gérée = traitement écrit par un humain ») ; un W « erreur ignorée » quand le handler ne lit pas l'erreur reçue, tu le fais taire en la lisant ou par `[ignore-error]` ; le certificat liste ces handlers ; le W s'abandonne avec le critère d'abandon s'il ne détecte rien d'utile.
- Encore ouvert : l'ordre de fermeture des valeurs quand un `fail` survient pendant un déroulement (le gear de fin ne peut pas échouer, donc probablement sans objet : à vérifier au code).
