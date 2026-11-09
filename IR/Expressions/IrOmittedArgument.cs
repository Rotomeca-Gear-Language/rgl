using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// L'argument d'un paramètre par défaut que l'appel ne donne pas : le gear appelé calcule sa valeur. Il tient la
/// place du paramètre dans <see cref="IrCall.Arguments"/> et n'est jamais écrit en C++.
/// </summary>
/// <param name="Span">L'appel.</param>
/// <param name="Type">Le type du paramètre.</param>
internal sealed record IrOmittedArgument(SourceSpan Span, IrType Type) : IrExpression(Span, Type);
