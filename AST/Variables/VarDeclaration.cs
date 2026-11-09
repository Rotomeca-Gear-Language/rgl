using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Variables;

/// <summary>
/// Déclaration de variable : <c>int a: 0;</c>, <c>var a: 0;</c> ou <c>const int A: 0;</c>, précédée de
/// <c>mut</c> quand son contenu se modifie (<c>mut var ps: [];</c>).
/// </summary>
/// <param name="Span">Du premier mot (<c>mut</c>, le mot-clé ou le type) au <c>;</c> final.</param>
/// <param name="IsConst">Vrai pour <c>const</c> : la variable ne peut plus être réassignée.</param>
/// <param name="IsIntef">Vrai pour <c>var</c> : le type est déduit de la valeur.</param>
/// <param name="Type">Le type écrit ; <see langword="null"/> pour <c>var</c>, qui n'en écrit pas.</param>
/// <param name="Name">Le nom de la variable.</param>
/// <param name="Expression">La valeur initiale, toujours présente (manquante après une erreur).</param>
/// <param name="IsMutable">Vrai pour <c>mut</c> : le contenu de la variable se modifie en place.</param>
/// <param name="IsMove">Vrai pour <c>nom:: valeur</c> : la valeur est déplacée quand c'est possible.</param>
internal sealed record VarDeclaration(SourceSpan Span, bool IsConst, bool IsIntef, ITypeNode? Type, Identifier Name, IExpression Expression, bool IsMutable = false, bool IsMove = false, Notes.DeclarationNotes? Notes = null) : IStatement;