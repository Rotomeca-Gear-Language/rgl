using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// <c>task f(x)</c> : appelle un gear ordinaire sur le pool de threads du runtime ; l'appel rend tout de suite une promesse de son résultat.
/// <c>task</c> est un mot contextuel : suivi d'un nom, il lance l'appel qui suit.
/// </summary>
/// <param name="Span">Du mot <c>task</c> à la fin de l'appel.</param>
/// <param name="Call">L'appel à exécuter sur le pool.</param>
internal sealed record TaskCallExpression(SourceSpan Span, IExpression Call) : IExpression;
