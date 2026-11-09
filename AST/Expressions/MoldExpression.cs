using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Une valeur de mold : <c>{x: 1.0, y: 2.0}</c>, <c>new Vec2 {x: 1.0, y: 2.0}</c>, <c>{...pos, x: 3.0}</c>
/// ou le raccourci <c>{x, y}</c>. Sans <c>new</c>, son type vient de là où elle va (déclaration,
/// paramètre, retour), ou c'est un mold anonyme.
/// </summary>
/// <param name="Span">Du <c>new</c> ou de l'accolade ouvrante à l'accolade fermante.</param>
/// <param name="Type">Le type écrit après <c>new</c> ; <see langword="null"/> sans <c>new</c>.</param>
/// <param name="Entries">Les champs et les spreads, dans l'ordre d'écriture.</param>
internal sealed record MoldExpression(SourceSpan Span, ITypeNode? Type, IReadOnlyList<IMoldEntry> Entries) : IExpression;

/// <summary>Une entrée d'une valeur de mold : un champ avec sa valeur, ou un spread.</summary>
internal interface IMoldEntry : IAstNode;

/// <summary><c>x: 1.0</c>, ou le raccourci <c>x</c> qui vaut <c>x: x</c>.</summary>
/// <param name="Span">L'entrée.</param>
/// <param name="Name">Le nom du champ.</param>
/// <param name="Value">Sa valeur ; pour le raccourci, le nom lui-même.</param>
/// <param name="IsShorthand">Vrai pour le raccourci <c>{x}</c>.</param>
internal sealed record MoldFieldValue(SourceSpan Span, Identifier Name, IExpression Value, bool IsShorthand) : IMoldEntry;

/// <summary><c>...pos</c> : tous les champs d'une autre valeur de mold.</summary>
/// <param name="Span">Le spread.</param>
/// <param name="Value">La valeur dont les champs sont repris.</param>
internal sealed record MoldSpreadValue(SourceSpan Span, IExpression Value) : IMoldEntry;
