namespace Rgl.IR.Types;

/// <summary>
/// Un type primitif : <c>int</c>, <c>uint</c>, <c>string</c>… Un type numérique peut être borné
/// (<c>int&lt;0..100&gt;</c>) : même représentation, mais un autre type, dont chaque valeur entrante est
/// contrôlée.
/// </summary>
/// <param name="Kind">Le primitif.</param>
/// <param name="Bounds">Ses bornes ; <see langword="null"/> pour un type non borné.</param>
internal sealed record PrimitiveType(PrimitiveKind Kind, NumericBounds? Bounds = null) : IrType
{
    public static readonly PrimitiveType Char = new(PrimitiveKind.Char);
    public static readonly PrimitiveType Int = new(PrimitiveKind.Int);
    public static readonly PrimitiveType UInt = new(PrimitiveKind.UInt);
    public static readonly PrimitiveType Long = new(PrimitiveKind.Long);
    public static readonly PrimitiveType Double = new(PrimitiveKind.Double);
    public static readonly PrimitiveType Bool = new(PrimitiveKind.Bool);
    public static readonly PrimitiveType String = new(PrimitiveKind.String);
    public static readonly PrimitiveType MString = new(PrimitiveKind.MString);
    public static readonly PrimitiveType Symbol = new(PrimitiveKind.Symbol);
    public static readonly PrimitiveType DateTime = new(PrimitiveKind.DateTime);
    public static readonly PrimitiveType Regex = new(PrimitiveKind.Regex);
    public static readonly PrimitiveType Duration = new(PrimitiveKind.Duration);
    public static readonly PrimitiveType Void = new(PrimitiveKind.Void);
    public static readonly PrimitiveType FileStream = new(PrimitiveKind.FileStream);

    /// <summary>Vrai pour un type entier, signé ou non.</summary>
    public bool IsInteger => Kind is PrimitiveKind.Byte or PrimitiveKind.UByte or PrimitiveKind.Short or PrimitiveKind.UShort
        or PrimitiveKind.Int or PrimitiveKind.UInt or PrimitiveKind.Long or PrimitiveKind.ULong;

    /// <summary>Vrai pour un type numérique : entier ou flottant.</summary>
    public bool IsNumeric => IsInteger || Kind is PrimitiveKind.Float or PrimitiveKind.Double;

    /// <summary>Le même type, sans ses bornes.</summary>
    public PrimitiveType Unbounded => Bounds is null ? this : new PrimitiveType(Kind);

    /// <inheritdoc/>
    public override string ToString() => Bounds is null ? _Name : $"{_Name}<{Bounds}>";

    private string _Name => Kind switch
    {
        PrimitiveKind.UByte => "ubyte",
        PrimitiveKind.UShort => "ushort",
        PrimitiveKind.UInt => "uint",
        PrimitiveKind.ULong => "ulong",
        PrimitiveKind.MString => "mstring",
        PrimitiveKind.FileStream => "FileStream",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}
