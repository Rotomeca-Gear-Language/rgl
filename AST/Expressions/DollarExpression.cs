using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary><c>$</c> dans une expression : la valeur du switch, dans un <c>case eval</c>.</summary>
/// <param name="Span">Le <c>$</c>.</param>
internal sealed record DollarExpression(SourceSpan Span) : IExpression;
