# Serveur HTTP

Le module `Http` fournit un serveur HTTP/1.1 concurrent, un client HTTP/HTTPS,
TLS et des routes WebSocket pour écrire des services et des API en RGL.

```rgl
use <Http> only {HttpRequest, HttpResponse, HttpServer};

gear home(HttpRequest request): HttpResponse =>
    HttpResponse.html(`<h1>Bonjour {request.routeParameters['name']}</h1>`);

gear echo(HttpRequest request): HttpResponse =>
    HttpResponse.json(request.body, 201);

guard start(): void {
    mut HttpServer server: HttpServer.create();
    server.get('/hello/:name', home);
    server.post('/echo', echo);
    server.listen('127.0.0.1', 8080);
}

train |> start !> [fail] |> done;
```

## Requête

Un handler reçoit un `HttpRequest` :

| Champ | Contenu |
|---|---|
| `method` | Méthode telle que reçue, par exemple `GET` |
| `target` | Cible brute, query comprise |
| `path` | Partie de la cible avant `?` |
| `query` | Partie après `?`, sans le point d’interrogation |
| `headers` | Lignes d’en-têtes brutes, séparées par CRLF |
| `body` | Corps lu selon `Content-Length` |
| `queryParameters` | Couples séparés par `&` et `=` |
| `routeParameters` | Composants capturés par une route `:nom` |

La première version ne réalise pas encore le décodage percent-encoding. Ainsi,
`?name=Jean%20Dupont` conserve la valeur `Jean%20Dupont`.

## Réponse

`HttpResponse` contient :

- `status`, 200 par défaut ;
- `contentType`, `text/plain; charset=utf-8` par défaut ;
- `body`, vide par défaut.

Les constructeurs suivants choisissent le type de contenu :

```rgl
HttpResponse.text('bonjour');
HttpResponse.html('<h1>bonjour</h1>');
HttpResponse.json('{"ok":true}');
```

Ils acceptent un statut en deuxième argument. La réponse calcule
`Content-Length` à partir des octets UTF-8 et ferme la connexion après l’envoi.

## Routage

Les routes sont essayées dans leur ordre d’ajout. `get` et `post` sont des
raccourcis de `route(method, path, handler)`.

`staticFiles()` ajoute une route GET de dernier recours pour le dossier `static` à côté de l'exécutable ; `staticFiles(root)` choisit un autre dossier. Le type MIME est déduit de l'extension, un fichier absent donne 404 et un chemin qui tente de sortir de la racine avec `..` est refusé.

Pour une application, le module `Web` expose ces opérations sous forme d'étapes de pipeline. Une étape reçoit le serveur configuré par la précédente et le transmet à la suivante :

```rgl
use <Web>;
import Home;
import Admin;

train
|> Web.createServer
|> Home.mount('/')
|> Admin.mount('/admin')
|> Web.staticFiles
|> Web.listen
|> done;
```

`Web.staticFilesFrom(root)` choisit un autre dossier statique. `Web.listenOn(port)` écoute localement sur un autre port et `Web.listenWith(address, port, maxRequests, workers)` donne accès à tous les paramètres. `listen`, `listenOn` et `listenWith` terminent le pipeline ; les étapes précédentes renvoient toutes le serveur afin de rester composables.

```rgl
server.get('/api/status', status);
server.staticFiles();
```

Un composant commençant par `:` capture exactement un composant de chemin :

```rgl
server.get('/users/:id', (request) =>
    HttpResponse.json(`{"id":"{request.routeParameters['id']}"}`));
```

Une route doit avoir le même nombre de composants que le chemin demandé. Une
requête sans correspondance reçoit automatiquement `404 Not Found`.

## Écoute et erreurs

```rgl
server.listen(address, port, maxRequests, workers);
```

- `address` vaut `127.0.0.1` par défaut ; `0.0.0.0` écoute toutes les interfaces IPv4, `::` toutes les interfaces IPv4 et IPv6, et une adresse IPv6 littérale (`::1`) est acceptée ;
- `port` vaut 8080 par défaut ;
- `maxRequests` vaut zéro, donc le serveur continue jusqu’à l’arrêt du processus ;
- une valeur positive est utile aux tests et arrête proprement le serveur après ce nombre de requêtes.
- `workers` vaut zéro et choisit alors au moins deux workers selon la machine ; une
  valeur positive fixe le nombre maximal de connexions traitées en parallèle.

`listen` est un guard. Une erreur de socket devient `HttpError`, avec le code
système et l’opération `listen`.

## Client HTTP et HTTPS

