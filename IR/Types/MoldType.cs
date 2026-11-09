using Rgl.IR.Symbols;

namespace Rgl.IR.Types;

/// <summary>Le type d'une valeur de mold, nommé (<c>Vec2</c>) ou anonyme (<c>{int x, int y}</c>).</summary>
/// <param name="Symbol">Le mold.</param>
internal sealed record MoldType(MoldSymbol Symbol) : IrType
{
    /// <inheritdoc/>
    public override string ToString() => Symbol.ToString();
}
