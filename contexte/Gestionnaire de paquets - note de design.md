# Gestionnaire de paquets RGL : note de design (étape 0)

Statut : **proposition validée par l'utilisateur le 2026-11-13**, aucun code écrit. Les points marqués « décision de l'utilisateur » sont tranchés ; les autres sont des choix de Claude, **à faire valider**.

## Objectif et contrainte

Un système de dépendances à la npm/Cargo pour RGL **sans rien héberger** (pas de serveur, pas de site payant) : le dépôt Git de l'auteur est le registre, comme Go ou Deno. Les auteurs publient sur GitHub, GitLab ou Codeberg.

## Décisions de l'utilisateur

1. **Une seule version par paquet dans tout le programme.** Deux versions d'une même dépendance ne coexistent pas (à rouvrir plus tard : la portée par `(URL, version)` et un nom unique de programme `pkg:<url>@<version>.fichier` le permettraient).
2. **Versions exactes seulement** (`1.2.0`), pas d'intervalles.
3. **Les `forge: extend` d'un paquet s'appliquent à tout le programme, comme ceux du projet** (changé le 2026-11-13) : sans conflit, rien à écrire ; en cas de conflit de noms on départage par le namespace (`v.<Mon/Ns>f(a)`, mécanisme existant) ; si deux paquets ont le même namespace, on ajoute le paquet : `v.<Mon/Ns@"github.com/alice/json">f(a)`.
4. **Le C++ d'une dépendance** (blocs `cpp`, `.rgl.cpp`, descriptors, fichiers `native`) est refusé par défaut ; il faut inscrire le paquet dans `package-trust.cpp` du projet racine (clé `package-trust`, changée le 2026-11-13 : `cpp-trust` ne parle que de fichiers `.forge`).
5. **Syntaxe d'import** : `import package json;` quand le nom est sans ambiguïté, sinon `import package "github.com/alice/json" as AliceJson;`.

## Le paquet

Un paquet est un **dépôt qui est une box** : à la racine, un `rgl.json` (`name`, `version`, `description`, `license`, et ses propres `dependencies`) et un `index.forge` (kind `index`) qui `export` le public. Les règles de visibilité d'une box sont inchangées, et la portée de fichiers est une `FileScope` opaque comme celle d'une box (deux paquets peuvent avoir des fichiers de même nom).

- **Identité** : l'URL sans schéma (`github.com/alice/json`). C'est elle qui évite le squattage de noms.
- **Version** : le tag Git `vX.Y.Z`. `rgl publish` vérifie qu'il est égal à `version` de `rgl.json`.
- **Nom court** : le champ `name` de `rgl.json` du paquet (un identifiant RGL), gardé dans le lock. Il sert à `import package json;`.

## Déclarer et importer

Le manifeste du projet (et celui de chaque paquet pour ses propres dépendances) :

```json
"dependencies": {
  "github.com/alice/json": "1.2.0",
  "gitlab.com/bob/geometry": "2.0.1"
}
```

Les imports se résolvent **contre les dépendances du manifeste du fichier qui importe** (celui du projet pour le projet, celui du paquet pour un fichier du paquet) :

- `import package json [as J] [only {a, b as c}];` : le nom court désigne la dépendance dont le `name` est `json`. Si deux dépendances du même manifeste ont ce `name`, c'est une erreur qui conseille la forme longue.
- `import package "github.com/alice/json" [as AliceJson] [only {…}];` : l'URL est écrite entre guillemets et doit être dans le manifeste ; `as` est obligatoire quand le nom court serait ambigu.
- Un nom de paquet ne peut pas être `std`.

### Un seul exemplaire par programme

Tous les manifestes (racine, puis paquets, en profondeur) sont lus avant de lier. Si deux d'entre eux demandent des **versions différentes de la même URL**, c'est une erreur qui dit qui demande quoi (« `github.com/alice/json` : 1.2.0 demandé par le projet, 1.3.0 demandé par `geometry` »). Le projet est libre de corriger en changeant la version dans son manifeste. Un forçage explicite (`overrides`) est reporté.

