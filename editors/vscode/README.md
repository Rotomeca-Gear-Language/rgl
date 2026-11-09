# RGL pour Visual Studio Code

Coloration par le sens, autocomplétion réelle (la std, les extensions, le projet) avec import automatique, survol, définitions, diagnostics et snippets du langage RGL, tel que le compilateur le connaît à la slice 4.8 (fichiers et imports, `use <Math>`, boxes, génériques, extensions, la bibliothèque standard), pour les fichiers `.rgl` et `.forge`.

## Diagnostics

Si le cache ou la std embarquée manque, l’analyse se met en attente et l’extension propose
« Restaurer la std » une seule fois par session. La restauration utilise les fichiers du
compilateur, sans téléchargement, puis redémarre le serveur de langage. Si vous refusez,
l’éditeur reste utilisable ; après une réparation manuelle, utilisez « RGL: Restart Language Server ».
Cette récupération demande un compilateur incluant le protocole de restauration (extension 0.10.1).

L'extension lance le compilateur en serveur de langage (`rgl lsp`). À chaque modification, il relit le fichier comme le fait `rgl compile` : lexer, parseur, puis binder, chaque étape seulement si la précédente n'a pas signalé d'erreur. Les erreurs et avertissements s'affichent avec leur code (`E003`, `W004`…), le passage souligné et les aides du compilateur. Un fichier `.forge` n'est encore que lu et analysé. Le `rgl.json` du dossier du fichier est pris en compte (`overflow`, `recursion`).

## Autocomplétion

Comme dans un éditeur de C# ou de JavaScript, elle connaît ce qui est visible à l'endroit du curseur et le type de ce qui précède un point :

- les variables du bloc (seulement celles déjà déclarées), les paramètres, les paramètres génériques, les constantes, les gears, molds, enums et ingots du fichier, les noms importés, les erreurs standard, les mots-clés et les types primitifs ;
- après `x.`, selon le type de `x` — déduit même sans type écrit (`var v: new Vec2 {…}`, `var d: longueur(v)`) : les champs d'un mold (spreads et paramètres génériques compris), les variantes d'un enum (`Direction.`), les membres d'un tableau (`length`, `push`) ou d'un dictionnaire (`length`, `add`, `has`), **et les extensions** qui s'appliquent au type de `x` (`xs.sum()`, `texte.trim()` : celles de la std et celles de votre projet, `[int]` ne propose pas ce qui demande des textes), les éléments d'un tuple (`Item1`, ou leur nom), les noms d'un fichier importé (`Geometry.`) ;
- à une place de type (après `new`, `as`, dans les paramètres et le type de retour d'un gear, entre `<` et `>` d'un générique) : seulement des types ;
- sur une ligne `import`, les fichiers du projet (et `box`, `tools`), puis après `import Fichier only {`, les noms du fichier ; sur `use <`, les fichiers et les boxes de la std, puis après `use <Math> only {`, leurs noms ; sur `forge:`, les genres de fichier ;
- **datasets** — `dataset Moves of Move { … }` : après `Moves.`, ses entrées (`attaquer`, `soin`…), `id` (le type de ses identifiants, aussi dans un champ de mold : `Moves.id`), `length` et `find` ; après un identifiant (`joueur.job.`), `get()` et `name()`, et après `get().` les champs du mold ; sur une ligne `dataset Nom`, `of`, `as` et `from`, puis les molds après `of` ou `as` ; les datasets des autres fichiers du projet s'importent en les choisissant, comme les autres noms ;
- après `std.`, **toute la bibliothèque standard**, sans rien importer ; un gear se complète avec ses parenthèses, le curseur entre elles s'il attend des arguments (`sqrt($0)`).

**Import automatique.** Tous les noms de la std se proposent sans les avoir importés, avec « std · Math » à droite : choisir `sqrt` (Tab ou Entrée) écrit `use <Math> only {sqrt};` en tête du fichier (ou ajoute `sqrt` au `use <Math> only {…}` déjà écrit). Il en va de même pour les noms des autres fichiers `.forge` du projet : les noms déclarés par les autres fichiers `.forge` du projet (ceux que le fichier peut importer : le projet, ou la box où il se trouve) sont proposés aussi, avec le nom du fichier à droite. Choisir l'un d'eux ajoute l'import qui manque : `import Fichier only {nom};` après les imports existants (ou l'en-tête, ou en tête de fichier), ou le nom ajouté à l'`import Fichier only {…}` déjà écrit. Un nom de fichier se propose aussi, avec `import Fichier;`.

Chaque proposition montre sa déclaration et le commentaire qui la précède.

## Documentation personnalisée et multilingue

