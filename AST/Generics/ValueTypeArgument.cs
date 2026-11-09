using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Generics;

/// <summary>Un argument générique qui est une valeur : le <c>10</c> de <c>Max&lt;10&gt;(v)</c>.</summary>
/// <param name="Value">La valeur écrite.</param>
internal sealed record ValueTypeArgument(IExpression Value) : ITypeNode
{
    /// <inheritdoc/>
    public SourceSpan Span => Value.Span;
}
