namespace Rgl.IR.Types;

/// <summary>
/// Le type d'une lambda : <c>(int, string) =&gt; bool</c>. Une lambda <c>pure</c> n'a aucun effet de
/// bord : elle entre aussi dans le type sans <c>pure</c>, pas l'inverse. Un gear passé par son nom
/// a le type de sa signature.
/// </summary>
/// <param name="Parameters">Les types des paramètres, dans l'ordre.</param>
/// <param name="Return">Le type de retour ; <c>void</c> si elle ne renvoie rien.</param>
/// <param name="IsPure">Vrai pour <c>pure (…) =&gt; T</c>.</param>
internal sealed record FunctionType(IReadOnlyList<IrType> Parameters, IrType Return, bool IsPure) : IrType
{
    /// <summary>Le même type, sans <c>pure</c>.</summary>
    public FunctionType Impure => this with { IsPure = false };

    /// <summary>
    /// Indique si une valeur de type <paramref name="source"/> entre dans ce type : mêmes paramètres
    /// et même retour, et pure si ce type l'exige.
    /// </summary>
    public bool Accepts(FunctionType source) => source.Impure == Impure && (source.IsPure || !IsPure);

    /// <summary>
    /// Indique si une valeur de ce type contient une lambda, même au fond d'un mold, d'un tableau ou
    /// d'une union : elle ne se compare alors pas (deux lambdas n'ont pas d'égalité).
    /// </summary>
    public static bool IsInside(IrType type) => _IsInside(type, []);

    private static bool _IsInside(IrType type, HashSet<Symbols.MoldSymbol> seen) => type switch
    {
        FunctionType => true,
        NullableType nullable => _IsInside(nullable.Inner, seen),
        IngotType ingot => _IsInside(ingot.Base, seen),
        ArrayType array => _IsInside(array.Element, seen),
        DictionaryType dictionary => _IsInside(dictionary.Key, seen) || _IsInside(dictionary.Value, seen),
        TupleType tuple => tuple.Elements.Any(e => _IsInside(e, seen)),
        UnionType union => union.Members.Any(m => _IsInside(m, seen)),
        ResultType result => _IsInside(result.Value, seen) || _IsInside(result.Error, seen),
        MoldType mold => seen.Add(mold.Symbol) && mold.Symbol.Fields.Any(f => _IsInside(f.Type, seen)),
        _ => false,
    };

    /// <inheritdoc/>
    public bool Equals(FunctionType? other) =>
        other is not null && IsPure == other.IsPure && Return == other.Return && Parameters.SequenceEqual(other.Parameters);

    /// <inheritdoc/>
    public override int GetHashCode() => Parameters.Aggregate(HashCode.Combine(23, Return, IsPure), (hash, parameter) => HashCode.Combine(hash, parameter));

    /// <inheritdoc/>
    public override string ToString() => (IsPure ? "pure " : "") + "(" + string.Join(", ", Parameters) + ") => " + Return;
}
