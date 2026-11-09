using System.Globalization;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// La niche d'un <c>T?</c> : une valeur de <c>T</c> que le programme n'utilise jamais, que <c>rgl::standard::niche_nullable</c> prend pour
/// « null ». Le <c>T?</c> a alors la taille de <c>T</c>, sans l'indicateur et le padding d'un <c>std::optional</c>. Seuls les types dont
/// les valeurs sont toutes connues du compilateur en ont une : un <c>bool</c>, un enum (une valeur qu'aucune variante n'a), un entier
/// borné (juste au-delà d'une borne).
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>La valeur écrite en C++ qui représente null pour <c>T?</c>, ou <see langword="null"/> si <paramref name="inner"/> n'a pas de niche.</summary>
    private static string? _NicheOf(IrType inner)
    {
        switch (_Representation(inner))
        {
            case PrimitiveType { Kind: PrimitiveKind.Bool }:
                return "2";

            case PrimitiveType { IsInteger: true, Bounds: { } bounds } primitive when _Range(primitive) is var (low, high):
                if (bounds.Max is long max && max < high) return _Literal(max + 1, primitive.Kind);
                if (bounds.Min is long min && min > low) return _Literal(min - 1, primitive.Kind);
                return null;

            case EnumType enumeration:
                return _EnumNiche(enumeration.Symbol);

            default:
                return null;
        }
    }

    /// <summary>La valeur d'un enum qu'aucune variante n'a, dans son type sous-jacent C++ : la plus grande, sinon la plus petite.</summary>
    private static string? _EnumNiche(EnumSymbol symbol)
    {
        // Les mêmes règles que l'écriture de l'enum : les valeurs écrites, ou l'index des variantes.
        var byIndex = !(symbol.Underlying.IsInteger || symbol.Underlying == PrimitiveType.Bool) || !symbol.HasValues;
        var underlying = byIndex && symbol.HasValues ? _IndexType(symbol.Variants.Count) : symbol.Underlying;
        if (underlying == PrimitiveType.Bool) return "2";
        if (underlying is not { IsInteger: true } primitive) return null;

        var used = symbol.Variants.Select(v => byIndex ? (long)v.Index : v.Value is long value ? value : long.MinValue).ToHashSet();
        var (low, high) = _Range(primitive);
        if (!used.Contains(high)) return _Literal(high, primitive.Kind);
        if (!used.Contains(low)) return _Literal(low, primitive.Kind);
        return null;
    }

    /// <summary>Les valeurs extrêmes d'un type entier (pour <c>ulong</c>, jusqu'à celle d'un <c>long</c> : les bornes de RGL tiennent dans un <c>long</c>).</summary>
    private static (long Low, long High) _Range(PrimitiveType type) => type.Kind switch
    {
        PrimitiveKind.Byte => (sbyte.MinValue, sbyte.MaxValue),
        PrimitiveKind.UByte => (0, byte.MaxValue),
        PrimitiveKind.Short => (short.MinValue, short.MaxValue),
        PrimitiveKind.UShort => (0, ushort.MaxValue),
        PrimitiveKind.Int => (int.MinValue, int.MaxValue),
        PrimitiveKind.UInt => (0, uint.MaxValue),
        PrimitiveKind.Long => (long.MinValue, long.MaxValue),
        _ => (0, long.MaxValue),
    };

    /// <summary>Un entier écrit comme littéral C++ du type voulu.</summary>
    private static string _Literal(long value, PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Long => value == long.MinValue ? "(-9223372036854775807ll - 1)" : $"{value.ToString(CultureInfo.InvariantCulture)}ll",
        PrimitiveKind.ULong => $"{value.ToString(CultureInfo.InvariantCulture)}ull",
        PrimitiveKind.UInt => $"{value.ToString(CultureInfo.InvariantCulture)}u",
        PrimitiveKind.Int when value == int.MinValue => "(-2147483647 - 1)",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}
