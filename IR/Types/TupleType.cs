namespace Rgl.IR.Types;

/// <summary>
/// Un tuple <c>(int, string)</c>, comme en C# : ses éléments se lisent par position (<c>Item1</c>,
/// <c>Item2</c>…) ou par leur nom (<c>(int x, int y)</c>). Les noms n'entrent pas dans l'identité du
/// type : <c>(int x, int y)</c> et <c>(int, int)</c> sont le même type.
/// </summary>
/// <param name="Elements">Les types des éléments, au moins deux.</param>
/// <param name="Names">Le nom de chaque élément, ou <see langword="null"/> ; autant que d'éléments.</param>
internal sealed record TupleType(IReadOnlyList<IrType> Elements, IReadOnlyList<string?> Names) : IrType
{
    /// <summary>La position (à partir de 0) de l'élément <paramref name="member"/> : <c>Item1</c> ou son nom ; -1 sinon.</summary>
    public int IndexOf(string member)
    {
        for (var i = 0; i < Elements.Count; ++i)
        {
            if (Names[i] == member || member == $"Item{i + 1}") return i;
        }
        return -1;
    }

    /// <inheritdoc/>
    public bool Equals(TupleType? other) => other is not null && Elements.SequenceEqual(other.Elements);

    /// <inheritdoc/>
    public override int GetHashCode() => Elements.Aggregate(19, (hash, element) => HashCode.Combine(hash, element));

    /// <inheritdoc/>
    public override string ToString() =>
        "(" + string.Join(", ", Elements.Select((type, i) => Names[i] is { } name ? $"{type} {name}" : type.ToString())) + ")";
}