Une dépendance cyclique (A dépend de B qui dépend de A) est une erreur.

## Les extensions d'un paquet

Les `forge: extend` d'un paquet sont chargés d'office et valent pour **tout le programme**, comme ceux du projet et de la std (`SourceGraph.Loader.LoadExtensionFiles` les cherche dans chaque paquet). Trois cas, du plus simple au plus précis :

1. **Aucun conflit** : `xs.somme()` marche, d'où que vienne l'extension.
2. **Conflit de noms** (deux extensions donnent le même nom au même type) : E119 à l'appel comme aujourd'hui, et on choisit par le namespace que chaque `forge: extend` déclare : `xs.<std/collections>somme()`.
3. **Conflit de namespace** (deux paquets déclarent le même `namespace`) : on ajoute le paquet, entre guillemets, après un `@` : `xs.<Mon/Ns@"github.com/alice/json">somme()` (aussi `Type.<Mon/Ns@"…">NOM`). Le texte entre guillemets est l'adresse du paquet (celle du manifeste) ; un paquet inconnu ou qui n'a pas ce namespace est E119.

Conséquences : E123 (namespace unique) ne vaut plus qu'**à l'intérieur d'un paquet ou du projet** (deux paquets peuvent avoir le même namespace) ; `std` reste hors paquet, son namespace s'écrit sans `@`. Comme la version d'un paquet est unique dans le programme (décision 1), l'adresse suffit à le désigner. Pas encore tranché : la qualification d'un fichier `forge: extend` du projet lui-même (aucun `@`, ou `@""` ?) — je propose aucun `@`.

## Le C++ des dépendances et la confiance

Décision de l'utilisateur (2026-11-13) : **`cpp-trust` ne concerne que des fichiers `.forge` du projet** ; ce que le projet autorise à ses paquets s'écrit dans une clé à part, `package-trust`, un objet de deux listes d'adresses :

```json
"package-trust": {
  "binaries": ["github.com/alice/json"],
  "cpp": ["github.com/alice/json"]
}
```

- `binaries` : le paquet garde ses binaires de bibliothèques (`dll so dylib a lib obj o`) à l'extraction (voir plus bas).
- `cpp` : ses blocs `cpp {…}`, ses `.rgl.cpp`, ses descriptors et ses `forge: native` sont admis ; sans cette entrée ils sont refusés (E150). `cpp` n'implique pas `binaries`.
- Seul le **projet racine** autorise : un paquet ne se donne pas la confiance et celle d'un paquet ne s'étend pas à ses dépendances. `no-cpp: true` et `no-trust` s'appliquent aux paquets comme au reste.
- Une bibliothèque précompilée (`library:` d'un `forge: native` ou d'un descriptor) d'un paquet qui n'est pas dans `binaries` est refusée (E161) ; `rgl add` liste les fichiers natifs d'un paquet quand on l'ajoute. Un paquet de confiance qui livre une `.a` la lie comme celle du projet.
- `NativePolicy` reste commun à `SourceGraph`, au binder, à `BuildPipeline` et au LSP.

## Ce qui n'est pas extrait

Décisions de l'utilisateur (2026-11-13) : un paquet livre des sources, donc à l'extraction on écarte, sans erreur, ce qui n'a rien à y faire.
- **Toujours** : les extensions `exe msi bin pdb zip 7z tar gz` et les dossiers `.git`, `.github`, `node_modules`, `out`, `bin`, `obj` (à toute profondeur).
- **Sauf si `package-trust.binaries` autorise le paquet** (`"package-trust": {"binaries": ["github.com/alice/json"]}`) : les binaires de bibliothèques `dll so dylib a lib obj o`. Les garder ne fait pas des exécutables, qui restent écartés.
- L'empreinte du lock se calcule **après** ce tri ; un paquet de confiance a donc un autre contenu, un autre dossier du cache (`<version>+native`) et `"trusted": true` dans son entrée du lock ; changer `package-trust` fait reprendre le paquet (ou est E157 avec `--locked`).
- Une notice dit combien de fichiers ont été écartés, par raison. Côté auteur, `export-ignore` de `.gitattributes` fait déjà la même chose dans l'archive d'un tag.

