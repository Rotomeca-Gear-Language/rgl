using Rgl.IR.Expressions;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>Une expression évaluée pour son effet, sa valeur ignorée : <c>increment v;</c>.</summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Expression">L'expression.</param>
internal sealed record IrExpressionStatement(SourceSpan Span, IrExpression Expression) : IrStatement(Span);
