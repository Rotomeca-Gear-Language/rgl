using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// <c>await promesse</c> : suspend le gear async courant jusqu'à ce que la promesse soit terminée, et donne son résultat.
/// </summary>
/// <param name="Span">Du mot <c>await</c> à la fin de l'opérande.</param>
/// <param name="Operand">La promesse attendue.</param>
internal sealed record AwaitExpression(SourceSpan Span, IExpression Operand) : IExpression;
