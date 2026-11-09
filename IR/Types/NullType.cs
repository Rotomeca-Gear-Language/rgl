namespace Rgl.IR.Types;

/// <summary>Le type du littéral <c>null</c>, qui n'est assignable qu'à un type nullable.</summary>
internal sealed record NullType : IrType
{
    /// <summary>L'unique instance.</summary>
    public static readonly NullType Instance = new();

    private NullType() { }

    /// <inheritdoc/>
    public override string ToString() => "null";
}
