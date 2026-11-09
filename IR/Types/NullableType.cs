namespace Rgl.IR.Types;

/// <summary><c>T?</c> : une valeur de <c>T</c>, ou <c>null</c>. Jamais le nullable d'un nullable.</summary>
/// <param name="Inner">Le type rendu nullable.</param>
internal sealed record NullableType(IrType Inner) : IrType
{
    /// <inheritdoc/>
    public override string ToString() => $"{Inner}?";
}
