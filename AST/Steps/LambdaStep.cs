using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>Une lambda utilisée comme étape de pipeline : <c>|&gt; (x) =&gt; x + 1</c>.</summary>
/// <param name="Span">La lambda entière.</param>
/// <param name="Lambda">La fonction anonyme, qui reçoit la valeur courante.</param>
internal sealed record LambdaStep(SourceSpan Span, LambdaExpression Lambda) : IStep;