Les commentaires `///` prennent en charge les sections XML (`summary`, `param`,
`returns`, etc.) et les balises personnalisées, par exemple `<attention>…</attention>`.
Leurs noms et attributs sont colorés ; les sections personnalisées apparaissent
au survol et dans la documentation des propositions d’autocomplétion.

Chaque bloc `<lang name="…">` englobe **toute une traduction** :

```rgl
/// <lang name="fr">
/// <summary>Double un entier.</summary>
/// <param name="n">Le nombre.</param>
/// <returns>Le double.</returns>
/// <attention>Exemple de section personnalisée.</attention>
/// </lang>
/// <lang name="en">
/// <summary>Doubles an integer.</summary>
/// <param name="n">The number.</param>
/// <returns>Twice the number.</returns>
/// <attention>Example of a custom section.</attention>
/// </lang>
gear doubler(int n): int => n * 2;
```

Le serveur utilise la langue d’interface du système sur lequel il tourne :
correspondance exacte, puis langue parente (`fr-CA` → `fr`), puis anglais, puis
premier bloc disponible. La langue d’interface de VS Code ne la remplace pas.
En session distante (SSH, WSL, conteneur), il s’agit du système distant. Les commentaires sans `<lang>` restent valables. Un `<lang>` placé
à l’intérieur d’un `summary`, d’un `param` ou d’un autre bloc est signalé par `DOC013`.

Le résumé, les paramètres et le retour sélectionnés apparaissent au survol,
dans les propositions d’autocomplétion et dans l’aide aux appels. Tapez
**`doclang`** puis Tab pour insérer deux blocs complets (français/anglais), ou
**`doccustom`** pour une section personnalisée. Entrée sur une ligne `///`
continue le commentaire sur la ligne suivante.

Ces fonctions demandent le compilateur contenant la prise en charge de `<lang>`.
Après `dotnet build`, vérifiez `rgl.compilerPath` et lancez **RGL : Redémarrer le
serveur de langage**. Pour charger les changements de l’extension depuis les
sources, lancez-la dans un hôte de développement VS Code (`code --extensionDevelopmentPath=editors/vscode`
depuis la racine du dépôt).

## Éditeur de dataset

Un `dataset` s'édite dans un **tableau** : une ligne par entrée, une colonne par champ du mold. Au-dessus de chaque déclaration `dataset Nom …`, le CodeLens **Éditer le dataset** l'ouvre ; la commande « RGL : Éditer le dataset sous le curseur » (palette, ou menu du clic droit) ouvre celui de la ligne du curseur.

- **Les colonnes** sont les champs du mold, avec leur type écrit (`int<-100..100>`, `Element`, `Classes.id`) et une étoile pour ceux qui n'ont pas de valeur par défaut. Pour un `dataset … as Mold`, ce sont les champs que les entrées écrivent, et le bouton **+ Champ** en ajoute.
- **Les valeurs possibles** se proposent dans la cellule : les variantes d'un enum, les entrées d'un autre dataset pour un identifiant (`Classes.mage`), `true` et `false`.
- **Ajouter, dupliquer, supprimer** une entrée ; le tableau refuse d'appliquer un nom d'entrée invalide, réservé (`id`, `length`, `get`, `find`, `name`) ou en double, et un champ obligatoire vide.
- **Appliquer** réécrit les entrées du dataset dans le document, une par ligne, sans l'enregistrer (annulable avec Ctrl+Z). Les commentaires écrits entre les accolades du dataset ne sont pas conservés : le tableau réécrit tout ce qu'elles contiennent.
- **`dataset … from "moves.json"`** : le tableau lit le fichier JSON (cherché par son nom dans le projet) et le réécrit — un objet dont les clés sont les noms, ou un tableau d'objets avec un champ `id`, selon la forme du fichier. Les cellules y sont du JSON (`"Feu"`, `5`) ; un texte qui n'en est pas devient une chaîne.

## Survol, appels et définition

- **Survol** : la déclaration de ce qu'on survole — signature d'un gear (`gear pure clamp<T: number>(T v, T lo, T hi): T`), champs d'un mold ou variantes d'un enum, type d'une variable (même déduit), membre d'un type —, puis le **commentaire écrit juste avant la déclaration** (`//`, `///` ou `/* … */`, sans ligne vide entre eux), et le fichier qui la déclare. Un nom de fichier importé donne son genre et sa description. Un mot-clé ou un type primitif est expliqué en une phrase.
- **Aide à la saisie d'un appel** : en écrivant `f(` puis chaque `,`, la signature de `f` avec le paramètre en cours ; derrière `|>`, la valeur du pipeline compte pour le premier argument.
- **Aller à la définition** (F12, Ctrl+clic) : le gear, mold, enum, ingot, constante, variable, champ ou fichier désigné, dans son fichier.

