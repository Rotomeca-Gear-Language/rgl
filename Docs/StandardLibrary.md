# Bibliothèque standard

La bibliothèque standard est embarquée dans le compilateur. Un module public
s'importe avec `use <Nom>;` ou avec une sélection :

```rgl
use <Path> only {join, normalize};
use <File> only {readText, writeText};
```

`use <std>;` rassemble les membres publics sous l'espace `std`. Les fichiers
`forge: extend` ajoutent automatiquement leurs méthodes aux types concernés.

## Collections

Les transformations génériques sont des gears du module `Collections` :

| Signature | Résultat | Coût |
|---|---|---|
| `map<T, U>([T], pure (T) => U): [U]` | Un résultat par élément, dans le même ordre | O(n) |
| `flatMap<T, U>([T], pure (T) => [U]): [U]` | Transformation puis concaténation | O(n + taille produite) |
| `zip<T, U>([T], [U]): [(T, U)]` | Paires jusqu'à la fin du tableau le plus court | O(min(n, m)) |

```rgl
[int] doubled: std.map([1, 2, 3], (int n) => n * 2);
[(int, string)] pairs: std.zip([1, 2], ['a', 'b', 'c']);
```

Les extensions suivantes s'appellent sur un tableau :

| Signature | Comportement |
|---|---|
| `partition(test)` | Rend `(matches, others)` en conservant l'ordre |
| `takeWhile(test)` | Garde le préfixe qui satisfait `test` |
| `dropWhile(test)` | Retire ce même préfixe |
| `chunk(size)` | Découpe en blocs d'au plus `size`; `size == 0` rend `[]` |
| `enumerate()` | Rend des couples nommés `(position, value)` |
| `sortedWith(compare)` | Rend une copie triée, stable, en O(n log n) |
| `sortWith(compare)` | Trie le tableau mutable en place |

Un comparateur rend une valeur négative si son premier argument doit précéder
le second, zéro s'ils sont équivalents pour le tri, et une valeur positive
sinon :

```rgl
pure (int, int) => int descending:
    (a, b) => a > b ? -1 : (a < b ? 1 : 0);

[int] copy: [3, 1, 2].sortedWith(descending); // [3, 2, 1]
mut [int] values: [3, 1, 2];
values.sortWith((a, b) => a < b ? -1 : (a > b ? 1 : 0));
```

`sortedWith` utilise un tri fusion stable. Il alloue une nouvelle collection.
`sortWith` calcule cette collection puis recopie ses éléments dans le tableau
mutable reçu.

## Chaînes

Les positions sont exprimées en unités de `string`, comme `length` et
`substring`.

| Extension | Comportement |
|---|---|
| `lastIndexOf(what)` | Dernière occurrence, ou `null`; la chaîne vide est trouvée à `length` |
| `isBlank()` | `true` si le texte est vide ou ne contient que espace, tabulation, LF ou CR |
| `removePrefix(prefix)` | Retire le préfixe s'il est présent |
| `removeSuffix(suffix)` | Retire le suffixe s'il est présent |
| `capitalize()` | Met le premier caractère ASCII en majuscule |
| `splitOnce(separator)` | Rend `(before, after, found)` et ne coupe qu'à la première occurrence |
| `replaceAll(what, by)` | Remplace les occurrences non chevauchantes; un motif vide ne change rien |

Les opérations de casse restent volontairement ASCII. Elles ne constituent pas
une normalisation Unicode.

## Mathématiques

`pow` utilise les surcharges, le type des arguments choisit donc l'opération :

| Signature | Comportement |
|---|---|
| `pow(int, uint): int` | Exponentiation entière rapide |
| `pow(double, int): double` | Puissance réelle avec exposant entier signé |
| `pow(double, double): double` | Puissance réelle selon `std::pow` |

`powInt(int, uint)` reste un alias de compatibilité. Le nouveau code devrait
appeler `pow`.

Les fonctions `sqrt`, `cbrt`, `hypot` et les surcharges flottantes de `pow`
passent par la bibliothèque mathématique C++ sous une frontière `@trusted pure`.
`hypot` évite les débordements intermédiaires de `sqrt(a*a + b*b)`.

| Fonction | Contrat |
|---|---|
| `cbrt(x)` | Racine cubique, définie aussi pour les nombres négatifs |
| `approxEqual(a, b, absTol, relTol)` | Tolérance absolue ou relative; valeurs par défaut `1e-12` et `1e-9` |
| `inverseLerp(start, end, value)` | Position non bornée; rend `0` si les bornes sont égales |
| `remap(value, fromStart, fromEnd, toStart, toEnd)` | Transposition linéaire entre deux intervalles |
| `smoothstep(edge0, edge1, x)` | Interpolation cubique bornée entre 0 et 1 |

