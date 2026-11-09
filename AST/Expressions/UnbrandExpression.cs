using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary><c>uid°</c> : la valeur d'un ingot, sans sa marque, dans le type de sa base.</summary>
/// <param name="Span">De la valeur au <c>°</c>.</param>
/// <param name="Value">La valeur marquée.</param>
internal sealed record UnbrandExpression(SourceSpan Span, IExpression Value) : IExpression;
