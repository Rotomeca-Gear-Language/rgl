using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Le pipeline dans une expression : <c>v |&gt; g(a, b)</c> vaut <c>g(v, a, b)</c>, la valeur de
/// gauche devenant le premier argument.
/// </summary>
/// <param name="Span">De la valeur à la fin de l'appel.</param>
/// <param name="Value">La valeur qui entre dans le gear.</param>
/// <param name="Gear">Le gear appelé.</param>
/// <param name="Arguments">Les arguments écrits entre parenthèses, après la valeur ; vide sans parenthèses.</param>
/// <param name="OperatorSpan">Le <c>|&gt;</c>.</param>
internal sealed record PipeExpression(SourceSpan Span, IExpression Value, Identifier Gear, IReadOnlyList<IExpression> Arguments, SourceSpan OperatorSpan) : IExpression;