## Aléatoire

`Random` reste un générateur Park–Miller déterministe. Il convient aux tests,
jeux et simulations simples, pas aux secrets.

`nextInt(min, max)` accepte les bornes dans n'importe quel ordre et les inclut
toutes les deux. Il combine deux sorties du générateur et utilise un rejet pour
éviter le biais modulo, y compris sur toute la plage d'un `int`.

```rgl
use <Random> only {Random, choice, shuffled};

mut Random random: Random.seed(42);
string? item: choice(random, ['rouge', 'vert', 'bleu']);
[int] order: shuffled(random, [1, 2, 3, 4]);
```

`choice` rend `null` pour un tableau vide. `shuffled` rend une copie mélangée
par Fisher–Yates et ne modifie pas le tableau d'origine. Les deux font avancer
le générateur mutable.

## Chemins

Le module `Path` utilise `std::filesystem` et reçoit des chemins UTF-8 :

| Fonction | Contrat |
|---|---|
| `join(left, right)` | Joint deux composants avec les règles de la plateforme |
| `normalize(path)` | Réduit lexicalement `.` et `..`, sans consulter le disque |
| `isAbsolute(path)` | Indique si le chemin est absolu pour la plateforme cible |
| `filename(path)` | Dernier composant, extension comprise |
| `basename(path)` | Dernier composant sans sa dernière extension |
| `extension(path)` | Dernière extension, point compris, ou texte vide |
| `dirname(path)` | Chemin parent, ou texte vide |
| `currentDirectory()` | Dossier courant du processus |
| `executablePath()` | Chemin absolu du binaire en cours |
| `executableDirectory()` | Dossier qui contient le binaire |
| `findFile(name, folder)` | Premier fichier de ce nom sous `folder`, ou `null` |

Les fonctions retournent des séparateurs `/` pour produire une représentation
portable. `normalize` ne vérifie ni l'existence du chemin ni les liens
symboliques.

Les chemins relatifs partent toujours de `currentDirectory()`. Pour des ressources
déployées avec l'application, partez de `executableDirectory()` :

```rgl
string assets: std.join(std.executableDirectory(), 'front/html');
string? page: std.findFile('index.html', assets);
```

## Fichiers

Le module `File` est une façade sur les primitives d'IO du langage :

| Fonction | Effet |
|---|---|
| `readText(path)` | Lit le fichier entier comme UTF-8 strict |
| `readBytes(path)` | Lit tous les octets |
| `writeText(path, content)` | Crée ou remplace un fichier UTF-8 |
| `writeBytes(path, content)` | Crée ou remplace un fichier binaire |
| `appendText(path, content)` | Crée le fichier ou ajoute le texte à sa fin |
| `exists(path)` | Teste l'existence d'un fichier ou dossier sans lever d'erreur |

Les cinq opérations d'IO sont des guards. Elles propagent un `IoError` avec le
chemin, le lieu de l'appel et la cause. Elles doivent donc être appelées depuis
un guard ou traitées par un rail dans un train :

```rgl
use <File> only {readText, writeText};

guard copyText(string source, string destination): void {
    string content: readText(source);
    writeText(destination, content);
}

train 'source.txt' |> copyText('copie.txt') !> [fail] |> done;
```

`readText` retire un BOM UTF-8 et refuse les octets qui ne forment pas un texte
UTF-8 valide. Pour conserver exactement les octets, utiliser `readBytes`.

## HTTP

Le module `Http` fournit un serveur concurrent, un client HTTP/HTTPS, TLS et des
routes WebSocket. Les routes GET et POST acceptent des composants `:paramètre` ;
les paramètres de route et de query sont exposés comme dictionnaires. TLS repose
sur OpenSSL et vérifie les certificats côté client.

La référence complète et les limites du transport sont décrites dans
[Serveur HTTP](Http.md).

## Compléments (complétion de la std)

Chaque nom ci-dessous est public ; les gears écrits en C++ passent par un bloc `cpp { … }` (voir `Docs/InlineCpp.md`).

### Bornes des nombres

`int.min`, `int.max`, `uint.max`, `long.min`, `ulong.max`, `byte.min`… sont des constantes du compilateur ; pour `float` et `double`, `min` est la plus petite valeur finie (la plus négative), comme `double.MinValue` en C#.

