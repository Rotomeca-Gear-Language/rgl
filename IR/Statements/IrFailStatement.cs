using Rgl.IR.Expressions;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>
/// <c>fail</c> dans un guard : le guard s'arrête et renvoie l'erreur, dont <c>where</c> reçoit la
/// position du <c>fail</c>. Comme <c>return</c>, il ne laisse jamais l'exécution passer à la suite.
/// </summary>
/// <param name="Span">L'instruction dans le source : sa position est le <c>where</c> de l'erreur.</param>
/// <param name="Error">L'erreur envoyée, un mold d'erreur (un texte ou un <c>fail;</c> seul deviennent une Exception).</param>
internal sealed record IrFailStatement(SourceSpan Span, IrExpression Error) : IrStatement(Span);
