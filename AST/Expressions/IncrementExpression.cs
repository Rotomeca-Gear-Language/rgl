using Rgl.AST.Interfaces;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// <c>increment v</c>, <c>decrement v</c>, <c>post_increment v</c> ou <c>post_decrement v</c>, avec
/// éventuellement un pas : <c>increment&lt;3&gt; v</c> ajoute 3.
/// </summary>
/// <param name="Span">Du mot-clé à l'opérande.</param>
/// <param name="Operator">Le mot-clé, par exemple <see cref="TokenKind.PostIncrementKeyword"/>.</param>
/// <param name="Step">Le littéral entier entre chevrons ; <see langword="null"/> s'il n'est pas écrit (pas de 1).</param>
/// <param name="Operand">La variable modifiée.</param>
internal sealed record IncrementExpression(SourceSpan Span, TokenKind Operator, LiteralExpression? Step, IExpression Operand) : IExpression;
