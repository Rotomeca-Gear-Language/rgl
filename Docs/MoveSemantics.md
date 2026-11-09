# Intention de déplacement

`::` à la déclaration et `<-|` à l’affectation expriment une intention de
transfert : la cible reçoit la valeur et le nom source est libéré. Cette règle
ne dépend pas de `mut`.

```rgl
mut [int] source: [1, 2, 3];
var cible:: source;
// var encore: source; // E006 : source libérée
string source: 'nouvelle valeur'; // W001 : nouvelle déclaration
```

Pour une cible déjà déclarée :

```rgl
[int] source: [1, 2, 3];
[int] cible: [];
cible <-| source;
// var encore: source; // E006
```

Le compilateur déplace effectivement la valeur lorsque c’est possible. S’il doit
la copier (constante, paramètre, capture de lambda ou conversion), il émet W014,
mais **le nom source est tout de même libéré**. Le repli vers une copie ne change
pas l’intention de transfert.

```rgl
const int source: 5;
var cible:: source; // W014 : copie, puis libération du nom source
// var encore: source; // E006
```

Le même comportement s’applique à `<-|`. Pour conserver l’accès à la source,
utilisez `:` à la déclaration ou `<-` à l’affectation.

La libération du nom d’un paramètre ou d’une capture ne supprime pas la variable
de l’appelant ni celle du contexte extérieur de la lambda. Une constante globale
consommée dans un gear reste disponible dans les autres gears.

Une copie interdite pour un type à cycle de vie reste une erreur E136 : W014
n’autorise pas à copier un tel type. Une affectation vers soi-même (`a <-| a`)
conserve son comportement : elle ne libère pas `a`. Une expression calculée ne
possède pas de nom source à libérer.
