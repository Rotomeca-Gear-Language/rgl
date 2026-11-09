# B.4 « Copies, moves et générateur » — découpage en sous-slices

Issu du conseil du 2026-11-04 (cinq conseillers, relecture croisée). Constat : la liste de B.4 est une vingtaine de slices déguisées en une,
elle part de techniques et non de profils mesurés, et plusieurs de ses éléments dépendent d'analyses (résumés interprocéduraux) dont le coût et
le risque sémantique sont élevés. On la découpe en sous-slices livrables, chacune validée par la comparaison des bancs (`expected.txt`) avant la suivante.

## Ce que Clang fait déjà : on ne l'écrit pas dans le compilateur

- **Inliner, coller le corps d'une fonction, dérouler les petites boucles** : Clang -O2 le fait, avec la connaissance de la cible. Le compilateur
  se contente de rendre le C++ inlinable (fonctions `static`, corps visibles, `[[unlikely]]` sur les chemins d'échec).
- **En debug (`-O0`) rien n'est inliné, et c'est voulu** : cela contredit UBSan et la lisibilité du débogage. Le debug se traite en *allégeant* le C++
  émis (ne séquencer que quand une opérande peut en modifier une autre), pas en inlinant.
- **Fusion de boucles** : Clang ne la fait pas. Une même chaîne `||> a ||> b |+> c` est déjà une seule boucle (B.1). Reste à fusionner les parcours séparés
  par une variable que le compilateur voit, et `|+>` avec `[...acc, x]` : seulement si les mesures de B.4a le justifient.

## Les sous-slices

1. **B.4a — Mesure.** Compteurs d'allocations et de copies profondes (`compile --count-allocations`, `bench/compteurs.ps1`), classement des bancs,
   runtime compilé une seule fois pour les tests Zig. *Faite* (voir ci-dessous). Critère de sortie : un classement chiffré des bancs par octets alloués
   et par copies profondes ; sans lui les suivantes restent des hypothèses.
2. **B.4b — Moves et vivacité intraprocéduraux.** CFG et vivacité dans l'IR, moves au dernier usage (`LastUses` étendu), passage des paramètres par valeur
   (petits molds triviaux, paramètres consommés) ou `const&`, invariant écrit et testé : rien ne remplace ni ne libère la variable d'origine pendant l'appel.
   Diagnostic qui liste les copies profondes non éliminées. Garde-fou : le move-only de A1.1 et les arènes de B.3.
3. **B.4c — Algorithmique.** `[...acc, x]` en place quand le compteur de références vaut 1 (`array`, `dictionary`) ; `[K:V]` construit par lot.
   **À trancher avant** : la politique des compteurs de références. L'audit thread-safe de la 5.5 (`string`, `function`, `@task`, pool à vol de travail) est la base :
   non atomique n'est sûr que pour ce qui ne traverse jamais `@task`.
4. **B.4d — Locales.** Récursion terminale en boucle (sans `recursion_guard`), suppression des vérifications prouvées inutiles par analyse
   intraprocédurale seulement, `[[unlikely]]` et fonctions d'arrêt froides.
5. **B.4e — Fusion et rapport.** Fusion de parcours entre chaînes, `|+>` avec `[...acc, x]`, rapport explicatif d'optimisation. Seulement si B.4a a montré
   que des copies ou des allocations intermédiaires dominent un banc.

## Reporté après B.4

Résumés interprocéduraux (pureté, lectures/écritures, alias, échappement), escape analysis interprocédurale, spécialisation statique des trains,
optimisation des erreurs typées, choix du passage des paramètres sensible à l'ABI, relocation par bits, `--native`, micro-optimisations du compilateur C#.
Chacun a un risque réel de dérive sémantique (ordre des effets, positions d'erreur) pour un gain non prouvé : à rouvrir sur un cas mesuré.

## Règles communes

- Chaque sous-slice a sa branche et ses tests ; aucune ne se fusionne sans la comparaison des bancs (sortie identique à `expected.txt`, debug et release).
- Un critère d'arrêt chiffré est écrit avant de coder : seuil de gain minimal sur au moins un banc, sinon on abandonne la passe.
- On compare au C++ écrit à la main sur les bancs qui perdent encore, et on regarde l'assembleur (`--emit-asm`) avant de supposer où Clang perd.
- Le coût en temps de compilation Clang est mesuré (colonne « Compilation » de `bench/run.ps1`).

## B.4a — résultat (2026-11-04, release, heap seulement)

| Banc | Allocations | Octets (Ko) | Copies de tableaux | Éléments copiés |
|---|---:|---:|---:|---:|
| chaines | 35 836 | 390 946 | 22 037 | 5 620 782 |
| particules | 74 | 167 318 | 1 | 0 |
| json | 800 023 | 63 805 | 600 005 | 2 200 003 |
| constantes-* | 7 | 703 | 0 | 0 |
| parcours | 1 | 0 | 0 | 0 |
| recursif, demarrage | 0 | 0 | 0 | 0 |

Lecture : `chaines` est le banc de copies (22 000 tableaux recopiés, 5,6 millions d'éléments : c'est le quadratique des chaînes de la std, `[...acc, x]`),
`json` celui de la quantité de petites copies (600 000), `particules` celui du volume (167 Mo en 74 blocs : des tableaux entiers recréés à chaque frame, plutôt
que des copies). Aucun banc ne copie de texte en profondeur : les textes sont partagés par compteur. `recursif` et `parcours` n'allouent presque rien : leur
écart avec du C++ à la main ne vient pas de la mémoire (à regarder dans l'assembleur avant toute passe). Les chiffres sont dans `bench/results/compteurs-release.md`.

Limites de la mesure : seuls les blocs du tas sont comptés (le pool et les arènes ont leurs allocateurs) ; on ne compte pas par gear (le compteur est global au
programme) ; un banc qui n'utilise pas le composant Memory du runtime (`recursif`) n'écrit pas de bilan et est lu comme zéro.

## B.4b, B.4c, B.4d — résultats (2026-11-05)

- **B.4b** : l'accumulateur d'un `|+>` est reçu par valeur et déplacé : `chaines` passe de 22 037 copies de tableaux à 2 (391 → 84 Mo alloués) ; le temps ne bouge presque pas (dominé par les chaînes, B.6). Le passage des paramètres par valeur pour les gears ordinaires, le CFG et le diagnostic des copies profondes ne sont pas faits.
- **B.4c** : `[...a, x]` reprend le bloc de `a` quand rien ne le relit (variable, accumulateur, champ de tuple) ; `[K:V]` par lots (≈ 275 → ≈ 70 ms sur 100 000 ajouts). Le partage avec compteur de références n'est pas fait.
- **B.4d** : la récursion terminale devient une boucle sans garde (100 millions de tours là où l'ancien code s'arrêtait à 200). **Abandonnés après mesure, sans gain** : les faits d'intervalle (Clang prouve déjà ce que le compilateur aurait prouvé) et les chemins froids (`[[gnu::cold]]`, `[[unlikely]]` : taille et temps identiques). `fib` reste à ≈ 160 ms parce que le garde de récursion (≈ 50 ms) et le contrôle de débordement des additions coûtent : les retirer changerait le sens de la limite de profondeur ou du mode `checked`.
- **B.4e** : deux parcours en deux déclarations qui se suivent se fondent en une boucle (`bench/fusion` ≈ 49 → ≈ 25 ms) ; `--explain-optimizations` rapporte le fait (I004) et le refusé avec raison (W070).
- **Reste de B.4** : les copies de `json` viennent des lectures d'éléments SoA (`gather`), à traiter à part ; passage des paramètres par valeur, CFG et diagnostic des copies profondes (B.4b) non faits.
