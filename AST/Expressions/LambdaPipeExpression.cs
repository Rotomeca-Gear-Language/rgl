using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Un pipeline d'expression dont l'étape est une lambda : <c>n |&gt; (x) =&gt; x + 1</c>.</summary>
/// <param name="Span">De la valeur à la fin de la lambda.</param>
/// <param name="Value">La valeur transmise à la lambda.</param>
/// <param name="Lambda">La lambda à appeler.</param>
/// <param name="OperatorSpan">Le séparateur <c>|&gt;</c>.</param>
internal sealed record LambdaPipeExpression(SourceSpan Span, IExpression Value, LambdaExpression Lambda, SourceSpan OperatorSpan) : IExpression;