Tout cela se lit sur l'arbre du fichier, qui se rattrape des erreurs : ça répond pendant qu'on écrit, même quand le fichier ne compile pas.

Rien ne s'écrit à l'appel d'un gear qui modifie la variable qu'il reçoit (paramètre `mut`) : l'éditeur affiche `mut`, grisé, devant chaque argument passé à un paramètre `mut` (indications dans le texte, quand le fichier n'a pas d'erreur).

Le réglage **`rgl.compilerPath`** donne le compilateur à lancer : `rgl` trouvé dans le `PATH` par défaut, ou un chemin vers `rgl.exe`, ou vers `rgl.dll` (lancé avec `dotnet`), par exemple après `dotnet build` dans le dépôt du compilateur :

```json
"rgl.compilerPath": "E:/…/compilateur/rgl/bin/Debug/net10.0/rgl.dll"
```

Le compilateur réglé par un chemin est copié, avec son dossier, dans celui de l'extension avant d'être lancé : le serveur ne verrouille donc pas les fichiers du compilateur, et `dotnet build` peut les remplacer pendant que VS Code est ouvert. Après avoir recompilé le compilateur, la commande **RGL : Redémarrer le serveur de langage** relance le serveur sur la nouvelle version.

## Coloration

La coloration vient de deux sources. La **grammaire** colore ce qui se lit dans le texte (mots-clés, littéraux, opérateurs, déclarations). Le **serveur** colore ce que chaque nom *est*, comme dans C# ou TypeScript (jetons sémantiques), même quand on le retape ailleurs : un ingot, un mold, un enum, une error, un gear, une méthode d'extension, une variable, une constante (en lecture seule), un paramètre, un champ, une variante, un fichier importé, `std` ont chacun leur sorte, et ce qui vient de la std est marqué `defaultLibrary`.

- **Un ingot a la couleur d'un type primitif, en italique** — `ingot score: int;`, `score s`, `score(3)`, `s°` : partout où le nom apparaît, sa couleur et sa forme viennent de ce qu'il est. Les thèmes qui ne connaissent pas les jetons sémantiques retombent sur les couleurs de la grammaire.
- Les couleurs se changent comme celles de tout langage, par `editor.semanticTokenColorCustomizations` (sortes `ingot`, `struct`, `class`, `enum`, `enumMember`, `function`, `method`, `variable`, `parameter`, `property`, `namespace`, avec les précisions `declaration`, `readonly`, `defaultLibrary`).

Le détail de la grammaire :

- Les déclarations : `gear`, `guard`, `emit` (et `pure`) avec le nom du gear, `mold`, `error`, `enum`, `ingot` avec le nom du type, `train` et `!train`, `var`, `mut`, `const`, `undefine`.
- Le contrôle de flux : `if`, `else`, `switch`, `case eval`, `default`, `break all`, `goto`, `match`, `return`, `fail` et `done`.
- Les trains : les étapes `|> gear`, les parcours `||>` et `|!|>`, les accumulations `|+> 0 with ajouter`, les rails `!> [ … ]` (imbriqués), `$ code`, les boucles de guard `g*`, `g*<10>`, `g*<!10>`, les points de reprise `^1 g` et les reprises `^1`, `^1<3>`, `^1<!3>`.
- Les intervalles `[0..10]`, `[0..<10]`, `[0..10 by 2]`.
- Les opérateurs en mots (`and`, `eq`, `as`, `new`, `increment`…) et en symboles (`<-`, `<-|`, `::`, `+<-`, `°<-`, `??`, `?°`, `?.`, `°`, `..`, `...`, `->`, `=>`).
- Les types primitifs, et les noms qui commencent par une majuscule (molds, errors, ingots, enums), dont `Exception`, `CastError` et `LoopError`.
- Les littéraux : nombres (`0xFF`, `0b1010`, `0o17`, `1_000`, `1.5e3`), durées (`200ms`, `2min`), chaînes `"…"` et `m"…"` avec leurs échappements, chaînes brutes `'…'`, templates `` `… {expression} …` ``, symboles `§nom`, `true`, `false`, `null`.
- Les génériques : `gear f<T: number>(…)`, `mold Pair<T, U>`, `<int max>`, les contraintes `number` et `error`, `T is X`, `T accept X`, `f<int>(x)`, `forge: mold`, `import box`, `import tools`.
- Les extensions : `extend T { … }`, `extend<T: number> [T] { … }`, le `this` du premier paramètre, `export`, `namespace Mon/Ns;` et l'appel qualifié `v.<Mon/Ns>f()` (le namespace est coloré comme un nom).
- `ok` et `err`, les formats `console`…, et les mots réservés (`for`, `while`, `class`…) signalés comme invalides.

