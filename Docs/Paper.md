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

## Tête de page : `page`

Dès qu'un bloc `@rgl` ou une interpolation écrit `page`, ou qu'un gear `load` existe, la page reçoit une tête de page. C'est un objet `PageHead` implicite, propre à chaque connexion, passé comme paramètre `mut page` à tous les gears de la page (handlers, `load`). Le nom `page` est réservé : un `reactive page` est refusé. Le mot « page » dans une chaîne n'est ni réécrit ni détecté.

```paper
@rgl {
    reactive int count: 0;

    gear load(): void {
        page.title <- 'Accueil';
        page.script('/app.js');
        page.env('API_URL', 'https://api.exemple.fr');
        page.env('debug', true);
        page.env('max', 5);
    }

    gear increase(): void {
        count +<- 1;
        page.title <- `Clic {count}`;
    }
}

<button @click="increase">@@ count @@</button>
```

- `page.title <- 'x'` règle le titre de l'onglet (`document.title`). Aucune balise `<title>` n'est nécessaire ; une balise `<title>@@ page.title @@</title>` reste possible, mais seule la commande met l'onglet à jour.
- `page.script(url)` charge un script. Le navigateur ne charge jamais deux fois la même adresse.
- `page.env(nom, valeur)` définit une variable lisible dans `window.RGL_ENV` (texte, `bool` ou entier `long`). Les valeurs sont échappées pour une balise `script` : `<` devient `<`.
- `load()` est un gear sans paramètre, exécuté à la création de l'état (rendu HTML et ouverture du WebSocket). C'est l'endroit où régler la tête initiale.

Au rendu initial, `render()` écrit à la fin de la page, dans cet ordre : `window.RGL_ENV`, le titre, les balises `script`, puis le runtime. Ensuite, chaque réponse WebSocket d'un handler ajoute un membre `"head":{"title":…,"scripts":[…],"env":{…}}` seulement pour ce qui a changé depuis le dernier envoi ; `rgl-paper.js` l'applique. Un gear qui écrit `page.…` ou un champ d'un état invalide les fragments qui lisent cet état.

## Paramètres de page : `param`

```paper
@rgl {
    param string who;
    param int times: 2;
    reactive int count: 0;

    gear load(): void { count <- who.length as int; }
}

<p>@@ who @@ x@@ times @@</p>
```

`param TYPE nom;` ou `param TYPE nom: valeur;` déclare une donnée que l'appelant de la page fournit. Le module exporte un mold `Props` (un champ par `param`, la valeur par défaut est celle du `param`) et `State` reçoit les mêmes champs. Un `param` se lit comme un `reactive` (`who`, `times`) mais **est en lecture seule** : un gear qui l'écrit est refusé ; copie sa valeur dans un `reactive` pour la modifier. Il n'invalide jamais de fragment.

Trois façons de monter la page :

```rgl
|> Home.mount('/', {who: 'Ann'})                                      // valeur fixe
|> Home.mountWith('/', (HttpRequest request) =>                       // calculée à chaque requête
       new Home.Props {who: request.queryParameters.getOr('who', 'anonyme')})
|> Home.mount('/')                                                    // seulement si tous les param ont une valeur par défaut
```

`render(props)` (rendu sans serveur) prend aussi un `Props`. Une lambda ne peut pas rendre un mold littéral nu (`=> {…}` est lu comme un bloc) : écris `new Home.Props {…}`.

Le fournisseur de `mountWith` est appelé **deux fois par visite** : pour la requête HTTP de la page, puis pour la requête d'ouverture du WebSocket, afin que l'état du circuit parte des mêmes valeurs. Les paramètres ne passent donc jamais par le navigateur (rien à falsifier), mais le fournisseur doit être déterministe pour une même requête. Le client ajoute `location.search` à l'adresse du WebSocket : la chaîne de requête, les en-têtes et les cookies de la page sont ceux que voit le fournisseur. **Limite** : le chemin de la requête du WebSocket est celui du socket, et les `routeParameters` d'une route `/user/:id` ne sont pas transmis ; utilise la chaîne de requête.

## Layouts : `@layout` et `<slot />`

```paper
<!-- Main.paper -->
@rgl {
    reactive int visits: 10;
    gear load(): void { page.title <- 'Mon site'; }
    gear bump(): void { visits +<- 1; }
}
<html><body>
    <nav>@@ visits @@ visites</nav>
    <slot />
    <footer @click="bump">pied</footer>
</body></html>
```

```paper
<!-- Home.paper -->
@layout Main;
@rgl {
    reactive int count: 0;
    gear load(): void { page.title <- 'Accueil'; }
}
<h1>Accueil</h1><button>@@ count @@</button>
```

