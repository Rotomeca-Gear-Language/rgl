namespace Rgl.IR.Types;

/// <summary>
/// <c>int | string</c> : une valeur de l'un des types. Les membres sont dépliés (une union dans une
/// union), sans doublon, et rangés dans un ordre fixe : <c>int | string</c> et <c>string | int</c>
/// sont le même type.
/// </summary>
internal sealed record UnionType : IrType
{
    /// <summary>Les membres, dans leur ordre fixe (celui des alternatives en C++).</summary>
    public IReadOnlyList<IrType> Members { get; }

    private UnionType(IReadOnlyList<IrType> members) => Members = members;

    /// <summary>L'union de ces types ; un seul type distinct donne ce type lui-même.</summary>
    public static IrType Of(IEnumerable<IrType> members)
    {
        var flat = members.SelectMany(m => m is UnionType union ? union.Members : [m])
            .Distinct()
            .OrderBy(m => m.ToString(), StringComparer.Ordinal)
            .ToArray();
        return flat.Length == 1 ? flat[0] : new UnionType(flat);
    }

    /// <summary>La position de <paramref name="member"/> dans l'union, ou -1.</summary>
    public int IndexOf(IrType member)
    {
        for (var i = 0; i < Members.Count; ++i)
        {
            if (Members[i] == member) return i;
        }
        return -1;
    }

    /// <inheritdoc/>
    public bool Equals(UnionType? other) => other is not null && Members.SequenceEqual(other.Members);

    /// <inheritdoc/>
    public override int GetHashCode() => Members.Aggregate(17, (hash, member) => HashCode.Combine(hash, member));

    /// <inheritdoc/>
    public override string ToString() => string.Join(" | ", Members);
}
