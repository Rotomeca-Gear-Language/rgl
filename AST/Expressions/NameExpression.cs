using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Un nom utilisé comme valeur : variable, paramètre (<c>args</c>)…</summary>
/// <param name="Name">Le nom.</param>
internal sealed record NameExpression(Identifier Name) : IExpression
{
    /// <inheritdoc/>
    public SourceSpan Span => Name.Span;
}
