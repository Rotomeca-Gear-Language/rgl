using Rgl.IR.Symbols;

namespace Rgl.IR.Types;

/// <summary>
/// Les types à cycle de vie : un mold qui a un gear de fin (<c>mold Handle -&gt; fermer</c>), ou qui en
/// contient un dans un champ. Une valeur de ce type n'a qu'un propriétaire : elle ne se copie jamais, elle
/// se déplace, et son gear de fin s'appelle une fois, quand son dernier propriétaire la perd.
/// </summary>
internal static class LifecycleRules
{
    /// <summary>
    /// Vrai si <paramref name="type"/> est lui-même à cycle de vie : un mold qui a un gear de fin ou un champ
    /// qui en a un, ou un ingot de l'un d'eux. Un tableau, un nullable… qui en contient un ne l'est pas : ils
    /// ne sont pas pris en charge (voir <see cref="HoldsInside"/>).
    /// </summary>
    public static bool Has(IrType type) => _Has(type, []);

    private static bool _Has(IrType type, HashSet<MoldSymbol> seen) => type switch
    {
        PointerType { Owned: true } => true,
        IngotType ingot => _Has(ingot.Base, seen),
        MoldType mold => _HasMold(mold.Symbol, seen),
        _ => false,
    };

    /// <summary>Vrai si le mold a un gear de fin, ou un champ de type à cycle de vie.</summary>
    public static bool HasMold(MoldSymbol mold) => _HasMold(mold, []);

    private static bool _HasMold(MoldSymbol mold, HashSet<MoldSymbol> seen) =>
        mold.DeclaresDestructor || (seen.Add(mold) && mold.Fields.Any(f => _Has(f.Type, seen)));

    /// <summary>
    /// Vrai si <paramref name="type"/> est un tableau, un dictionnaire, un tuple, un nullable, une union, un résultat,
    /// une promesse ou un type de fonction dont l'un des types directs est à cycle de vie : ces compositions
    /// copieraient la valeur, et ne sont pas encore prises en charge.
    /// </summary>
    public static bool HoldsInside(IrType type) => type switch
    {
        NullableType nullable => Has(nullable.Inner),
        ArrayType array => Has(array.Element),
        DictionaryType dictionary => Has(dictionary.Key) || Has(dictionary.Value),
        TupleType tuple => tuple.Elements.Any(Has),
        UnionType union => union.Members.Any(Has),
        ResultType result => Has(result.Value) || Has(result.Error),
        PromiseType promise => Has(promise.Value),
        FunctionType function => function.Parameters.Any(Has) || Has(function.Return),
        _ => false,
    };
}