## Récupération et intégrité

- **Téléchargement** : archive `.tar.gz` par HTTPS — GitHub (`codeload.github.com/<o>/<r>/tar.gz/refs/tags/v<version>`), GitLab (`/-/archive/v<version>/…`), Codeberg (`/archive/v<version>.tar.gz`). Un autre hôte est refusé au début. Lecture par `System.Formats.Tar` et `GZipStream` : pas de `git` requis, sauf pour `rgl publish`.
- **Extraction sûre** : pas de lien symbolique, aucun chemin qui sort du dossier, taille totale et nombre de fichiers plafonnés, dossier racine de l'archive retiré.
- **Hash** : SHA-256 de **l'arbre extrait** (chemins triés, séparateurs normalisés, contenus), jamais de l'archive : les archives des forges ne sont pas garanties identiques d'un téléchargement à l'autre.
- **`rgl.lock`** (JSON, à versionner) : pour chaque paquet, `source`, `version`, `name`, `hash`, `dependencies` (les URL qu'il demande). Un paquet absent est résolu et écrit au lock ; un hash qui ne correspond pas est une erreur ; `--locked` interdit toute modification (pour la CI).
- **Cache** : `<cache>/packages/<hôte>/<chemin>/<version>/` avec son hash. `rgl cache clear` le vide comme le reste (le build suivant retélécharge) et `--size` le compte.
- **Hors ligne** : un paquet déjà en cache et au lock ne demande aucun réseau.
- Pas de scripts d'installation, ni de hooks : un paquet ne s'exécute jamais avant la compilation (la politique native garde le C++).

## Testabilité

Une `IPackageSource` (récupérer l'archive d'une URL et d'une version) injectée par `CliContext`, comme `ICompilerToolchain` : les tests passent par des paquets écrits dans un dossier temporaire, sans réseau. `HttpPackageSource` est la seule qui parle HTTPS.

## Réalisation prévue (slices)

1. **Récupération et lock** : `Project/Packages/` (`PackageManifest`, `PackageLock`, `PackageSource`, `PackageCache`, `PackageResolver`) ; `ProjectManifest.Dependencies`.
2. **Liaison** : `import package` (parser, `AST/Files/ImportDeclaration`, `ImportKind.Package`), `SourceGraph` (une portée par paquet, `FileScope`), `SourceModule.Name` = `pkg:<url>.<fichier>` et nom C++ dérivé, `NativePolicy` (clé `package:`), extensions des paquets globales et namespace qualifié `<ns@"paquet">`.
3. **CLI** : `rgl add <url>@<version>`, `remove`, `restore` (selon le lock), `tree`, `outdated`, `update`, `publish`.
4. **Éditeur** : le LSP lit les paquets en cache (`ProjectIndex`, complétion, survol, définition, actions rapides « rgl restore »).
5. **Index de découverte** (dépôt `rgl-index`, GitHub Pages, `rgl search`) — plus tard.

## Codes proposés

E155 paquet ou version introuvable (ou téléchargement impossible) · E156 hash différent du lock · E157 manifeste de paquet invalide (nom, version, index absent) · E158 conflit de versions d'un même paquet · E159 dépendance cyclique entre paquets · E160 nom de paquet ambigu ou inconnu dans un `import package` · E161 bibliothèque précompilée refusée. La politique native réutilise E150.

## Pas dans cette étape

Intervalles de versions, coexistence de deux versions, dépendances de développement, versions retirées (yank), signature, miroir, paquets de sous-dossier d'un dépôt, hôtes autres que GitHub/GitLab/Codeberg, workspaces, l'index de recherche.
