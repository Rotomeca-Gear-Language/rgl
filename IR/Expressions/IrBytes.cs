using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary><c>texte as bytes</c> : les octets UTF-8 de la chaîne. Toujours possible.</summary>
/// <param name="Span">Le cast dans le source.</param>
/// <param name="Value">La chaîne.</param>
/// <param name="Type">Le type <c>bytes</c>.</param>
internal sealed record IrStringToBytes(SourceSpan Span, IrExpression Value, IrType Type) : IrExpression(Span, Type);

/// <summary>
/// <c>octets as? string</c> (<c>null</c> si les octets ne sont pas de l'UTF-8 valide) ou <c>octets as! string</c> (le guard
/// échoue avec une <c>CastError</c>).
/// </summary>
/// <param name="Span">Le cast dans le source : sa position est celle de l'échec.</param>
/// <param name="Value">Les octets : un tableau de <c>ubyte</c>.</param>
/// <param name="Type">Une <c>string?</c> pour <c>as?</c>, une <c>string</c> pour <c>as!</c>.</param>
internal sealed record IrBytesToString(SourceSpan Span, IrExpression Value, IrType Type) : IrExpression(Span, Type)
{
    /// <summary>Vrai pour <c>as!</c>, qui fait échouer le guard.</summary>
    public bool IsForced => Type is not NullableType;
}
