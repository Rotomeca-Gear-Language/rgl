using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary><c>&lt;- clock</c> : l'instant présent, lu sur l'horloge du système.</summary>
/// <param name="Span">La lecture.</param>
internal sealed record IrNow(SourceSpan Span) : IrExpression(Span, PrimitiveType.DateTime);

/// <summary>La date de ces millisecondes depuis 1970 (<c>n as datetime</c>, après conversion de <c>n</c> en <c>long</c>).</summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Value">Les millisecondes, en <c>long</c>.</param>
internal sealed record IrDateTimeOf(SourceSpan Span, IrExpression Value) : IrExpression(Span, PrimitiveType.DateTime);