### JSON depuis ou vers un texte

`parseJson<T>(texte)` et `toJson(valeur)` sont des intrinsèques (comme `sleep`) : mêmes règles que le format `json` d'un fichier, mais sans fichier, donc sans guard. Le résultat est `T ! ParseError` (`string ! ParseError` pour `toJson`) ; la `ParseError` porte le chemin de la valeur fautive dans `input`.

### Dictionnaires et tableaux

- Dictionnaire, méthodes intégrées : `keys()`, `values()`, `put(k, v)` et `without(k)` (les deux derniers rendent **un nouveau** dictionnaire : écrire `d <- d.put(k, v);`, une instruction qui jette le résultat est refusée).
- Extensions de dictionnaire : `remove` (en place, sur une variable `mut`), `entries`, `merge`, `filter` ; gear `mapValues`.
- Extensions de tableau : `pop`, `removeAt`, `insert`, `clear` (en place, sur une variable `mut`), `windowed`, `unzip` ; gears `zipWith`, `sortBy`, `minBy`, `maxBy`, `groupBy`.
- Structures (`Structures` et ses extensions) : `Stack<T>`, `Queue<T>`, `Set<K>`, `PriorityQueue<T>`, créées par `newStack<int>()`, `newQueue<T>()`, `newSet<K>()` et `newPriorityQueue<T>(comparaison)`. Opérations : `push`/`pop`/`peek`/`size`/`isEmpty` (pile), `enqueue`/`dequeue` (file), `add`/`remove`/`has`/`values`/`union`/`intersection`/`difference` (ensemble).

### Texte et encodages (`Text`, extensions de chaînes)

`base64Encode`/`base64Decode`, `hexEncode`/`hexDecode`, `urlEncode`/`urlDecode`, `codePoints`, `fromCodePoint`, `compareText`, `levenshtein`, `parseCsv`/`toCsv`. Extensions de `string` : `isUpper`, `isLower`, `isAlphaNumeric`, `isPunctuation`, `format('{0} {1}', a, b)`. Extensions de nombre : `toFixed(décimales)`, `toRadix(base)`, `toHex()`, `toBinary()`.

### Fichiers de configuration (`Config`)

`parseIni`, `parseToml` et `parseYaml` rendent un `[string:string] ! ParseError` à clés pointées (`serveur.port`, `liste[0]`) ; `toIni` écrit un INI. TOML : tables, tableaux de tables, tableaux, tables en ligne, chaînes, nombres, booléens (pas les dates). YAML : sous-ensemble (dictionnaires et listes par indentation, scalaires, valeurs de flux `[a, b]` et `{a: b}` ; ni ancres ni documents multiples).

### Système