## Snippets

`train`, `gear`, `gearpure`, `guard`, `emit`, `mold`, `error`, `enum`, `if`, `ifelse`, `switch`, `match`, `rail`, `geargeneric`, `gearnumber`, `moldgeneric`, `import`, `importonly`, `extend`, `extendgeneric`, `namespace`, `nsCall`, `typetest`.

## Installation

L'extension a besoin de sa dépendance `vscode-languageclient` : dans ce dossier, installer les dépendances, faire le paquet, puis l'installer.

```bash
npm install
npx @vscode/vsce package
```

```bash
code --install-extension rgl-0.10.1.vsix
```


## Interopérabilité native et mémoire — jusqu’à A2.4 (extension 0.9.0)

Installer un compilateur RGL qui contient A2.4, puis régler `rgl.compilerPath` sur
son exécutable ou sa DLL. Après une recompilation, utiliser **RGL : Redémarrer le
serveur de langage**. L’extension utilise ce compilateur pour l’analyse ; une mise
à jour de la seule extension ne met pas à jour le compilateur.

- **Liaisons C** (`forge: native`) : propositions des fonctions et types importés,
  signatures, survol et diagnostics des en-têtes. Zig et le runtime doivent être
  installés auprès du compilateur (`tools/zig`, `runtime`), ou Zig désigné par
  `RGL_ZIG`. La copie privée du serveur conserve leur emplacement d’origine.
  Les modifications enregistrées des fichiers `.h`, `.hpp`, `.hxx` et `.hh`
  actualisent les diagnostics des documents ouverts. La définition d’un symbole
  C conduit à l’en-tête de son fichier `.forge` qui référence la bibliothèque.
- **C++** : guards et trains sans corps, descriptors, constructeurs `create`,
  champs et accesseurs, méthodes statiques et signatures génériques spécialisées.
  La navigation des descriptors conserve les positions du fichier original.
  Les fichiers `.rgl.cpp` utilisent la prise en charge C++ de VS Code ; les erreurs
  de compilation C++ restent vérifiées par `rgl check`/la compilation.
- **Mémoire** : coloration et survol de `unsafe`, `ptr<T>`, `uptr<T>`, `char`,
  `delete` et `pack`, complétion et signature de `free()`, déduction du type des
  accès `*p` et des tableaux préparés avec `pack`. Les diagnostics du compilateur,
  dont E147, E148 et W068, apparaissent dans le panneau Problèmes.

Snippets ajoutés : `unsafe`, `ptr`, `uptr`, `ptrmove`, `pack`, `forgenative`,
`forgedescriptor`, `descriptor`, `descriptorgeneric`, `guardnative`,
`unsafenative` et `trainnative`. Les pointeurs allouent directement leur valeur,
par exemple `ptr<int> p: 5;`, à l’intérieur d’un gear `unsafe`.

Voir [mémoire et SoA](../../Docs/Unsafe.md) et
[intégration C++](../../Docs/NativeInterop.md) pour les règles et limites actuelles.
Les blueprints restent différés après la v1.


## Blocs C++ et contrats de confiance — A2.4B (0.10.0)

La coloration C++ est intégrée aux blocs `cpp { ... }` et revient au RGL pour
leurs arguments et leur résultat. Les snippets `cpp` et `trustedpure` proposent
les formes complètes. La déduction des variables reconnaît le retour du bloc ;
les propositions RGL sont suspendues dans son contenu C++.

Le survol d’une fonction `@trusted` distingue le contrat natif attesté de la
pureté vérifiée de sa partie RGL. `unsafe pure` est reconnu.
Le schéma de `rgl.json` décrit `no-trust`, `no-cpp` et `cpp-trust` ; une modification
enregistrée du manifeste actualise les diagnostics des documents ouverts.

La coloration embarquée ne crée pas un serveur IntelliSense C++ pour les fragments.
Les erreurs C++ sont vérifiées à la compilation ; les restrictions du manifeste et
les erreurs de typage RGL sont également vérifiées par le serveur de langage.
Voir [C++ intégré](../../Docs/InlineCpp.md) pour le contrat complet et les exemples.

## Création de mold, éléments obsolètes, navigation et tâches (0.13.0)

