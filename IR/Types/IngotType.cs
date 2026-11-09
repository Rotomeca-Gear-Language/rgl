using Rgl.IR.Symbols;

namespace Rgl.IR.Types;

/// <summary>
/// Le type d'une valeur d'ingot : la valeur de sa base, sous une autre identité. Il ne se mélange
/// ni avec sa base ni avec un autre ingot ; <c>°</c> lui retire sa marque.
/// </summary>
/// <param name="Symbol">L'ingot.</param>
internal sealed record IngotType(IngotSymbol Symbol) : IrType
{
    /// <summary>Le type de base.</summary>
    public IrType Base => Symbol.Base;

    /// <summary>Le type de base, sous toutes les marques : <c>int</c> pour un ingot posé sur un autre ingot de <c>int</c>.</summary>
    public IrType Underlying => Base is IngotType inner ? inner.Underlying : Base;

    /// <inheritdoc/>
    public override string ToString() => Symbol.Name;
}