- `Environment` : `arguments()`, `getEnv`, `setEnv`, `environment()`, `exit(code)`, `platform()`, `architecture()`, `cpuCount()`.
- `Console` : `writeError`/`writeErrorLine` (sortie d'erreur), `readAll()`, constantes ANSI et `colorize`.
- `Logs` : `setLogLevel(niveau)` filtre les messages, `setLogFile(chemin)` les recopie dans un fichier.
- `File` : `removePath`, `createDirectory`, `listDirectory`, `isDirectory`, `isFile`, `fileSize`, `modifiedTime`, `copyFile`, `moveFile`. `Path` : `relative`, `changeExtension`.

### Mathématiques, temps, erreurs

- `Math` : `isNaN`, `isInfinity`, `isFinite`, `sinh`, `cosh`, `tanh`, `asinh`, `exp2`, `modulo`, `floorDiv`, `ceilDiv`, `bitCount`, `isPrime`, et les grands entiers en décimal : `bigAdd`, `bigSub`, `bigMul`, `bigDivMod`, `bigCompare`, `bigPow`, `bigFactorial`. Pas de type décimal exact.
- `Time` : `milliseconds`… `weeks` (durées), `Stopwatch` (`startStopwatch`, `elapsed`), `localOffset` ; extensions de durée (`totalSeconds`, `toText`…) et de date (`addMonths`, `addYears`, `format('yyyy-MM-dd HH:mm')`, `toLocal`, `toUtc`).
- `Async` : `timeout(promesse, durée)` (la promesse d'origine continue) et `retry(essais, pause, action)`.
- `Results` : `isOk`, `valueOf`, `errorOf`, `mapOk`, `mapError`, `andThen`, `orElse` sur `T ! E` ; `mapNullable`, `flatMapNullable`, `filterNullable` sur `T?`.

### Tests (`Test`)

`assertTrue`, `assertFalse`, `assertEqual`, `assertNotEqual`, `assertNear`, `assertNull`, `assertNotNull`, `failTest` et `test(nom, () => { … })`. Un échec écrit un rapport avec la position de l'appel (`__WHERE__`) sur la sortie d'erreur et arrête le programme avec le code 1.

### Empreintes, compression, réseau

- `Crypto` : `sha256` (octets ou texte), `hmacSha256`, `sha1`, `md5`, `crc32`, `randomBytes`, `uuid4`, `constantTimeEquals`. SHA-1 et MD5 ne sont plus sûrs : compatibilité seulement. Pas de chiffrement.
- `Compression` : `deflateRaw`/`inflateRaw`, `gzipCompress`/`gzipDecompress`. La compression utilise des codes de Huffman fixes (taux modeste) ; la décompression lit tous les types de blocs.
- `Net` : sockets TCP et UDP bloquants, avec délai, en IPv4 et IPv6 (`tcpConnect`, `tcpListen`, `tcpAccept`, `udpOpen`, `socketSend`, `socketReceive`, `udpSend`, `socketPort`, `socketClose`), et leurs versions asynchrones `tcpConnectAsync`, `tcpAcceptAsync`, `socketSendAsync`, `socketReceiveAsync` (des `@task` : l'attente se fait sur le pool de threads, une promesse de `T ! AsyncException`, erreur de genre `IoError` ; un `cancel` ne les interrompt pas, donne-leur un délai). Composant runtime `Net` (lié seulement s'il est utilisé).

### Géométrie et couleurs

`Geometry` ajoute `Rect`, `Circle`, `Segment`, `Polygon` (2D) et `Matrix3`/`Matrix4` (produit par `*`, `identity`, `translation`, `scaling`, rotations, `inverse`, `determinant`, `transformPoint`). `Color` ajoute les constantes (`Color.RED`…), `Color.rgb`, `Color.fromHex`, `Color.fromHsl`, `toHex`, `toHsl`, `mix`, `lighten`, `darken`, `inverted`, `luminance`, `grayscale`.

### HTTP

La requête porte `headerFields` et `cookies` (`request.header('X-Token')`, `request.cookie('theme')`), les paramètres de requête et de route sont décodés (pourcentages et `+`). La réponse porte `headers` et `cookies` (`withHeader`, `withCookie`, `HttpResponse.redirect`, `noContent`). `server.middleware((request, next) => …)` chaîne des intergiciels (le premier ajouté s'exécute en premier) ; `put`, `del` et `patch` complètent `get` et `post`. Le serveur accepte les corps `Transfer-Encoding: chunked` et des requêtes jusqu'à 16 Mio, garde les connexions ouvertes (keep-alive, 100 requêtes ou 5 secondes d'inactivité), écoute en IPv4 et en IPv6 (`::` pour les deux) et compresse en gzip les réponses textuelles d'au moins 1 Kio quand le client l'accepte (`server.compress(octets)` règle le seuil, 0 désactive). Le client suit les redirections (`maxRedirects`, 5 par défaut), demande et décompresse le gzip, accepte les URL IPv6 (`http://[::1]:8080/`) et expose `response.header(nom)`.

### Ligne de commande (`Cli`)

`parseArguments(args, spec)` lit les arguments du train d'entrée selon une `CliSpec` (`program`, `description`, `options`) construite avec `option(nom, description, lettre, défaut, obligatoire)` et `flag(nom, description, lettre)` (les paramètres se donnent dans l'ordre : RGL n'a pas d'arguments nommés ; `null` passe un défaut). Il accepte `--port 80`, `--port=80`, `-p 80`, `-p80`, `-p=80`, des drapeaux groupés (`-vq`) et `--` pour finir les options ; un nombre négatif (`-5`) est un argument comme un autre. Le résultat est un `CliArguments ! ParseError` : `values` (par nom long), `positional`, et les extensions `has`, `value`, `valueOr`, `intValue`, `doubleValue`. Une option inconnue, une valeur manquante, une valeur donnée à un drapeau et une option obligatoire absente sont des `ParseError` dont `input` est l'argument en cause. `usage(spec)` écrit l'aide, une ligne par option, alignées.

### Statistiques et hasard

- Extensions de tableau de nombres (`stats_ext`) : `median`, `percentile(p)` (interpolation linéaire, 0 à 100), `variance` et `standardDeviation` (population, ou échantillon avec `true`), `range` ; `null` pour un tableau vide.
- `Random` : `nextDouble(min, max)`, `nextGaussian(moyenne, écart)` (Box-Muller) et `Random.fromEntropy()` (graine tirée du générateur du système : imprévisible pour un tiers, mais la suite reste celle de Park-Miller ; pour une clé, `randomBytes`). `choice` et `shuffled` existaient déjà.

### Crypto : SHA-512, PBKDF2, mots de passe

`sha512`, `hmacSha512`, `pbkdf2Sha256` et `pbkdf2Sha512` (RFC 8018, octets en sortie), `hashPassword(motDePasse, itérations: 600000)` (PBKDF2-HMAC-SHA256, sel aléatoire, texte `pbkdf2-sha256$itérations$sel$clé` à ranger tel quel) et `verifyPassword(motDePasse, rangé)` (temps constant, `false` si le texte rangé est illisible). Chiffrement : `aesEncrypt(clé, octets)` / `aesDecrypt(clé, octets)` (AES-GCM, clé de 16, 24 ou 32 octets, nonce de 12 octets tiré au hasard et placé devant le texte chiffré, balise de 16 octets à la fin ; `null` si la clé est mauvaise ou si les octets ont été modifiés) et, pour maîtriser le nonce ou ajouter des données associées authentifiées, `aesGcmSeal` / `aesGcmOpen`. L'implémentation est écrite à la main (vérifiée sur les vecteurs d'essai officiels du NIST) et AES y est à tables : elle n'est pas protégée contre un programme local qui observerait le cache du processeur. Une clé se dérive d'un mot de passe par `pbkdf2Sha256`. Pas de signatures asymétriques.

### Authentification et formulaires HTTP

- `Auth` : `parseBasicAuthorization`, `parseBearerAuthorization`, `basicAuthorization(utilisateur, mot de passe)`, `bearerAuthorization(jeton)`, et les jetons JWT HS256 : `signJwt(charge JSON, secret)`, `verifyJwt(jeton, secret)` (signature en temps constant, `exp` en secondes vérifié ; `null` si refusé).
- `HttpAuth` : les intergiciels `basicAuth(verify, zone)`, `bearerAuth(verify, zone)`, `jwtAuth(secret, zone)` (à ajouter par `server.middleware(…)` ; sans identifiants valides la réponse est un 401 avec `WWW-Authenticate` et la route n'est pas appelée ; les requêtes `OPTIONS` passent, car une pré-vérification CORS n'envoie pas d'identifiants) et `unauthorized(schéma, zone)`.
- Sur une requête : `request.basicCredentials()`, `request.bearerToken()`, `request.multipart()` (`[FormPart]` : `name`, `fileName`, `contentType`, `content` tel que reçu, octets d'un fichier compris — `as bytes`) et `request.form()` (champs `application/x-www-form-urlencoded` décodés).
- Sur un serveur : `server.cors(origine)` ajoute `Access-Control-Allow-Origin` aux réponses et répond 204 aux pré-vérifications `OPTIONS`.
- Sessions : `request.session(secret)` lit les données (`[string:string]?`) d'un cookie signé HMAC-SHA256 (comme un JWT, avec expiration), `response.withSession(secret, données, secondes: 3600)` le pose, `response.withoutSession()` le supprime ; le serveur ne garde rien, mais le navigateur peut lire le cookie : pas de secrets dedans. Limitation de débit (`HttpLimit`) : `server.middleware(rateLimit(100, 1min))` répond 429 avec `Retry-After` au-delà de la limite (l'appelant est le premier élément de `X-Forwarded-For`, sinon un compteur unique ; `rateLimitBy(limite, fenêtre, (r) => clé)` choisit la clé) ; le compteur est partagé entre les threads du serveur, par un verrou en C++. Pas encore : sessions gardées côté serveur (identifiant opaque), adresse du pair dans `HttpRequest`. Un fichier `forge: extend` ne peut pas importer un fichier qui se sert d'extensions (il est chargé avant elles) : les extensions de `HttpRequest` et `HttpServer` sont donc écrites dans `Http.forge`, qui importe `Auth` et `HttpForm`.

### Contrôle d'accès par rôle : `@before` et annotations déclarées

`denyUnless(roleOf, allowed)` (`HttpAuth`) fabrique un contrôle : `roleOf` est **ton** gear `(HttpRequest) => Role?` (le rôle vient de la session, d'un jeton JWT ou d'une base : la std n'en impose aucune origine) ; le contrôle répond 401 sans rôle, 403 si le rôle n'est pas dans `allowed`, et rend `null` sinon. On le branche avec `@before(contrôle)` devant la route, ou, plus lisible, par une annotation que tu déclares :

```
enum Role { Admin, Editor, Reader }
gear roleCourant(HttpRequest r): Role? { … }
annotation exigeRole(params [Role] roles) = @before(denyUnless(roleCourant, roles));

@exigeRole(Role.Admin, Role.Editor)
gear tableauDeBord(HttpRequest r): HttpResponse { … }
```

Une annotation déclarée est un paquet d'annotations de base ; ses paramètres se lisent comme des constantes dans son corps ; elle est développée à la compilation. `forbidden()` est la réponse 403.

### Fuseaux horaires (`TimeZone`)

`timeZone('Europe/Paris')` (la casse est ignorée ; `UTC`, `Z`, `+05:30` et `UTC+2` marchent aussi, `zoneNames()` donne les noms connus) rend un `TimeZone?`. Les règles d'heure d'été sont celles d'aujourd'hui, écrites dans la std (Europe depuis 1996, États-Unis et Canada depuis 2007, Australie, Nouvelle-Zélande) : aucune dépendance au système, mais une date plus ancienne peut avoir eu un autre décalage, et une ville dont la règle est particulière (Israël, Chili, Égypte, Maroc…) n'est pas dans la liste. `utcOffset(zone, instant)` donne le décalage (heure d'été comprise), `isDaylightSaving`, `localTime(zone, instant)` les composantes de l'heure locale (`LocalTime` : année, mois, jour, heure…, `dayOfWeek`, décalage), `inZone(instant, zone)` l'heure du mur écrite comme une date (donc lisible par `year()`, `hour()` et `format('dd/MM/yyyy HH:mm')`), `fromZone(zone, heureDuMur)` l'inverse (quand l'heure existe deux fois, la première ; quand elle n'existe pas, décalée d'une heure) et `formatIso(instant, zone)` écrit `2026-10-24T12:30:00+02:00`. `localOffset(instant)` et `toLocal()` de `Time` restent ceux du système.

### File à deux bouts, cache LRU, complexes, date avec motif

- `Deque<T>` (`newDeque<T>()`) : `pushFront`, `pushBack`, `popFront`, `popBack`, `peekFront`, `peekBack`, `size`, `isEmpty`, `toArray`. Deux piles ; quand l'une se vide, la moitié de l'autre la remplit (temps constant en moyenne). Un `Set<K>` est déjà rangé dans l'ordre de ses clés (`values()`).
- `LruCache<K, V>` (`newLruCache<K, V>(capacité)`) : `put`, `get` (rend la clé la plus récente), `peek` et `has` (sans changer l'ordre), `remove`, `size`, `keys` (de la moins récente à la plus récente) ; ajouter une clé nouvelle à un cache plein retire la moins récente. Les clés suivent les mêmes règles que celles d'un dictionnaire ; l'ordre de récence est une liste, donc `get` et `put` coûtent un temps proportionnel à la taille : pour quelques centaines d'entrées, pas pour des millions.
- `Complex` (`complex(re, im)`) : les opérateurs `+ - * /` et le `-` unaire, `magnitude`, `phase`, `conj`, `fromPolar`, `complexExp`, `complexPow(z, n)`, `complexSqrt`, `complexText`. Pas de décimal exact pour l'instant.
- `parseDateTime(texte, motif)` : le miroir de `format` (`yyyy`, `yy`, `MM`, `dd`, `HH`, `mm`, `ss`, `SSS`, écrits avec leurs zéros, le reste à l'identique) ; `null` si le texte ne suit pas le motif ou si la date n'existe pas.

### Tas, clés extrêmes, décimaux exacts, aides de test

- `Heap<T>` (`newHeap<T>(compare)`) : un tas binaire, `push` et `pop` en temps logarithmique, `peek`, `size`, `isEmpty` ; à préférer à `PriorityQueue` (insertion triée, linéaire) quand il y a beaucoup d'éléments. Un dictionnaire est rangé dans l'ordre de ses clés : `firstKey()` et `lastKey()` en donnent les extrémités (un arbre ordonné n'a donc pas de type à part).
- `Decimal` (`Decimal.forge`) : un nombre décimal exact (`units` / dix puissance `scale`) avec les opérateurs `+ - * /`, le `-` unaire et les comparaisons (`1.50 eq 1.5`), `parseDecimal`, `decimalText`, `normalizeDecimal`, `roundDecimal` (la moitié s'éloigne de zéro), `divide(a, b, échelle)`, `decimalToDouble`. Calculs sur des entiers de 64 bits (environ 18 chiffres) : un débordement arrête le programme.
- `Test` : `withFixture(setup, test, teardown)`, `benchmark(nom, répétitions, action)` (écrit le temps et le rend), un journal d'appels de doublure (`CallLog`, `newCallLog`, `recordCall`, `callCount`, `assertCalled`).
- `parseDateTime(texte, motif)` lit aussi `M`, `d`, `H`, `m` et `s` (un ou deux chiffres, sans zéro obligatoire).
- Le client HTTP vit dans son propre fichier : `use <HttpClient> only {HttpClient, HttpClientResponse};`.

### Valeurs de données, MessagePack, CBOR, Markdown

- `DataValue` (`Data.forge`) : une valeur dont la forme n'est connue qu'à l'exécution — `dataNull`, `dataBool`, `dataInt`, `dataDouble`, `dataText`, `dataBytes`, `dataList`, `dataMap` (clés textuelles, ordre gardé), `dataGet`, `dataAt`, `dataToText` (lisible, proche du JSON). Elle sert de pivot aux formats binaires.
- `toMessagePack` / `fromMessagePack` et `toCbor` / `fromCbor` : écrivent et lisent une `DataValue` (entiers sous leur forme la plus courte, flottants sur 64 bits, tailles définies ; CBOR lit aussi les flottants de 16 et 32 bits). Vérifiés sur les exemples des spécifications. Refusés : les types d'extension de MessagePack, les étiquettes et tailles indéfinies de CBOR, les clés qui ne sont pas du texte, les entiers non signés au-delà de 63 bits ; la lecture rend `null` pour des octets invalides, tronqués ou suivis d'octets en trop.
- `markdownToHtml(texte)` : titres, paragraphes, listes à puces et numérotées, citations, blocs de code, règle horizontale, `**gras**`, `*italique*`, `` `code` ``, liens et images. Le texte est toujours échappé, et un lien ou une image n'est écrit que si son adresse est `http`, `https`, `mailto:` ou relative (jamais `javascript:`). Pas de listes imbriquées, de tableaux, ni de code indenté.

### Images PNG

`decodePng(octets)` lit un fichier PNG (8 bits par canal ; gris, gris et alpha, RGB, RGBA, palette avec sa transparence ; les cinq filtres ; sans entrelacement ; les totaux de contrôle sont vérifiés) et rend une `Image` (`width`, `height`, `pixels` en RGBA, 4 octets par pixel, de haut en bas) ou `null`. `encodePng(image)` écrit un PNG RGBA 8 bits. `newImage`, `imageFromRgba` et `pixelAt` construisent et lisent. Les images ont au plus 16384 × 16384 pixels. Vérifié contre le lecteur et l'écrivain PNG du compilateur et contre des fichiers filtrés à la main.

### Terminal, signaux, observation de fichiers, processus enfants

Ces quatre fichiers s'appuient sur deux composants du runtime (`System` et `Child`), liés seulement quand un programme les utilise.

- `Terminal.forge` : `isTerminal()`, `isTerminalOutput()`, `terminalColumns()`, `terminalRows()`, `enableAnsi()` (Windows : active les séquences ANSI), `readPassword(invite)` (saisie sans écho ; sans terminal, une ligne ordinaire), `readKey(délai)` (une touche à la fois : `up`, `down`, `left`, `right`, `home`, `end`, `pageup`, `pagedown`, `delete`, `enter`, `tab`, `escape`, `backspace` ou le caractère ; `null` si l'entrée n'est pas un terminal), `progressBar(fait, total, largeur)` (le texte `[#####-----] 50%`), `showProgress(…)` (le réécrit sur la ligne) et `chooseMenu(titre, options, choix)` (un menu aux flèches, `null` sans terminal ou avec Échap).
- `Signals.forge` : `onSignal('interrupt' | 'terminate' | 'hangup' | 'user1' | 'user2')` demande à garder le signal au lieu de laisser le système arrêter le programme (`hangup`, `user1` et `user2` n'existent que sous POSIX), `takeSignal()` rend celui qui est arrivé (`null` sinon), `waitSignal(délai)` l'attend ; `watch(chemin, délai)` rend les `FileChange` (`kind` : `created`, `modified` ou `removed`, et `path`) d'un fichier ou d'un dossier, en comparant des instantanés (taille et date) toutes les 50 ms — sans API propre à une plateforme, donc sans notification instantanée.
- `Child.forge` : `startChild(programme, arguments)` (et `startChildIn(dossier, …)`) lance un processus qui garde ses trois flux ouverts ; `childWrite`, `childWriteBytes` et `closeChildInput` lui écrivent, `childRead`/`childReadLine` lisent sa sortie, `childReadError`/`childReadErrorLine` son erreur (un `ChildChunk` : `text`, `timedOut`, `closed`), `childWait` attend sa fin (son code, `null` au délai), `killChild` l'arrête, `releaseChild` ferme ce qui reste. Si l'enfant remplit un flux que personne ne lit, il se bloque : lis ou ferme les deux. Un programme nommé `rgl` est refusé. Une lecture par morceaux peut couper un caractère UTF-8 en deux (chaque octet refusé devient U+FFFD) ; les lectures par lignes ne coupent jamais un caractère.

### Client WebSocket (`WebSocket`)

`webSocketConnect('ws://hôte:port/chemin', délai)` fait la poignée de main HTTP (clé tirée au hasard, `Sec-WebSocket-Accept` vérifié) et rend un `WebSocketClient` (`null` si l'adresse n'est pas un `ws://`, si la connexion échoue ou si le serveur refuse). `webSocketSend` (texte), `webSocketSendBytes`, `webSocketReceive(client, délai)` (un `ClientMessage` : `kind` `text`, `binary`, `close` ou `timeout`, avec `text`, `data` et `code`) et `webSocketClose(client, code)` ; le client est une variable `mut`, qui garde les octets reçus pas encore lus comme message. Les pings reçoivent leur pong sans que tu le voies, un message coupé en plusieurs trames est rassemblé, une fermeture du serveur reçoit sa réponse avant d'être rendue, un message de plus de 16 Mio ferme la connexion (code 1009), un texte invalide aussi (1007). Les trames envoyées sont masquées. **`wss://` n'est pas pris en charge** : la std n'a pas de flux TLS brut pour les sockets (le TLS n'existe que dans le client et le serveur HTTP).

### Normalisation Unicode (`Unicode`)

`normalizeNfc`, `normalizeNfd`, `normalizeNfkc`, `normalizeNfkd`, `isNfc`, `stripAccents` (« Héros à l'école » donne « Heros a l'ecole ») et `equalsIgnoringAccents`. Les tables viennent de la base de caractères Unicode (`runtime/unicode/generate_tables.py` les écrit dans `unicode_tables.hpp` depuis `UnicodeData.txt` et `CompositionExclusions.txt`) ; les syllabes Hangul sont calculées. Le composant `Unicode` du runtime est vérifié sur le fichier de conformité du consortium (`NormalizationTest.txt`, une vingtaine de milliers de cas, copié dans `runtime/tests`). Un octet qui n'est pas de l'UTF-8 valide est recopié tel quel. `equalsIgnoringAccents` passe par NFKC, retire les accents et ignore la casse ; les lettres sans décomposition (« ø », « ß ») restent distinctes. Pour changer de version d'Unicode : télécharger les trois fichiers, relancer le script, puis le test du runtime.

### HTTP/2 (client)

Voir `Docs/Http.md` : le client HTTPS négocie `h2` par ALPN et parle HTTP/2 (HPACK, contrôle de flux) sans changement de ton code ; une réponse `chunked` est recomposée. Le serveur reste en HTTP/1.1.

### Cookies du client HTTP, signatures Ed25519

- `CookieJar` (`HttpClient.forge`, `newCookieJar()`) : `client.sendWithJar(jar, 'GET', url)` joint les cookies du pot à la requête et range ceux de la réponse (lignes `Set-Cookie`) ; `jarHeader(jar, url)` et `jarStore(jar, url, réponse)` font les deux moitiés à la main. Les cookies sont gardés par nom de machine ; `Path`, `Domain`, `Secure` et `Expires` ne sont pas lus (seul `Max-Age=0` supprime) ; ceux d'une étape intermédiaire de redirection ne sont pas gardés.
- `ed25519Seed()`, `ed25519PublicKey(graine)`, `ed25519Sign(graine, message)` et `ed25519Verify(clé, message, signature)` (`Ed25519.forge`) : signatures de la RFC 8032 sur 32 octets de clé, 64 de signature. Le calcul sur la courbe est écrit à la main (d'après TweetNaCl, constantes calculées plutôt que recopiées), le hachage est le SHA-512 de la std ; vérifié sur les vecteurs de la spécification. Il n'est pas protégé contre une observation fine du temps de calcul par un programme local. Pas de RSA ni d'ECDSA.
