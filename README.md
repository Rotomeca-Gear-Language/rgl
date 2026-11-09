# RGL — le compilateur

**RGL** (*Rotomeca Gear Language*) est un langage compilé dont le compilateur, `rgl`, est écrit en C# (.NET 10).
Un programme RGL est **transpilé en C++**, puis compilé par Clang, fourni par la chaîne **Zig** que `rgl setup` installe
pour vous : il n'y a rien d'autre à installer, ni compilateur C++, ni SDK.

> Version **0.0.1**. Le langage et le compilateur évoluent vite : attendez-vous à des changements.

## Un langage organisé en trains

Un programme RGL ne s'écrit pas comme une suite d'instructions mais comme un **train** : une valeur qui traverse des
étapes (`|>`). Chaque étape est un *gear* (une fonction), un *guard* (une fonction qui peut échouer) ou un *emit*
(qui termine le train). Quand un guard échoue, un **rail** (`!> [ … ]`) prend le relais.

```rgl
mold Vec2 { double x; double y; }

gear pure longueur2(Vec2 v): double => v.x * v.x + v.y * v.y;

guard nombre(string texte): int => texte as! int;

guard positif(int n): int {
    if (n < 0) {
        fail "négatif";
    }
    done;
}

emit afficher(int n): void {
    `Carré : {n * n}\n` -> console;
}

emit signaler(Exception e): uint {
    `Erreur : {e}\n` -> console;
    return 2;
}

train "42" |> nombre |> positif !> [signaler] |> afficher;
```

```text
$ rgl exec main.rgl
Carré : 1764
```

Avec `"-4"` au départ, le guard `positif` échoue et le rail écrit `Erreur : négatif (main.rgl:9:9)` : l'erreur porte la
position du `fail` dans le source.

### Ce que le langage offre

- **Types** : entiers et flottants de toutes tailles, `bool`, `string`, durées (`1.5s`), dates, regex (`/motif/flags`),
  symboles, types bornés (`int<0..100>`), *ingots* (types de marque), énumérations, unions (`int | string`), résultats
  (`T ! E`), nullables (`T?`), tuples, tableaux (`[T]`, `[T;N]`, `[T+]`), dictionnaires (`[K:V]`), intervalles (`[0..10]`).
- **Molds** : structures avec valeurs par défaut, spread (`{...pos, x: 3.0}`), typage structurel, opérateurs et
  conversions surchargés, mold à cycle de vie (`mold Handle -> fermer`).
- **Erreurs sans exceptions** : `guard`, `fail`, rails, erreurs typées (`error Nom { … }`), propagation implicite.
  Un **mode sûr** (`"safe": true`) refuse tout train qui cacherait une erreur.
- **Parcours et accumulation** : `||>` (par élément), `|!|>` (strict), `|+>` (accumulation), boucles de guard, points de
  reprise `^N`.
- **Génériques** monomorphisés, **extensions** (`extend T { … }`), surcharge de gears, paramètres par défaut,
  `params [T]`, annotations (`@deprecated`, `@tap`, `@before`, annotations déclarées).
- **Mémoire** : `mut` pour modifier le contenu, déplacements au dernier usage, tableaux de molds stockés en colonnes
  (SoA) selon l'usage réel des champs, arènes et pool (`arena { … }`), `ptr<T>` / `unsafe` quand il le faut.
- **Asynchrone coopératif** : `@async`, `await`, `sleep`, `whenAll`, `@task` pour le travail bloquant, `@fire`.
- **Fichiers et données** : `raw`, `json`, `xml`, `custom`, flux `file`, `import json` / `import xml` à la compilation,
  `dataset` (tables de données typées, extensibles par mods), `precast`.
- **Interopérabilité C/C++** : `forge: native` (en-têtes C lus par Clang), `.rgl.cpp` (composants C++ à la main),
  `forge: descriptor` (classes C++ vues comme des types RGL), blocs `cpp { … }` intégrés, politique de confiance
  (`@trusted`, `no-cpp`, `cpp-trust`).
