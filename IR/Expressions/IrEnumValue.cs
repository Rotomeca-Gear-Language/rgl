using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary><c>Direction.North</c>, ou <c>North</c> dans un <c>case</c> : une variante d'enum.</summary>
/// <param name="Span">La variante dans le source.</param>
/// <param name="Enum">Le type de l'enum.</param>
/// <param name="Index">La position de la variante.</param>
internal sealed record IrEnumValue(SourceSpan Span, EnumType Enum, int Index) : IrExpression(Span, Enum);
