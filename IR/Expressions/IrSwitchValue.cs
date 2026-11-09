using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>La valeur d'un switch, déjà évaluée : dans les tests de ses cas, et <c>$</c> dans un <c>case eval</c>.</summary>
/// <param name="Span">L'endroit qui la lit dans le source.</param>
/// <param name="Type">Son type, celui de la valeur du switch.</param>
/// <param name="SwitchId">Le switch.</param>
internal sealed record IrSwitchValue(SourceSpan Span, IrType Type, int SwitchId) : IrExpression(Span, Type);
