using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>
/// Une étape de parcours d'un train : <c>||&gt; actif !&gt; [journal]</c> applique l'étape à chaque élément
/// du tableau reçu ; son rail reçoit l'erreur de chaque élément raté et le parcours continue. Avec
/// <c>|!|&gt;</c>, le premier échec abandonne tout, et le rail termine le train comme celui d'un guard.
/// </summary>
/// <param name="Span">De l'opérateur à la fin de l'étape.</param>
/// <param name="IsStrict">Vrai pour <c>|!|&gt;</c>.</param>
/// <param name="Target">Ce qui est appliqué à chaque élément.</param>
/// <param name="Rail">Le rail de l'étape, s'il y en a un.</param>
internal sealed record EachStep(SourceSpan Span, bool IsStrict, EachTarget Target, Train.Rail? Rail = null) : IStep;
