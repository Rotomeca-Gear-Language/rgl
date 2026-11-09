using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// L'accumulation d'un tableau en une seule valeur : <c>notes |+&gt; 0 with (acc, n) =&gt; acc + n</c>.
/// La valeur de départ entre dans la première étape avec le premier élément, chaque résultat avec
/// l'élément suivant ; derrière des parcours (<c>a ||&gt; f |+&gt; 0 with g</c>), tout se fait élément par élément.
/// </summary>
/// <param name="Span">De la valeur à la fin de l'étape.</param>
/// <param name="Value">Le tableau accumulé (ou le parcours qui le produit).</param>
/// <param name="Seed">La valeur de départ, écrite avant <c>with</c>.</param>
/// <param name="Target">L'étape appliquée à l'accumulateur et à chaque élément.</param>
/// <param name="OperatorSpan">Le <c>|+&gt;</c>.</param>
internal sealed record FoldExpression(SourceSpan Span, IExpression Value, IExpression Seed, EachTarget Target, SourceSpan OperatorSpan) : IExpression;
