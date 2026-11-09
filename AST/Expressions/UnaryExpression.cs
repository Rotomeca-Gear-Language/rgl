using Rgl.AST.Interfaces;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Une opération à un opérande, placée devant lui : <c>-x</c>, <c>not ok</c>, <c>invert m</c>.</summary>
/// <param name="Span">De l'opérateur à la fin de l'opérande.</param>
/// <param name="Operator">L'opérateur, par exemple <see cref="TokenKind.Minus"/> ou <see cref="TokenKind.NotKeyword"/>.</param>
/// <param name="Operand">L'opérande.</param>
internal sealed record UnaryExpression(SourceSpan Span, TokenKind Operator, IExpression Operand) : IExpression;
