# Paper

Paper compile des composants `.paper` en modules RGL natifs et prépare leur exécution interactive dans le navigateur.

## Rendu sûr

```paper
@rgl {
    const string title: '<RGL>';
}

<h1>@@ title @@</h1>
<section>@@ html('<strong>brut</strong>') @@</section>
```

`@@ valeur @@` convertit la valeur en texte puis échappe toujours HTML. `html` est un ingot de `string` ; une expression explicitement enveloppée dans `html(...)` est insérée sans échappement. Tous les blocs `@rgl` forment une seule portée privée au fichier, indépendamment de leur position.

Un fichier peut être incorporé uniquement dans une interpolation Paper :

```paper
@@ import file "fragment.html" @@
@@ html(import file "fragment.html") @@
```

La première forme échappe le fichier, la seconde l'insère brut. `import [..] file` sort d'une box. Cette expression n'appartient pas à la grammaire RGL générale.

## Fragments indépendants

Chaque interpolation devient un gear `__paper_fragment_N`. Le rendu initial place deux commentaires stables autour de sa valeur :

```html
<!--rgl:f0-->valeur<!--rgl:/f0-->
```

Le module exporte `renderFragment(id)` ; `rgl-paper.js` remplace uniquement la plage entre ces commentaires. Il n'est donc pas nécessaire de reconstruire la page complète.

## État réactif

```paper
@rgl {
    reactive int count: 0;

    gear increase(DomEvent event): void {
        count +<- 1;
    }
}

<button @click="increase">@@ count @@</button>
```

Les déclarations `reactive TYPE nom: valeur;` deviennent les champs d'un mold `State`, créé par `createState()`. `render()` crée cet état pour le premier rendu ; `renderWithState(state)` permet au serveur de rendre une session existante. Les gears qui appartiennent à la page reçoivent cet état. Le compilateur calcule les variables lues par chaque fragment et celles écrites par chaque handler. `dispatch` renvoie seulement les identifiants des fragments invalidés.

Un handler prend zéro paramètre ou un `DomEvent`. Les attributs `@click`, `@input`, `@change`, `@submit`, `@keydown` et `@keyup` sont vérifiés à la compilation puis transmis par WebSocket par le runtime navigateur.

Chaque module Paper exporte `mount(server, path)`. Cette transformation reçoit le serveur depuis un pipeline, ajoute la page, le runtime JavaScript et sa route WebSocket privée, puis transmet le serveur configuré. Le code applicatif n'a pas à manipuler `mut` ni `State`. À chaque connexion, le serveur fabrique un circuit avec son propre état. Sa closure le conserve entre les messages et sa destruction le libère à la fermeture du WebSocket. Deux navigateurs, ou deux onglets, évoluent donc indépendamment. Chaque réponse contient uniquement les fragments invalidés.

```rgl
use <Web>;
import Home;

train
|> Web.createServer
|> Home.mount('/')
|> Web.staticFiles
|> Web.listen
|> done;
```

`Web.staticFiles` sert en dernier recours le dossier `static` copié à côté de l'exécutable. Les routes montées auparavant gardent la priorité. `Web.staticFilesFrom(autreDossier)` choisit une autre racine. `Web.listenOn(port)` et `Web.listenWith(address, port, maxRequests, workers)` configurent l'écoute.

## Build navigateur

Un projet qui contient un `.paper` produit à côté de son exécutable :

- `rgl-paper.js`, client WebSocket et moteur de patchs ;
- `rgl-paper.wasm`, noyau WebAssembly versionné.

La chaîne WebAssembly est une capacité de la chaîne de compilation. Zig implémente cette capacité avec la cible `wasm32-freestanding`; Paper utilise donc la même version de Zig gérée par RGL et son même cache, sans demander à l'utilisateur d'installer Emscripten ou un SDK WebAssembly séparé.

Les CSS, images et scripts applicatifs restent déclarés dans `assets` de `rgl.json`.

## Travail restant

- reprise optionnelle d'un circuit après une déconnexion grâce à un identifiant de session ;
- sucres `$#id`, `$.class` et `$[`selector`]` au-dessus de l'API `dom` ;
- types d'événements spécialisés ;
- composants imbriqués, paramètres et contenu enfant ;
- diagnostics et complétion LSP directement dans les positions du `.paper` ;
- exécution optionnelle de gears RGL côté navigateur au-delà du noyau ABI initial.
