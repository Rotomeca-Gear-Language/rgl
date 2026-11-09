using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>La valeur d'une variable, reliée à sa déclaration.</summary>
/// <param name="Span">Le nom dans le source.</param>
/// <param name="Variable">La variable désignée.</param>
internal sealed record IrVariableReference(SourceSpan Span, VariableSymbol Variable) : IrExpression(Span, Variable.Type);

/// <summary>
/// La valeur d'une variable, déplacée au lieu d'être copiée (<c>var b:: a</c>, <c>b &lt;-| a</c>) : la
/// variable est libérée, comme par <c>undefine</c>, et n'est plus lue ensuite.
/// </summary>
/// <param name="Span">Le nom dans le source.</param>
/// <param name="Source">La variable déplacée.</param>
internal sealed record IrMove(SourceSpan Span, IrVariableReference Source) : IrExpression(Span, Source.Type);
