namespace Rgl.IR.Types;

/// <summary>Les types primitifs de RGL.</summary>
internal enum PrimitiveKind
{
    /// <summary><c>byte</c> : entier signé sur 8 bits.</summary>
    Byte,

    /// <summary><c>ubyte</c> : entier non signé sur 8 bits.</summary>
    UByte,

    /// <summary><c>short</c> : entier signé sur 16 bits.</summary>
    Short,

    /// <summary><c>ushort</c> : entier non signé sur 16 bits.</summary>
    UShort,

    /// <summary><c>int</c> : entier signé sur 32 bits ; le type d'un littéral entier.</summary>
    Int,

    /// <summary><c>uint</c> : entier non signé sur 32 bits ; le type du code de sortie.</summary>
    UInt,

    /// <summary><c>long</c> : entier signé sur 64 bits.</summary>
    Long,

    /// <summary><c>ulong</c> : entier non signé sur 64 bits.</summary>
    ULong,

    /// <summary><c>float</c> : flottant sur 32 bits.</summary>
    Float,

    /// <summary><c>double</c> : flottant sur 64 bits ; le type d'un littéral flottant.</summary>
    Double,

    /// <summary><c>bool</c>.</summary>
    Bool,

    /// <summary><c>string</c> : chaîne immuable.</summary>
    String,

    /// <summary><c>mstring</c> : chaîne modifiable.</summary>
    MString,

    /// <summary><c>symbol</c> : symbole interné (<c>§nom</c>).</summary>
    Symbol,

    /// <summary><c>datetime</c> : un instant en UTC, en millisecondes depuis 1970-01-01T00:00:00Z, sur 64 bits signés.</summary>
    DateTime,

    /// <summary><c>regex</c> : expression régulière (<c>/motif/flags</c>), compilée par le moteur CTRE.</summary>
    Regex,

    /// <summary><c>duration</c> : durée en millisecondes, sur 64 bits signés (<c>100ms</c>).</summary>
    Duration,

    /// <summary><c>bytes</c> : suite d'octets.</summary>
    Bytes,

    /// <summary><c>FileStream</c> : un fichier ouvert (<c>var f &lt;- file "a.txt";</c>), fermé à la fin de sa portée ou par <c>close</c>.</summary>
    FileStream,

    /// <summary><c>void</c> : absence de valeur.</summary>
    Void,
    Char,
}