```rgl
use <HttpClient> only {HttpClient, HttpClientResponse};

guard charger(): string {
    HttpClient client: HttpClient.create();
    HttpClientResponse response: client.get('https://example.com/api');
    return response.body;
}
```

`HttpClient` possède les champs `headers` (lignes brutes supplémentaires) et
`timeout` (30 secondes par défaut). `send`, `get` et `post` rendent un
`HttpClientResponse` contenant `status`, `headers` et `body`. Le client accepte
les URL absolues `http://` et `https://`; HTTPS vérifie la chaîne du certificat
et le nom d’hôte avec les autorités du système.

**HTTP/2.** En HTTPS le client propose `h2` puis `http/1.1` (négociation ALPN) ; si le
serveur choisit `h2`, la requête passe en HTTP/2 sans rien changer à ton code : la
réponse est rendue sous la même forme (`status`, `headers`, `body`). Pris en charge :
HPACK complet (table dynamique, code de Huffman), contrôle de flux dans les deux sens
(un corps plus grand que la fenêtre du serveur attend ses `WINDOW_UPDATE`), `PING`,
`CONTINUATION`. Pas pris en charge : plusieurs requêtes en même temps sur une
connexion (une connexion par requête, comme en HTTP/1.1), priorités, poussée du
serveur (refusée dans les réglages), trailers. **Le serveur reste en HTTP/1.1** :
un worker bloquant par connexion ne se prête pas au multiplexage, et le servir en
HTTP/2 demanderait de refaire son modèle d’exécution. Un serveur ou un client
HTTP/2 en clair (`h2c`) n’existe pas. Une réponse en `Transfer-Encoding: chunked`
est recomposée par le client (le corps rendu n’est jamais coupé en morceaux).

## TLS

```rgl
server.listenTls('certificat.pem', 'cle-privee.pem', '0.0.0.0', 8443);
```

Le certificat peut contenir toute la chaîne PEM. La clé privée doit être en PEM.
TLS utilise OpenSSL : sous Linux, RGL détecte les emplacements système habituels.
Sous Windows x86_64, `dotnet run fetch-tools.cs` (dossier `tools/`) installe aussi OpenSSL dans
`tools/openssl` (paquet MSYS2 figé et vérifié par SHA-256 dans `tools/toolchain.json`) : RGL le
trouve seul et copie ses DLL à côté de l'exécutable. Sur les autres plateformes,
`RGL_OPENSSL_INCLUDE` désigne le dossier d’en-têtes et `RGL_OPENSSL_LIB` le dossier des
bibliothèques ; ces variables l'emportent sur `tools/openssl`. Les DLL ou bibliothèques partagées
OpenSSL doivent être disponibles à l’exécution.

## WebSocket

```rgl
use <Http> only {WebSocketMessage};

gear echo(WebSocketMessage message): string =>
    `{message.request.routeParameters['room']}:{message.text}`;

server.webSocket('/socket/:room', echo);
```

Pour conserver un état propre à chaque connexion, `webSocketCircuit` reçoit une fabrique appelée après le handshake. La lambda qu'elle renvoie traite ensuite toutes les frames de cette connexion et est détruite à sa fermeture :

```rgl
server.webSocketCircuit('/counter', (request) => {
    mut [int] state: [0];
    return (message) => (state ||> (mut count) => { count +<- 1; return `{count}`; })[0];
});
```

Le serveur réalise le handshake RFC 6455 sur HTTP ou HTTPS (`ws://` et `wss://`).
Chaque frame texte appelle le handler du circuit et sa chaîne de retour devient une frame
texte. Les frames ping reçoivent un pong et la fermeture est acquittée. Les
clients doivent masquer leurs frames, comme l’exige le protocole.

## Modèle d’exécution et limites

Le transport garde volontairement les limites suivantes :

- pool de workers bloquants, sans boucle de sockets non bloquants ;
- connexions persistantes (keep-alive) : un worker reste attaché à une connexion jusqu'à 100 requêtes, ou 5 secondes sans requête, ou `Connection: close` ; avec moins de clients persistants que de workers, rien n'attend ;
- corps de requête limité à 16 Mio, `Content-Length` ou `Transfer-Encoding: chunked` ;
- réponses client limitées à 4 Mio, le client demandant toujours `Connection: close` ;
- compression gzip des réponses textuelles à partir de 1 Kio (`server.compress(0)` la désactive) ;
- WebSocket texte sans fragmentation ni frames binaires ;
- handlers synchrones.

Le transport est écrit dans le runtime C++. Le routeur, les requêtes, les réponses
et les handlers restent en RGL. Sous Windows, la chaîne lie Winsock uniquement
lorsqu’un programme utilise ce module ; OpenSSL ne s’ajoute que pour HTTPS.
