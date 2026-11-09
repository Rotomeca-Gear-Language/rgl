using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Datasets;

/// <summary>
/// Un dataset : une table d'enregistrements dont chaque entrée porte un nom, qui devient son identifiant typé.
/// <c>dataset Moves of Move { attaquer: {name: 'Attaquer', power: 5}, soin: {…} }</c> lit chaque entrée comme un <c>Move</c> déclaré ;
/// <c>dataset Moves as Move { … }</c> crée le mold <c>Move</c> depuis les entrées ; <c>dataset Moves from "moves.json" of Move;</c>
/// lit les entrées d'un fichier de données.
/// </summary>
/// <param name="Span">Du mot <c>dataset</c> à l'accolade fermante ou au <c>;</c>.</param>
/// <param name="Name">Le nom du dataset : celui de sa table, de ses identifiants (<c>Moves.id</c>) et de ses entrées (<c>Moves.attaquer</c>).</param>
/// <param name="Mold">Le nom du mold de ses entrées.</param>
/// <param name="CreatesMold">Vrai pour <c>as</c> : le mold est créé par le dataset ; faux pour <c>of</c> : il est déclaré ailleurs.</param>
/// <param name="Source">Le fichier de données (<c>from "moves.json"</c>), tel qu'écrit ; <see langword="null"/> pour des entrées écrites.</param>
/// <param name="Entries">Les entrées écrites, dans l'ordre ; vides pour un fichier de données.</param>
internal sealed record DatasetDeclaration(SourceSpan Span, Identifier Name, Identifier Mold, bool CreatesMold, AST.Expressions.LiteralExpression? Source, IReadOnlyList<DatasetEntry> Entries) : IStatement;

/// <summary>
/// Les entrées qu'un autre fichier ajoute à un dataset : <c>extend dataset Moves { braise: {name: 'Braise', power: 9} }</c>. Les ajouts se fondent dans la
/// table du dataset avant la liaison, après celles du fichier qui le déclare : les identifiants du jeu de base ne bougent jamais (un mod ou un DLC s'écrit
/// ainsi, sans rien changer au jeu de base). Le fichier qui l'écrit importe celui qui déclare le dataset.
/// </summary>
/// <param name="Span">De <c>extend</c> à l'accolade fermante.</param>
/// <param name="Name">Le nom du dataset étendu.</param>
/// <param name="Entries">Les entrées ajoutées, dans l'ordre.</param>
internal sealed record DatasetExtension(SourceSpan Span, Identifier Name, IReadOnlyList<DatasetEntry> Entries) : IStatement;

/// <summary>Une entrée d'un dataset : <c>attaquer: {name: 'Attaquer', power: 5}</c>.</summary>
/// <param name="Span">L'entrée, sans sa virgule.</param>
/// <param name="Name">Son nom, qui donne <c>Moves.attaquer</c>.</param>
/// <param name="Value">Sa valeur, du mold du dataset.</param>
internal sealed record DatasetEntry(SourceSpan Span, Identifier Name, IExpression Value) : IAstNode;
