using Rgl.IR.Symbols;

namespace Rgl.IR.Types;

/// <summary>Le type d'une valeur d'enum.</summary>
/// <param name="Symbol">L'enum.</param>
internal sealed record EnumType(EnumSymbol Symbol) : IrType
{
    /// <inheritdoc/>
    public override string ToString() => Symbol.Name;
}
