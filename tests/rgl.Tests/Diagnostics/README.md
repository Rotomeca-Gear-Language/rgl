# Couverture des diagnostics E/W

Depuis la racine du dépôt :

```sh
dotnet test tests/rgl.Tests/rgl.Tests.csproj --filter FullyQualifiedName~DiagnosticCoverageTests
```

La liste des codes vient des constantes de `DiagnosticCodes`, et non d’un total
figé. Chaque code E/W possède son propre cas xUnit. Un test supplémentaire
vérifie qu’aucun code déclaré n’est absent du catalogue.

La couverture repose sur des régressions **exécutées avec leurs assertions** :

- les fichiers golden sont réellement lexés et parsés, puis leurs arbres et
  diagnostics sont comparés aux attendus ;
- les théories ordinaires dont un paramètre `expected`, `code` ou `expectedCode`
  attend explicitement un diagnostic sont rejouées avec leurs `InlineData` ;
- les tests dont le code attendu est fixé dans le corps sont reliés explicitement
  dans `AddSpecificRegressions`.

La présence du nom d’un code dans un commentaire ou un exemple ne suffit pas.
Le catalogue ne rejoue pas les théories conditionnelles `ZigTheory` : ces
régressions de diagnostics ne demandent pas de compilation native.

Le rapport xUnit contient la correspondance entre chaque code et la régression
choisie. Pour l’afficher :

```sh
dotnet test tests/rgl.Tests/rgl.Tests.csproj --filter FullyQualifiedName~DiagnosticCoverageTests --logger 'console;verbosity=detailed'
```

## Ajouter un diagnostic

1. Ajouter une régression qui provoque le diagnostic et vérifie son code.
2. Si elle suit une théorie déjà reconnue, ses nouvelles données sont reprises
   automatiquement. Sinon, la relier dans `AddSpecificRegressions`.
3. Relancer la commande ci-dessus. Un code sans régression fait échouer le test
   d’inventaire et son cas individuel.

Ce contrôle garantit au moins une régression par code ; il ne prétend pas couvrir
tous les chemins, messages et variantes de chaque diagnostic. Les notices `I`,
les diagnostics documentaires `DOC` et ceux de l’analyseur C# sont hors de son
périmètre.
