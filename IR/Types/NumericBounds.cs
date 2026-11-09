using System.Globalization;

namespace Rgl.IR.Types;

/// <summary>
/// Les bornes d'un type borné (<c>int&lt;0..100&gt;</c>), comprises : deux <see cref="long"/> pour un
/// type entier, deux <see cref="double"/> pour un flottant.
/// </summary>
/// <param name="Min">La borne basse.</param>
/// <param name="Max">La borne haute.</param>
internal sealed record NumericBounds(object Min, object Max)
{
    /// <summary>Indique si une valeur connue à la compilation (<see cref="long"/> ou <see cref="double"/>) est dans les bornes.</summary>
    public bool Contains(object value) => (value, Min, Max) switch
    {
        (long v, long min, long max) => v >= min && v <= max,
        (double v, double min, double max) => v >= min && v <= max,
        (long v, double min, double max) => v >= min && v <= max,
        (double v, long min, long max) => v >= min && v <= max,
        _ => false,
    };

    /// <summary>Indique si toutes les valeurs de <paramref name="other"/> sont dans ces bornes.</summary>
    public bool Contains(NumericBounds other) => Contains(other.Min) && Contains(other.Max);

    /// <inheritdoc/>
    public override string ToString() => $"{_Text(Min)}..{_Text(Max)}";

    private static string _Text(object value) => value switch
    {
        double real when real == Math.Floor(real) && Math.Abs(real) < 1e15 => real.ToString("0.0", CultureInfo.InvariantCulture),
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
