using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// Un intervalle d'entiers : <c>[0..10]</c>, <c>[0..&lt;n by 2]</c>, <c>[10..0]</c>. Un tableau de
/// taille fixe quand ses bornes et son pas sont connus à la compilation, un <c>[T]</c> sinon ; devant
/// un parcours, il est parcouru sans être construit.
/// </summary>
/// <param name="Span">L'intervalle dans le source.</param>
/// <param name="Array">Son type : le tableau de ses valeurs.</param>
/// <param name="Start">La première valeur, du type des éléments.</param>
/// <param name="End">La dernière valeur, ou celle juste après pour une fin exclue.</param>
/// <param name="Step">Le pas, jamais nul : écrit, ou 1 (-1 pour des bornes connues qui descendent).</param>
/// <param name="IsExclusive">Vrai pour <c>..&lt;</c>.</param>
internal sealed record IrRange(SourceSpan Span, ArrayType Array, IrExpression Start, IrExpression End, IrExpression Step, bool IsExclusive) : IrExpression(Span, Array);
