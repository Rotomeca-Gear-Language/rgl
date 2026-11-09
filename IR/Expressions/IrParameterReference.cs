using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>La valeur d'un paramètre, reliée à sa déclaration.</summary>
/// <param name="Span">Le nom dans le source.</param>
/// <param name="Parameter">Le paramètre désigné.</param>
internal sealed record IrParameterReference(SourceSpan Span, ParameterSymbol Parameter) : IrExpression(Span, Parameter.Type);
