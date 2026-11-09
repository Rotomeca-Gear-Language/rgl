using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Un appel dans une expression : <c>f(a, b)</c>.</summary>
/// <param name="Span">De l'expression appelée à la parenthèse fermante.</param>
/// <param name="Callee">Ce qui est appelé, le plus souvent un <see cref="NameExpression"/>.</param>
/// <param name="Arguments">Les arguments, dans l'ordre.</param>
/// <param name="TypeArguments">Les arguments génériques écrits, <c>f&lt;int&gt;(x)</c> ; <see langword="null"/> sans eux.</param>
internal sealed record CallExpression(SourceSpan Span, IExpression Callee, IReadOnlyList<IExpression> Arguments, IReadOnlyList<ITypeNode>? TypeArguments = null) : IExpression;
