using Rgl.AST.Interfaces;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Une opération à deux opérandes : <c>a + b</c>, <c>x eq 0</c>, <c>flags bitand 4</c>.</summary>
/// <param name="Span">Du début de l'opérande gauche à la fin de l'opérande droit.</param>
/// <param name="Left">L'opérande gauche.</param>
/// <param name="Operator">L'opérateur, par exemple <see cref="TokenKind.Plus"/> ou <see cref="TokenKind.EqKeyword"/>.</param>
/// <param name="Right">L'opérande droit.</param>
/// <param name="OperatorSpan">L'opérateur dans le source : l'endroit signalé si l'opération échoue à l'exécution.</param>
internal sealed record BinaryExpression(SourceSpan Span, IExpression Left, TokenKind Operator, IExpression Right, SourceSpan OperatorSpan) : IExpression;
