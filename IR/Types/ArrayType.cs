namespace Rgl.IR.Types;

/// <summary>Les sortes de tableaux.</summary>
internal enum ArrayKind
{
    /// <summary><c>[T]</c> : dynamique, de taille quelconque.</summary>
    Dynamic,

    /// <summary><c>[T;N]</c> : exactement N éléments.</summary>
    Fixed,

    /// <summary><c>[T;N..M]</c> : de N à M éléments.</summary>
    Bounded,

    /// <summary><c>[T+]</c> : au moins un élément.</summary>
    NonEmpty,
}

/// <summary>
/// Un tableau : <c>[T]</c>, <c>[T;N]</c>, <c>[T;N..M]</c> ou <c>[T+]</c>. Ses éléments se lisent mais
/// ne se modifient pas ; il grandit seulement par la fin.
/// </summary>
/// <param name="Element">Le type des éléments.</param>
/// <param name="Kind">La sorte de tableau.</param>
/// <param name="Size">N : la taille d'un <c>[T;N]</c>, le minimum d'un <c>[T;N..M]</c> ; 0 sinon.</param>
/// <param name="Max">M : le maximum d'un <c>[T;N..M]</c> ; 0 sinon.</param>
internal sealed record ArrayType(IrType Element, ArrayKind Kind = ArrayKind.Dynamic, uint Size = 0, uint Max = 0, bool Packed = false) : IrType
{
    /// <summary>Le nombre minimal d'éléments.</summary>
    public uint Minimum => Kind switch
    {
        ArrayKind.Fixed or ArrayKind.Bounded => Size,
        ArrayKind.NonEmpty => 1,
        _ => 0,
    };

    /// <summary>Le nombre maximal d'éléments ; <see langword="null"/> sans maximum.</summary>
    public uint? Maximum => Kind switch
    {
        ArrayKind.Fixed => Size,
        ArrayKind.Bounded => Max,
        _ => null,
    };

    /// <summary>Indique si un tableau de <paramref name="count"/> éléments en est un.</summary>
    public bool Fits(ulong count) => count >= Minimum && (Maximum is not { } max || count <= max);

    /// <summary>Indique si tout tableau de la sorte <paramref name="other"/> (mêmes éléments) en est un.</summary>
    public bool Contains(ArrayType other) => other.Minimum >= Minimum && (Maximum is not { } max || other.Maximum is { } otherMax && otherMax <= max);

    /// <summary>Le tableau dynamique des mêmes éléments.</summary>
    public ArrayType Dynamic => Kind == ArrayKind.Dynamic ? this : new ArrayType(Element, Packed: Packed);

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        ArrayKind.Fixed => $"[{Element};{Size}]",
        ArrayKind.Bounded => $"[{Element};{Size}..{Max}]",
        ArrayKind.NonEmpty => $"[{Element}+]",
        _ => $"[{Element}]",
    };
}