- **Web** : serveur et client HTTP/1.1 et HTTP/2, HTTPS, WebSocket, pages **Paper** (HTML + RGL, réactives) compilées
  vers un exécutable et du WebAssembly.
- **Bibliothèque standard** écrite en RGL et embarquée (`use <Math>;`) : console, maths, aléatoire, chaînes Unicode,
  collections, dates et fuseaux, JSON / MessagePack / CBOR / Markdown / PNG, cryptographie (SHA-2, AES-GCM, Ed25519,
  JWT), réseau, processus, terminal, tests, etc.

## Installation

1. Récupérez l'exécutable `rgl` (un seul fichier, .NET inclus) et placez-le où vous voulez.
2. **Double-cliquez dessus** (Windows) ou lancez :

   ```bash
   rgl setup
   ```

   `rgl setup` télécharge Zig et, sous Windows, OpenSSL (vérification SHA-256) dans le dossier utilisateur de RGL
   (`%LOCALAPPDATA%\rgl\tools`, ou `~/.cache/rgl/tools`), puis propose d'**ajouter `rgl` au PATH** pour pouvoir taper
   `rgl` de n'importe où. `--add-to-path` accepte sans poser la question, `--no-path` ne propose rien,
   `--force` réinstalle.

Le runtime C++ et la bibliothèque standard sont embarqués dans l'exécutable : rien d'autre à déployer.
Windows et Linux (x86_64, aarch64) sont pris en charge.

## Démarrage rapide

```bash
mkdir bonjour && cd bonjour
rgl init            # rgl.json, main.rgl, src/first.forge  (rgl init --web : serveur HTTP + page Paper)
rgl exec main.rgl   # compile puis lance
```

Un projet, c'est un fichier d'entrée **`.rgl`** (le train d'entrée) plus des fichiers **`.forge`** (gears, molds, trains
nommés…) retrouvés **par leur nom** dans le projet : `import Commandes;` trouve `Commandes.forge` où qu'il soit.
Les dossiers `Nom.box` sont des modules opaques, `Nom.tools` des dossiers d'outils.

## Commandes

| Commande | Rôle |
| --- | --- |
| `rgl init [--web]` | Crée un projet |
| `rgl compile <fichier.rgl>` | Compile ; l'exécutable est écrit dans `out/<profil>/` |
| `rgl exec <fichier.rgl>` | Compile puis lance (`-- args` pour les arguments du programme) |
| `rgl check` | Bilan de l'intégration C++ du projet |
| `rgl format [chemin…] [--check]` | Met en forme les `.rgl` et `.forge` (aucun réglage) |
| `rgl doc check` / `rgl doc build` | Vérifie / exporte la documentation `///` (JSON et XML) |
| `rgl descriptor <en-tête> --class <Classe>` | Écrit le squelette d'un `forge: descriptor` |
| `rgl precast export` | Écrit les precasts en JSON pour les outils |
| `rgl cache --size` / `rgl cache clear` | Taille du cache, ou vidage (Zig est conservé) |
| `rgl setup` | Installe Zig / OpenSSL et propose le PATH |
| `rgl lsp` | Serveur de langage pour les éditeurs |