- **Créer un mold** : entre les accolades de `new Vec2 { … }`, de `Vec2 p: { … }`, d'un argument `f({ … })`, d'un `return { … }`, d'un champ imbriqué ou d'un élément de tableau, l'éditeur propose les champs qui restent à écrire (`name: ` avec le curseur après les deux-points), **les obligatoires en premier** (sans valeur par défaut, non nullables), et une proposition « champs obligatoires » qui les écrit tous d'un coup avec un emplacement par champ. Les champs déjà écrits ne reviennent pas.
- **Obsolète barré** : un gear marqué `@deprecated("…")` est barré partout où il est employé — dans le code (jeton sémantique `deprecated`), dans la liste de complétion, et dans le diagnostic W061 (étiquette « obsolète »). Le survol dit « Obsolète » avec le message ; la déclaration elle-même reste lisible.
- **Références** (Maj+F12), **surlignage des occurrences** du mot sous le curseur, **renommage** (F2, dans tous les fichiers du projet : un nom de la std, un mot du langage ou un nom invalide est refusé avec la raison ; une variable écrite en raccourci `{x}` devient `{x: nouveau}`), **aller à la définition du type** (Ctrl+F12 sur une variable, un paramètre ou un champ), **aller au symbole dans l'espace de travail** (Ctrl+T, les noms qui commencent par la saisie d'abord, puis ceux qui la contiennent, puis ceux dont les lettres s'y retrouvent dans l'ordre).
- **Plier** les corps de gear, molds, enums, blocs, tableaux, blocs C++ et textes sur plusieurs lignes, les commentaires de bloc, les suites de commentaires `//`/`///` et les suites d'imports ; **étendre la sélection** (Maj+Alt+→) du mot à l'instruction, au corps, puis au fichier.
- **Tâches** : « Terminal > Exécuter une tâche > rgl » propose Compiler, Exécuter, Vérifier et Compiler/Exécuter en release ; un détecteur de problèmes (`$rgl`) place les diagnostics du compilateur dans le panneau Problèmes. Commandes de la palette (`RGL : Compiler le projet`, `Exécuter`, `Exécuter en release`, `Vérifier le projet`) et boutons ▶ et 🔨 dans la barre de titre d'un fichier RGL ; elles agissent sur le fichier `.rgl` actif (enregistré d'abord) ou sur le projet du dossier ouvert. Une tâche personnalisée s'écrit `{ "type": "rgl", "command": "exec", "release": true }` dans `tasks.json`.

Pas encore : formatage du code, actions rapides (corrections proposées), hiérarchie des appels, débogage, explorateur de tests.

## Formatage, actions rapides et en-tête des fichiers forge (0.14.0)

- **Formater le document** (Maj+Alt+F) ou **la sélection** : seuls l'espace en début et en fin de ligne et les lignes vides changent, jamais ce qui est écrit (le serveur compare les jetons avant et après et ne change rien s'ils diffèrent). L'indentation suit le style du projet : un niveau par accolade, crochet ou parenthèse ouverts (une ligne qui en ouvre plusieurs n'ajoute qu'un niveau), `case` au niveau du `switch` et son corps un niveau plus loin (deux `case` qui se suivent restent alignés), une ligne qui continue la précédente (`|>`, `!>`, un opérateur en tête ou en fin de ligne) un niveau de plus, sauf dans une liste entre parenthèses où les lignes `a and` / `b and` restent alignées. Les commentaires de bloc suivent le décalage de leur première ligne, le contenu des textes, gabarits et blocs C++ ne bouge pas, plus d'une ligne vide de suite devient une, et le fichier finit par une fin de ligne (`
` conservé s'il est utilisé). La largeur vient des réglages de l'éditeur (`editor.tabSize`, `editor.insertSpaces`, 4 espaces par défaut pour RGL). Le formatage ne réécrit pas l'espace à l'intérieur d'une ligne ni ne coupe les lignes longues.
- **Actions rapides** (ampoule, Ctrl+;) : **importer** un nom que rien ne désigne et que le projet ou la std déclare (`import Fichier only {nom};` ou `use <Fichier> only {nom};`, comme le fait la complétion) ; **ajouter `description: ''`** quand W005 la signale, **ajouter l'en-tête** `forge: gears` quand E016 le signale ; **supprimer l'import inutilisé** ou le nom inutilisé d'un `only {…}`, et **Organiser les imports** (Maj+Alt+O) qui retire tous ceux que rien n'emploie. Un fichier qui contient des extensions n'est jamais jugé inutilisé (elles s'appliquent sans être nommées), ni les imports de box, d'outils et de fichiers de données.
- **En tête d'un fichier `.forge`** : `forge` se propose seul sur la première ligne ; le choisir écrit `forge: ` et rouvre la liste des genres (`train`, `mold`, `gears`, `extend`, `index`, `native`, `precast`, `descriptor`) ; une fois le genre écrit, `description` se propose, et le choisir écrit `description: ''` avec le curseur entre les guillemets. Ces mots ne se proposent plus quand ils sont écrits, ni après le début du code, ni dans un `.rgl`.

