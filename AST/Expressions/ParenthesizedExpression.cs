using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Une expression entre parenthèses : <c>(a + b)</c>.</summary>
/// <param name="Span">De la parenthèse ouvrante à la fermante.</param>
/// <param name="Inner">L'expression entre les parenthèses.</param>
internal sealed record ParenthesizedExpression(SourceSpan Span, IExpression Inner) : IExpression;
