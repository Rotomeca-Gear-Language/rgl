using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Generics;

/// <summary>
/// Un paramètre générique d'un gear ou d'un mold : <c>T</c>, <c>T: number</c>, <c>T: {int x}</c>, <c>T: error</c>,
/// ou <c>int max</c> pour une valeur connue à la compilation.
/// </summary>
/// <param name="Span">Le paramètre entier.</param>
/// <param name="Name">Son nom.</param>
/// <param name="Constraint">Ce que le type doit être (<c>number</c>, <c>error</c>, un type mold anonyme) ; <see langword="null"/> sans contrainte ou pour une valeur.</param>
/// <param name="ValueType">Le type de la valeur pour un paramètre de valeur (<c>int max</c>) ; <see langword="null"/> pour un type.</param>
internal sealed record TypeParameter(SourceSpan Span, Identifier Name, ITypeNode? Constraint, ITypeNode? ValueType) : IAstNode;