Pas encore : coupure des lignes longues, espaces à l'intérieur d'une ligne, formatage à la frappe, hiérarchie des appels, débogage, explorateur de tests.

## Hiérarchie d'appels, liens, débogage, tests et pages Paper (0.15.0)

- **Hiérarchie d'appels** : sur un gear, un guard, un emit ou un train, « Afficher la hiérarchie d'appels » (Maj+Alt+H) liste qui l'appelle (appels entrants, regroupés par gear englobant, dans tout le projet, fichier d'entrée `.rgl` compris) et ce qu'il appelle (appels sortants, lus dans son corps).
- **Liens des imports** : le nom d'un fichier importé (`import Commandes;`, `use <Math>;`, `import box Base;`, `import json config;`, `import file "page.html" as page;`) s'ouvre d'un Ctrl+clic.
- **Débogage** : F5 (ou « Exécuter > Démarrer le débogage ») compile le projet en debug puis lance l'exécutable sous **gdb** (extension « C/C++ ») ou **LLDB** (extension « CodeLLDB ») ; les points d'arrêt se posent dans les fichiers `.rgl`, car le compilateur écrit des directives `#line` et compile avec `-g`. Configuration `{ "type": "rgl", "request": "launch" }` : `file`, `program` (sauter la compilation), `args`, `cwd`, `env`, `stopAtEntry`, `build`, `debugger` (`auto`, `gdb`, `lldb`), `debuggerPath`. Les valeurs s'affichent comme des valeurs C++ (pas encore de présentation propre aux types RGL), et la session n'a pas encore été essayée dans toutes les combinaisons de plateformes et de débogueurs.
- **Explorateur de tests** : un fichier `nom.test.rgl` est un test. « Exécuter » le lance (`rgl exec`) : le code 0 est un succès ; sinon le rapport « ÉCHEC (fichier:ligne:colonne) : raison » des vérifications de la std (`Test`), ou l'erreur de compilation, se pose sur sa ligne. « Déboguer » passe par la session ci-dessus. Un fichier de test n'est jamais le point d'entrée du dossier (`rgl compile -d .` l'ignore) et a son propre nom de projet (`projet-nom`), donc son exécutable ne remplace pas celui du programme.
- **Pages `.paper`** : le RGL des blocs `@rgl { … }` et des interpolations `@@ … @@` a ses diagnostics, sa complétion, son survol, son aide à la saisie et son accès à la définition, aux bonnes positions de la page ; le RGL est sur un **fond un peu plus clair** que le HTML (couleur `rgl.paperRegionBackground`, réglable dans `workbench.colorCustomizations`). Le serveur lit la page comme un fichier forge virtuel : les blocs tels quels, chaque interpolation comme `escapeHtml((expression).toString())`, les états `reactive` comme des constantes (écrire dans un état n'est donc pas une erreur). Pas encore : coloration sémantique, plan du document, import automatique, noms de gears dans `@click="…"` et attributs de page, fichiers importés par `import file` dans une interpolation.
- **Écriture** : un changement de texte ne relance plus l'analyse à chaque caractère : la complétion et le survol répondent tout de suite et les erreurs se recalculent 200 ms après la dernière frappe.

## Correctifs Paper et suggestion de mold (0.15.1)

- **Coloration des blocs `@rgl`** : elle ne s'appliquait pas dans VS Code (un fichier HTML y est tokenisé avec la racine `text.html.derivative`, que l'injection ne visait pas). Elle est visée maintenant.
- **Pages `.paper` importées** : `import Home;` donne ses noms (`Home.mount`, `Home.render`, `Home.State`, les gears de ses blocs `@rgl`) à la complétion, au survol et à l'aide à la saisie. Aller à la définition mène au début de la page.
- **Valeur d'un mold en grisé** : après `Vector2 vec:` (ou `mut Vector2 vec:`), `{x: , y: }` apparaît en grisé ; Tab l'écrit et place le curseur après le `:` du premier champ, Tab passe au suivant. Seuls les champs obligatoires (sans valeur par défaut, non nullables) sont écrits, dans l'ordre du mold ; rien n'est proposé s'il n'y en a aucun, ou si une valeur est déjà écrite. Il faut que les suggestions en ligne soient activées (`editor.inlineSuggest.enabled`, activé par défaut).