`@layout Nom;` (seul sur sa ligne) cherche `Nom.paper` par son nom dans le projet (une box ne voit que ses fichiers, comme pour `import file`). Le markup de la page prend la place du `<slot />` du layout (`<slot />`, `<slot/>` et `<slot></slot>` sont équivalents ; il en faut exactement un). Un layout peut avoir son propre `@layout`.

**La fusion se fait à la compilation** : le layout et la page deviennent une seule page, avec un seul état, une seule numérotation de fragments et les mêmes handlers. Conséquences :
- un `reactive`, un `param` ou un gear du layout se lit et s'appelle depuis la page, et inversement ; `page` est la même tête pour les deux ;
- un gear déclaré des deux côtés est refusé (« déclarent tous deux le gear … »), de même qu'un `reactive` ou un `param` en double ;
- un clic ne rafraîchit que les fragments qui lisent ce que son handler écrit, où qu'ils soient ;
- `load` fait exception : le `load` de chaque layout s'exécute avant celui de la page, du layout le plus extérieur au plus proche, donc la page a le dernier mot (`page.title` ci-dessus vaut « Accueil »).

Un fichier qui contient un `<slot />` sans avoir de layout au-dessus (le layout lui-même, compilé seul) rend une place vide. Limites : l'éditeur ne voit pas les noms du layout depuis la page ; le débogueur ne rattache ni les blocs du layout, ni les interpolations d'une page qui a un layout, à une ligne de la page ; les diagnostics d'un bloc du layout citent le texte engendré.

## Composants

```paper
<!-- Carte.paper -->
@rgl {
    param string titre;
    param int niveau: 1;
}
<section class="carte"><h2>@@ titre.toUpper() @@ (@@ niveau @@)</h2><slot /></section>
```

```paper
<!-- Home.paper -->
@rgl { reactive int count: 0; gear increase(): void { count +<- 1; } }

<Carte titre="salut" niveau=@@ count + 1 @@>
    <button @click="increase">@@ count @@</button>
</Carte>
<Carte titre="autre" />
```

Une balise dont le nom commence par une **majuscule** est un composant : `Carte.paper` est cherché par son nom dans le projet (règles de `@layout`). Le composant est un `.paper` ordinaire, et ses `param` sont ses propriétés typées.

- `titre="texte"` donne un texte (guillemets ou apostrophes) ; `niveau=@@ expression @@` donne n'importe quelle expression de la page. L'expression est lue dans la page, donc elle peut lire son état : `count + 1` ci-dessus se met à jour quand `count` change.
- Un `param` sans valeur par défaut est obligatoire ; un attribut qui n'est pas un `param`, écrit deux fois, ou sans valeur, est refusé. Le type d'une valeur est contrôlé par le compilateur contre celui du `param`.
- Le contenu de la balise prend la place du `<slot />` du composant. Sans `<slot />`, un contenu est refusé.
- Les composants s'emboîtent et un composant peut en utiliser d'autres ; un composant qui se contient lui-même est refusé.

**Un composant n'a pas d'état** : pas de `reactive`, pas de `load`, pas de `@layout`. Il est étendu à la compilation : ses `param` sont remplacés, dans ses interpolations `@@ … @@`, par les valeurs des attributs, et ses blocs `@rgl` (gears, constantes) rejoignent ceux de la page, une seule fois. Comme pour un layout, un gear déclaré par le composant et par la page est refusé. Conséquences : un `param` ne se lit que dans les interpolations du composant, pas dans ses gears ; les gears d'un composant ne se rappellent que depuis ses interpolations quand la page n'a pas d'état (un gear d'une page avec état reçoit l'état en paramètre, ce que l'interpolation ne lui passe pas).

Le `.paper` d'un composant reste aussi une page : compilé seul, il rend sa place de `<slot />` vide et ses `param` forment son `Props`. Une balise à majuscule qui ne doit pas être un composant (du JavaScript dans un `<script>`, par exemple) n'est pas protégée : écris-la dans un fichier à part ou en minuscule. Les commentaires HTML et les interpolations ne sont jamais lus comme des composants.

## Travail restant

- reprise optionnelle d'un circuit après une déconnexion grâce à un identifiant de session ;
- sucres `$#id`, `$.class` et `$[`selector`]` au-dessus de l'API `dom` ;
- types d'événements spécialisés ;
- paramètres de route (`/user/:id`) transmis au circuit, composants avec état, slots nommés ;
- noms du layout visibles dans l'éditeur depuis la page ;
- diagnostics et complétion LSP directement dans les positions du `.paper` ;
- exécution optionnelle de gears RGL côté navigateur au-delà du noyau ABI initial.
