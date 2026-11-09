using Rgl.IR.Expressions;
using Rgl.IR.Train;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>
/// Le corps d'un train appelable (d'un fichier <c>forge: train</c>), seule instruction du gear qui le porte : sa
/// valeur de départ et son chemin principal. Contrairement au train d'entrée, ses terminaisons renvoient une
/// valeur (<c>done</c>, <c>$ v</c>, un emit) ou une erreur (<c>fail</c>, un guard sans rail).
/// </summary>
/// <param name="Span">Le train dans le source.</param>
/// <param name="StartValue">La valeur qui entre dans le pipeline ; <see langword="null"/> s'il part de <c>null</c>.</param>
/// <param name="Main">Le chemin principal.</param>
internal sealed record IrTrainBody(SourceSpan Span, IrExpression? StartValue, IrPath Main) : IrStatement(Span);
