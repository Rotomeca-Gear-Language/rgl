using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>Un tuple : <c>(int, string)</c>, ou avec des noms : <c>(int x, string nom)</c>.</summary>
/// <param name="Span">Des parenthèses.</param>
/// <param name="Elements">Les éléments, au moins deux.</param>
internal sealed record TupleType(SourceSpan Span, IReadOnlyList<TupleTypeElement> Elements) : ITypeNode;

/// <summary>Un élément d'un type tuple : son type, et son nom s'il en a un.</summary>
/// <param name="Span">L'élément.</param>
/// <param name="Type">Son type.</param>
/// <param name="Name">Son nom ; <see langword="null"/> sans nom (il se lit alors Item1, Item2…).</param>
internal sealed record TupleTypeElement(SourceSpan Span, ITypeNode Type, Identifier? Name) : IAstNode;
