using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Le <c>$</c> d'un <c>@tap</c> qui s'exécute après le gear : la valeur que le gear vient de renvoyer.</summary>
/// <param name="Span">Le <c>$</c> dans le source.</param>
/// <param name="Type">Le type de retour du gear.</param>
internal sealed record IrTapValue(SourceSpan Span, IrType Type) : IrExpression(Span, Type);
