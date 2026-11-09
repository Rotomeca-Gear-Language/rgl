using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Le parcours des lignes d'un <c>soa mold</c> : <c>particles |S|&gt; damp</c> appelle le gear avec, pour
/// chaque rang, la valeur de chaque colonne dans le paramètre du même nom.
/// </summary>
/// <param name="Span">De la valeur à la fin de l'étape.</param>
/// <param name="Value">Le <c>soa mold</c> parcouru.</param>
/// <param name="IsShortest">Vrai pour <c>||S|&gt;</c> (s'arrête à la colonne la plus courte), faux pour <c>|S|&gt;</c> (les colonnes ont la même taille).</param>
/// <param name="Target">Le gear appelé à chaque ligne.</param>
/// <param name="OperatorSpan">Le <c>|S|&gt;</c> ou le <c>||S|&gt;</c>.</param>
internal sealed record SoaEachExpression(SourceSpan Span, IExpression Value, bool IsShortest, EachTarget Target, SourceSpan OperatorSpan) : IExpression;