Options de `compile` et `exec` : `-d <dossier>`, `--release` (le plus rapide), `--release=size` (le plus petit),
`--profile <nom>` (profil de `rgl.json`), `--emit-cpp` (s'arrête au C++), `--emit-asm`, `--explain-optimizations`,
`--explain-vectorization`, `--count-allocations`.

### Le manifeste `rgl.json`

```json
{
    "name": "bonjour",
    "version": "1.0.0",
    "rgl": "0",
    "exclude": ["out"]
}
```

Clés courantes : `overflow` (`"checked"` par défaut, ou `"unchecked"`, ou un objet par profil), `recursion` (limite de
profondeur, 200 par défaut, ou `"infinite"`), `safe` (mode sûr des trains), `loop: "safe"` (boucles bornées),
`icon`, `assets` (fichiers copiés à côté de l'exécutable), `profiles` (profils de build personnalisés),
`no-cpp`, `no-trust`, `cpp-trust` (politique du code C++ natif).

## Éditeur : extension Visual Studio Code

Le dossier [`editors/vscode`](editors/vscode) contient l'extension RGL : coloration par le sens, autocomplétion avec
import automatique, survol et documentation, renommage, références, formatage, actions rapides, hiérarchie d'appels,
débogage (points d'arrêt dans les `.rgl`), éditeur de datasets, pages Paper, tâches et explorateur de tests. Elle lance
le compilateur en serveur de langage (`rgl lsp`).

## Compiler le compilateur

Prérequis : le [SDK .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet build                      # le compilateur
dotnet run -- <arguments>         # le lancer
dotnet test rgl.slnx              # les tests (xUnit, tests/rgl.Tests)

cd tools
dotnet run fetch-tools.cs         # installe la version de Zig de tools/toolchain.json dans tools/zig
dotnet run publish.cs             # construit l'exécutable distribuable dans dist/ (Windows ou Linux)
```

`dotnet run publish.cs -- --rid linux-x64` construit pour un autre système, `-o dossier` change la sortie. C'est un alias
de `dotnet publish rgl.csproj -c Release -r <rid> --self-contained -p:PublishSingleFile=true`.

### Organisation du dépôt

| Dossier | Contenu |
| --- | --- |
| `Cli/`, `Project/`, `Build/` | Ligne de commande, projets et manifeste, pipeline de build et chaîne Zig |
| `Syntax/`, `Parsing/`, `AST/` | Lexer, parseur (descente récursive, Pratt) et arbre syntaxique |
| `Binding/`, `IR/` | Binder (typage, vérifications) et représentation intermédiaire, indépendante du C++ |
| `Emit/` | Émetteur C++ (avec `#line` vers les sources `.rgl`) |
| `Lsp/`, `Docs/`, `Paper/`, `Native/` | Serveur de langage, documentation `///`, pages Paper, interopérabilité C/C++ |
| `runtime/` | Runtime C++23 lié aux programmes compilés (chaînes, tableaux, async, HTTP, JSON…) |
| `std/` | Bibliothèque standard, écrite en RGL |
| `editors/vscode/` | Extension Visual Studio Code |
| `tests/`, `bench/` | Tests (dont golden files) et bancs de mesure |
| `Docs/` | Documentation : [std](Docs/StandardLibrary.md), [HTTP](Docs/Http.md), [Paper](Docs/Paper.md), [interopérabilité native](Docs/NativeInterop.md), [C++ intégré](Docs/InlineCpp.md), [unsafe](Docs/Unsafe.md), [surcharge de gears](Docs/GearOverloads.md), [déplacements](Docs/MoveSemantics.md) |

Les messages, diagnostics et commentaires du projet sont en **français**. Une erreur de Clang ne doit jamais atteindre
l'utilisateur : c'est un bug du transpileur (`build.log` du cache), sauf pour la lecture d'un en-tête C/C++ de l'utilisateur.

## Licence

RGL est distribué sous la **[Licence publique de l'Union européenne (EUPL) v. 1.2](LICENSE)**, une licence à
*copyleft* : vous pouvez utiliser, étudier, modifier et redistribuer le logiciel ; les versions modifiées que vous
distribuez ou fournissez en service réseau doivent rester sous EUPL (ou sous l'une de ses licences compatibles listées
en annexe, dont GPL v2/v3, AGPL v3, LGPL, MPL v2, CeCILL). Le texte fait foi dans le fichier [LICENSE](LICENSE).

Le runtime embarque [CTRE](runtime/third_party/ctre) (Compile-Time Regular Expressions, licence Apache 2.0) ; le compilateur utilise les bibliothèques NuGet `Rotomeca.Core`, `Rotomeca.Rop` et `Rotomeca.Utils`.
