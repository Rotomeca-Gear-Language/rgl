using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// <c>texte as! int</c> : le nombre écrit dans une chaîne. Si la chaîne n'est pas entièrement un
/// nombre de ce type, le guard qui l'évalue échoue (« « abc » n'est pas un int »).
/// </summary>
/// <param name="Span">Le cast dans le source : sa position est celle de l'échec.</param>
/// <param name="Target">Le type numérique visé, celui de l'expression.</param>
/// <param name="Text">La chaîne lue.</param>
internal sealed record IrParseNumber(SourceSpan Span, PrimitiveType Target, IrExpression Text) : IrExpression(Span, Target);
