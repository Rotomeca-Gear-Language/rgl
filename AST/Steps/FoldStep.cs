using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>
/// Une étape d'accumulation d'un train : <c>|+&gt; 0 with compter !&gt; [rapport]</c> réduit le tableau
/// reçu à une seule valeur. Le rail reçoit l'échec d'un guard appliqué, qui fait échouer toute l'accumulation.
/// </summary>
/// <param name="Span">De l'opérateur à la fin de l'étape.</param>
/// <param name="Seed">La valeur de départ.</param>
/// <param name="Target">L'étape appliquée à l'accumulateur et à chaque élément.</param>
/// <param name="Rail">Le rail de l'étape, s'il y en a un.</param>
internal sealed record FoldStep(SourceSpan Span, IExpression Seed, Expressions.EachTarget Target, Train.Rail? Rail = null) : IStep;
