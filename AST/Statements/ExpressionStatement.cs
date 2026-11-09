using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary>
/// Une expression écrite pour son effet, en instruction : un appel (<c>log(x);</c>,
/// <c>x |&gt; log;</c>) ou <c>increment v;</c> et ses variantes.
/// </summary>
/// <param name="Span">De l'expression au <c>;</c> final.</param>
/// <param name="Expression">L'expression.</param>
internal sealed record ExpressionStatement(SourceSpan Span, IExpression Expression) : IStatement;
