# Commentaires documentaires

Les commentaires `///` acceptent les sections habituelles (`summary`, `param`,
`returns`, etc.) et des balises XML personnalisées. Ces dernières apparaissent
au survol sous leur nom et sont conservées, attributs compris, dans l'export XML.
L'export JSON les expose dans `doc.custom` (`name`, `text`, `xml`).

```rgl
/// Effectue le traitement.
/// <attention niveau="important">Le fichier doit être ouvert.</attention>
gear traiter(): void {}
```

La balise facultative `<lang name="…">` englobe une documentation complète :
résumé, paramètres, retour, exemples et sections personnalisées. Elle se place
uniquement à la racine du commentaire, jamais à l’intérieur d’une section ni
d’un autre bloc `<lang>` :

```rgl
/// <lang name="fr">
/// <summary>Double un nombre.</summary>
/// <param name="x">Le nombre.</param>
/// <returns>Le double.</returns>
/// </lang>
/// <lang name="en">
/// <summary>Doubles a number.</summary>
/// <param name="x">The number.</param>
/// <returns>Twice the number.</returns>
/// </lang>
gear doubler(int x): int => x * 2;
```

La sélection utilise la culture d’interface du système (`CurrentUICulture`)
sur lequel tourne le compilateur. La langue d’interface de VS Code ne la remplace
pas. Ordre :
correspondance exacte, puis ses parents (`fr-CA`, puis `fr`), puis `en` (ou la
première variante `en-…`), puis le premier bloc disponible. Les noms de langue
ne sont pas sensibles à la casse. Un attribut `name` vide ou absent est signalé.
Le contenu sans balise `lang` reste commun et les commentaires existants gardent
leur comportement. Le texte libre au début d'un bloc de langue peut servir de
résumé implicite.

Les exports XML conservent toutes les traductions. Dans le JSON, les champs
habituels et `custom.text` contiennent le texte sélectionné ; `doc.localizedXml`
conserve les blocs XML avec toutes les langues pour les autres outils.

- [Blocs C++ et contrats de confiance — A2.4B](InlineCpp.md) : syntaxe, unsafe/pure/trusted et restrictions du manifeste.
- [Surcharges de gears](GearOverloads.md) : signatures, résolution, génériques, paramètres par défaut et ambiguïtés.
- [Bibliothèque standard](StandardLibrary.md) : collections, chaînes, mathématiques, aléatoire, chemins et fichiers.
- [Serveur HTTP](Http.md) : requêtes, réponses, routage, paramètres, écoute et limites de la première version.