## Correctifs (0.15.2)

- **Complétion d'un nom qualifié dans un train** : `|> Home.m` ne proposait rien (le parseur lit `Home.m` d'un bloc) ; les noms du fichier importé — page `.paper` ou `.forge` — se proposent maintenant, aussi pour un type `Fichier.Type`.
- **Coloration des pages `.paper`** : le HTML qui suit un `@@ … @@` perdait ses couleurs (l'interpolation ne se refermait jamais) ; elle se ferme.

## Nouveau fichier RGL et serveur plus rapide (0.16.0)

- **Nouveau fichier RGL…** : clic droit sur un dossier de l'explorateur, juste après « Nouveau fichier ». On choisit `.rgl` (le point d'entrée du projet, `train |> done;`) ou `.forge`, on écrit le nom (sans extension : c'est celui qu'un import écrit ; il est refusé s'il existe, et un deuxième `.rgl` avertit), puis pour un `.forge` le **genre** (`gears`, `mold`, `train`, `extend`, `precast`, `descriptor`, `native`, `index`) et sa **description**. Le fichier est créé avec `forge: genre` et `description: '…'` (un `forge: train` reçoit son train par défaut) et ouvert.
- **Serveur de langage nettement plus rapide** : une analyse complète du projet passe de ~3 s à ~65 ms, et complétion, survol et le reste de ~35 ms à ~1-10 ms. Les erreurs se recalculent 150 ms après la dernière frappe.

## Nouveau dossier Box et Tools (0.17.0)

Clic droit sur un dossier de l'explorateur, sous « Nouveau fichier RGL… » :

- **Nouveau dossier Box…** : on écrit le nom (sans `.box`) et une description ; l'extension crée `Nom.box/` avec son `index.forge` déjà construit (`forge: index`, la description, et en commentaires l'aide pour écrire les `import` et l'`export {...Fichier};`) et l'ouvre. La box s'importe par `import box Nom;`.
- **Nouveau dossier Tools…** : on écrit le nom (sans `.tools`) ; l'extension crée `Nom.tools/`, que l'on importe par `import tools Nom;` (tous ses fichiers d'un coup).

Le nom est un identificateur (lettres, chiffres, `_`, `-`) et refusé s'il existe déjà.

## Correctif du débogage (0.17.1)

Les points d'arrêt ne se posaient pas : sous Windows, **Zig écrit les informations de débogage dans un `.pdb`** (CodeView), que gdb ne lit pas, et l'extension choisissait gdb en premier. Elle prend maintenant, sous Windows, **LLDB** (extension CodeLLDB) puis, à défaut, le **débogueur de Visual Studio** (`cppvsdbg`, extension C/C++) ; gdb reste le choix ailleurs (ELF, DWARF). Réglage `debugger` de la configuration : `auto`, `lldb`, `vsdbg`, `gdb` (refusé sous Windows, avec l'explication). Vérifié en ligne de commande avec le LLDB de CodeLLDB : un point d'arrêt sur une ligne de train (`|> fois2(21)`) et un autre dans un gear s'arrêtent bien, avec les lignes du `.rgl`. Le débogueur de Visual Studio (`vsdbg`) n'a pas été essayé.

## Débogage : noms RGL et F11 qui reste dans votre code (0.18.0)

- **F11 n'entre plus dans le runtime** : une ligne comme `string copie: texte;` ne vous envoie plus dans `string.hpp` (constructeurs de `string`, `symbol`, tableaux, bibliothèque C++) ; F11 passe à la ligne suivante de votre `.rgl`, et entre toujours dans vos propres gears. Réglage `stepIntoRuntime: true` pour y entrer quand même. (lldb : `settings set target.process.thread.step-avoid-regexp`, posé avant le lancement ; gdb : `skip -rfu` ; le débogueur de Visual Studio n'a pas d'équivalent.)
- **Noms RGL dans un cadre RGL, noms C++ dans un cadre C++** : dans un cadre écrit en `.rgl`, `.forge` ou `.paper`, le panneau Variables montre `texte: string`, `n: uint`, `total: uint` au lieu de `rgl_p_texte: const rgl::standard::primitives::string &`, `rgl_v_n: unsigned int`… ; les temporaires du C++ engendré (`rgl_t0`, `rgl_start`…) sont cachés, une variable masquée s'écrit `x (2)`, les champs d'un mold ont leur nom RGL, et un Watch comme `n + 1` s'évalue. Dans un cadre C++ (le runtime), les noms C++ restent. Réglage `rglNames: false` pour tout garder en C++.
- Vérifié de bout en bout avec le vrai adaptateur CodeLLDB (sur un programme qui crée des `string` et des `symbol`) ; l'affichage dans VS Code lui-même suppose que les messages réécrits par le traqueur sont ceux qu'il reçoit. Non essayé : `vsdbg`, `gdb` hors Windows.

## Débogage des pages `.paper` (0.18.1)

Les points d'arrêt posés dans un `.paper` (dans un bloc `@rgl { … }` ou une interpolation `@@ … @@`) fonctionnent. Deux causes : VS Code n'autorise les points d'arrêt que dans les langages déclarés (`.paper` est du HTML : `html` est maintenant déclaré), et surtout le C++ engendré portait les numéros de ligne du module que le compilateur Paper fabrique, pas ceux de la page (`#line 14` pour une ligne 6). Les `#line`, et les positions des erreurs à l'exécution, portent maintenant la ligne de la page ; le code que la page n'écrit pas (`render`, `dispatch`, `mount`) est rattaché à la ligne 1. Pas encore : les messages d'erreur de compilation d'un bloc, qui citent encore les lignes du module engendré.

## Débogage : la valeur d'une variable au survol (0.19.0)

Pendant une session de débogage, survoler une variable donne sa valeur, dans un `.rgl`, un `.forge` et dans le RGL d'une page `.paper` (blocs `@rgl` et interpolations `@@ … @@`) :

- `texte` → `"bonjour"` (une `string`, même longue), `s` → `§ok` (un `symbol`), `liste` → `[3 éléments]` qui se déplie en `[0] 1`, `[1] 2`, `[2] 3`, `n` → `7`, `n + 1` → `8` ; un champ de mold se survole comme `pos.x`. Le type est écrit comme RGL l'écrit (`string`, `[int]`, `uint`). Avant : une `string` s'affichait `{ctrl_:0x0, ptr_:0x0, …}`.
- Ça marche même si le panneau Variables est fermé : les variables du cadre du haut de la pile sont demandées dès l'arrêt, pour connaître leurs noms.
- Les mêmes valeurs lisibles s'affichent dans le panneau Variables et l'évaluation d'une expression (Watch). Réglage `rglValues: false` pour revenir aux valeurs brutes du C++. Ces formateurs sont ceux de lldb (CodeLLDB), qui est le débogueur choisi sous Windows ; le débogueur de Visual Studio et gdb n'en ont pas.

## Débogage : le survol dans les pages `.paper`, et seulement des valeurs RGL (0.19.1)

- **Pages `.paper`** : le survol de `count` ne donnait rien dans une page, parce que l'état `reactive int count` est, dans le C++ engendré, le champ `state.count` du paramètre `state` des gears de la page, pas une variable. Les états réactifs de la page (lus dans ses blocs `@rgl`) sont maintenant retrouvés : `count` → `0`, `count + 1` → `1`, `state` → `{count:0}`. Les **constantes de fichier** (`title`, déclarées au premier niveau) se survolent aussi : les portées Static et Global du débogueur sont lues, et seules les constantes de votre programme y sont montrées.
- **Valeurs RGL, pas structures C++** : une primitive (`string`, `symbol`, durée, date, nombre, `bool`, et leurs `?`) n'a plus rien à déplier au survol : `"bonjour"`, `§ok`, `1500ms`, `1970-01-02T00:00:00Z`, `7`. Les champs d'un mold s'affichent sans préfixe (`{nom:"p", x:1}` au lieu de `{rgl_f_nom:"p", rgl_f_x:1}`), un optionnel se lit `null` ou sa valeur et son type `string?`. Le « [raw] » (vue C++ brute) que le débogueur ajoute est masqué.

## Le `.rgl` du projet depuis un `.forge` ou un `.paper` (0.19.2)

Dans un `.forge`, un `.paper` ou tout fichier d'un projet (pas seulement dans le `.rgl`), **Exécuter, Compiler, Vérifier** (palette et boutons de la barre de titre) et **Déboguer** (F5) travaillent sur le **`.rgl` d'entrée du projet** : le seul `.rgl` de la racine du projet (celle du `rgl.json` le plus proche), sans les fichiers `nom.test.rgl`. Avant, ces commandes lançaient `rgl compile` sans fichier. Si le projet a plusieurs `.rgl` ou aucun `rgl.json`, un message le dit au lieu d'essayer quelque chose. Les boutons ▶ 🔨 et le débogage sont aussi dans la barre de titre d'une page `.paper`, et **F5 dans une page propose le débogueur RGL** (il proposait Node ou Edge) ; contrepartie : RGL figure aussi dans la liste des débogueurs proposés pour un fichier HTML ordinaire.
