# Bancs de mesure

Huit programmes RGL qui servent à mesurer le compilateur et le runtime. Rien d'une optimisation ne s'ajoute au langage sans un chiffre d'ici : on lance les bancs avant, on les relance après, on garde ce qui accélère.

```powershell
./bench/run.ps1                                   # tous les bancs, profil release, 7 lancements chacun
./bench/run.ps1 -Only particules,parcours -Runs 15
./bench/run.ps1 -Mode debug
./bench/run.ps1 -Baseline bench/results/base-release.json   # ajoute la colonne « vs base »
```

Le script reconstruit le compilateur (`-NoBuild` pour l'éviter), compile chaque banc, lance l'exécutable une fois pour **vérifier sa sortie** puis le mesure, et écrit un tableau Markdown et un JSON dans `bench/results/` (`latest-<profil>.*`).

## Les bancs

| Banc | Ce qu'il mesure | Levier visé |
|---|---|---|
| `demarrage` | lancement d'un processus, taille d'un exécutable qui n'utilise que la console | coût de démarrage, taille des binaires, élagage du runtime |
| `recursif` | `fib(37)` : 78 millions d'appels d'un gear `pure`, arithmétique contrôlée, garde de récursion | inlining, contrôles d'overflow prouvés inutiles |
| `parcours` | `[0..<100000000] \|\|> f \|+> 0 with g`, entiers puis flottants | fusion des traversées, vectorisation, futur `\|\|>` parallèle |
| `particules` | 200 000 particules (mold à trois champs, ingots), des frames de guard avec `mut` jusqu'à extinction | move au dernier usage, SoA contre AoS, copies de tableaux |
| `chaines` | gabarits, `join`, `split`, `replace`, `toUpper` sur 500 lignes | fonctions de chaînes de la std (voir les constats) |
| `json` | un catalogue de 100 000 articles écrit en JSON (3 Mo) puis relu | lecture/écriture JSON, allocations |
| `constantes-demarrage` | quatre grosses constantes (deux dictionnaires de 5 000 entrées, un tableau, un tableau fixe) et une lecture de chacune | coût de construction au démarrage, taille de l'exécutable, temps de compilation |
| `constantes-recherche` | dix millions de recherches par clé entière puis par clé texte dans ces dictionnaires constants | débit d'une recherche dans un dictionnaire constant |

`constantes-*` sont générés (`python bench/generer-constantes.py [taille]`, 5 000 entrées par défaut) : le script écrit `main.rgl`, `expected.txt` (calculé en Python) et `rgl.json`.

## Règles

- **Chaque banc a un `expected.txt`**, calculé à part (en Python, jamais copié de la sortie du programme). Un banc dont la sortie ou le code de retour diffère est en échec : mesurer un programme faux n'a aucun sens. C'est aussi le test différentiel qui servira quand une optimisation changera la façon d'exécuter (séquentiel contre parallèle, par exemple).
- **Médiane et minimum** de N lancements après un lancement de chauffe. La colonne « Travail » retire la médiane de `demarrage` (≈ 12 ms sous Windows : création du processus). Le bruit d'une série à l'autre est de l'ordre de 2 % ; au-delà de 10 % la colonne « vs base » le signale.
- **Un banc doit durer au moins quelques dizaines de millisecondes de travail**, sinon la mesure ne dit rien (`particules` a d'abord été écrit avec 10 000 particules : 4 ms de travail, trop peu).
- Les chiffres ne se comparent qu'à matériel, profil, version de Zig et commit connus : le tableau les écrit en en-tête.
- `results/base-release.*` est le relevé de référence du 2026-10-25 (commit `a935fc9`). On le remplace volontairement, jamais par un lancement ordinaire : `cp bench/results/latest-release.json bench/results/base-release.json`.
- Ajouter un banc : un dossier avec `rgl.json`, `main.rgl` et `expected.txt` ; le script le trouve seul. Il imprime ce qui permet de vérifier le calcul sans afficher des milliers de lignes.

## Constats du premier relevé (2026-10-25, Release)

Machine : Core i5-13600KF (20 cœurs logiques), Windows 11, Zig 0.17.0.

| Banc | Médiane | Taille |
|---|---:|---:|
| demarrage | 12,3 ms | 100 Ko |
| recursif | 157 ms | 104 Ko |
| parcours | 103 ms | 369 Ko |
| particules | 78,9 ms | 105 Ko |
| json | 266 ms | 445 Ko |
| chaines | 590 ms | 114 Ko |

- **(Corrigé par la slice B.5 : `chaines` passe à 2 000 lignes et 18 s → ≈ 19 ms ; voir `CLAUDE.md`.)** **Les fonctions de chaînes de la std étaient quadratiques, et pas qu'un peu.** `chaines` met 0,59 s pour 500 lignes (11,5 Ko de texte) et 18 s pour 2 000 lignes : une taille ×4 coûte ×31. La première version, 200 000 lignes (4,4 Mo), ne terminait pas en plus de cinq minutes. Causes lues dans `std/extentions/strings_ext.forge`, `collections_ext.forge` et `runtime/src/string.cpp` : `substring`, `toUpper`, `reverse` et `join` reconstruisent le texte entier à chaque caractère ou élément (`` `{acc}{s[i]}` ``) ; chaque `s[i]` coûte O(n), car `string::at` appelle `length()` (qui parcourt tous les octets pour compter les points de code) avant `character(rang)` (qui les reparcourt) ; et `split` recopie le tableau des morceaux à chaque séparateur. Sur 500 lignes, mesuré séparément : `replace` ≈ 0,4 s, `split` ≈ 0,2 s, `toUpper` ≈ 0,1 s. Ces fonctions devraient être natives (runtime C++) ou écrites en une passe.
- **Le gain restant sur `particules` est en grande partie ailleurs que dans les threads** : 67 ms de travail pour 200 000 particules sur 70 frames, un seul cœur utilisé.
- `parcours` traite 200 millions d'éléments (100 M entiers, 100 M flottants) en ≈ 90 ms de travail, soit moins de 0,5 ns par élément : la boucle est déjà fusionnée et rapide.
- Les exécutables vont de 100 Ko (console seule) à 445 Ko (JSON).

## Constats sur les grosses constantes (2026-10-26, Release, commit `e4716d7`, 5 000 entrées)

Mesuré pour juger l'utilité d'une précompilation plus poussée des `const [K:V]` et `const [T]` (slice A1.2A). Référence : le même programme écrit à la main en C++ (`std::array` triés `constexpr` et `std::lower_bound`, mêmes options de compilation Zig), sortie identique.

| | RGL aujourd'hui | C++ à la main | Écart |
|---|---:|---:|---:|
| Compilation (Clang) | 104 s | 1,3 s | ×80 |
| Taille de l'exécutable | 1 494 Ko | 273 Ko | ×5,5 |
| Démarrage (médiane, `constantes-demarrage`) | 26,6 ms | 11,3 ms | +15 ms |
| 20 millions de recherches (`constantes-recherche`) | 496 ms | 469 ms | +6 % |

- **La recherche n'est pas le problème** : un `flat_map` trié se cherche déjà par dichotomie, comme le tableau trié de la référence (+6 %, ≈ 24 ns par recherche). Un hash parfait n'apporterait presque rien, et changerait l'ordre d'itération du dictionnaire (aujourd'hui trié par clé).
- **Le coût est dans la construction** : les constantes sont écrites en C++ comme `dictionary<…>{{k, v}, …}`, construites à l'exécution depuis une `initializer_list`. Clang met ≈ 100 s sur 286 Ko de C++ (le transpiler, lui, 0,5 s), le binaire est 5 fois plus gros et chaque lancement paie 14 ms de construction. Une constante écrite en tableau `constexpr` trié (tri fait par le compilateur C#, doublons refusés à ce moment) supprimerait les trois coûts.
- Le temps de compilation passe de la seconde à la minute-et-demie avec quelques milliers d'entrées : un `import json` d'un fichier de données de taille ordinaire est concerné.

## Ce qui n'est pas encore mesuré

- La mémoire crête et le nombre d'allocations.
- Une référence en C++ écrit à la main (la mesure du 2026-10-20 sur 10 000 particules en donnait une : travail ≈ 0,5 ms contre ≈ 49 ms alors, ≈ 2,6 ms après les optimisations de la branche `optimisations`).
- Linux : le script ne lance que l'exécutable de l'hôte et n'a été essayé que sous Windows.
- Les bancs d'E/S sur disque restent dans le cache du système d'exploitation : le banc `json` mesure du CPU et des allocations, pas le disque.

## Grosses constantes et datasets (slice B.6)

Une constante de fichier de 64 éléments ou plus, tous connus à la compilation (tableau, dictionnaire à clés entières ou textes, dataset), s'écrit comme une table `constexpr` rangée dans l'exécutable et recopiée au démarrage (`Emit/CppEmitter.Tables.cs`). Mesures (release) : `constantes-demarrage` (5 000 entrées par constante) 37 s → 5,4 s de compilation et 1,24 Mo → 0,65 Mo ; `dataset` (5 000 cartes à quatre champs, `bench/generer-dataset.py`) 21 s → 5,3 s et 1,37 Mo → 0,63 Mo. Le temps de démarrage ne change pas (dominé par le lancement du processus).
