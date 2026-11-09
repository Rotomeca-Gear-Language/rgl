using System.Numerics;
using Rgl.IR.Expressions;
using Rgl.IR.Types;

namespace Rgl.IR;

/// <summary>
/// Calcule la valeur d'une expression entièrement connue à la compilation. Une opération qui
/// déborderait ou diviserait par zéro n'est pas calculée : l'expression est alors traitée comme
/// connue seulement à l'exécution. Sert au binder (une constante calculable devient
/// <c>constexpr</c>, une constante qui tient dans le type attendu s'y adapte) et à l'émetteur
/// (une opération calculable ne peut pas déborder : inutile de la vérifier).
/// </summary>
internal static class ConstantFolder
{
    /// <summary>La valeur de <paramref name="expression"/>, ou <see langword="null"/> si elle n'est pas connue à la compilation.</summary>
    /// <returns>Un <see cref="long"/>, un <see cref="double"/>, un <see cref="bool"/>, un <see cref="string"/>, un <see cref="SymbolValue"/> ou un <see cref="MoldValue"/>.</returns>
    public static object? Fold(IrExpression expression) => expression switch
    {
        IrIntegerConstant constant => _InRange(constant.Value, constant.Type),
        IrFloatConstant constant => constant.Value,
        IrBoolConstant constant => constant.Value,
        IrStringConstant { Type: PrimitiveType { Kind: PrimitiveKind.String } } constant => constant.Value,
        IrSymbolConstant constant => new SymbolValue(constant.Name),
        IrDurationConstant constant => new DurationValue(constant.Milliseconds),
        IrNullConstant => NullValue.Instance,
        IrEnumValue value => new EnumValue(value.Enum.Symbol, value.Index),
        IrConversion { Type: NullableType } conversion => Fold(conversion.Operand),
        IrNonNull nonNull => Fold(nonNull.Value),
        IrCoalesce coalesce => Fold(coalesce.Left) switch
        {
            null => null,
            NullValue => Fold(coalesce.Right),
            var value => value,
        },
        IrVariableReference reference => reference.Variable.CompileTimeValue,
        IrConversion conversion => (Fold(conversion.Operand), conversion.Type) switch
        {
            (long value, PrimitiveType { IsInteger: true }) => _InRange(value, conversion.Type),
            (long value, PrimitiveType { IsNumeric: true }) => (double)value,
            (double value, PrimitiveType { Kind: PrimitiveKind.Float or PrimitiveKind.Double }) => value,
            _ => null,
        },
        IrRetype retype => Fold(retype.Value),
        IrBoundCheck check => Fold(check.Value) is { } value && check.Target.Bounds!.Contains(value) ? value : null,
        IrNumericCast cast => Cast(Fold(cast.Value), cast.Target),
        IrDurationMilliseconds milliseconds => Fold(milliseconds.Value) is DurationValue duration ? duration.Milliseconds : null,
        IrDurationOf duration => Fold(duration.Value) is long count ? new DurationValue(count) : null,
        IrUnary unary => _Unary(unary),
        IrConditional conditional => Fold(conditional.Condition) is bool condition
            ? Fold(condition ? conditional.WhenTrue : conditional.WhenFalse)
            : null,
        IrBinary binary => _Binary(binary),
        IrMoldValue { Parts.Count: 0 } mold => _Mold(mold),
        IrArrayValue { Array.Kind: Types.ArrayKind.Fixed, HasKnownCount: true } array => _Items(array.Parts.Select(p => p.Value)) is { } items ? new ArrayValue(items) : null,
        IrTupleValue tuple => _Items(tuple.Elements) is { } elements ? new TupleValue(elements) : null,
        IrIndex { Target.Type: Types.ArrayType } index => Fold(index.Target) is ArrayValue array && Fold(index.Index) is long position
            && (position < 0 ? position + array.Items.Count : position) is var rank && rank >= 0 && rank < array.Items.Count ? array.Items[(int)rank] : null,
        IrTupleItem item => Fold(item.Target) is TupleValue tuple ? tuple.Items[item.Position] : null,
        IrLength { Target.Type: Types.ArrayType { Kind: Types.ArrayKind.Fixed } array } => (long)array.Size,
        IrMemberAccess { Target.Type: MoldType type } access => Fold(access.Target) is MoldValue value
            ? value.Fields[_IndexOf(type, access.Field)]
            : null,
        _ => null,
    };

    /// <summary>
    /// Une valeur numérique connue convertie en <paramref name="target"/> : un flottant vers un entier
    /// est tronqué vers zéro ; <see langword="null"/> si le résultat ne tient pas dans le type.
    /// </summary>
    public static object? Cast(object? value, PrimitiveType target) => (value, target) switch
    {
        (long integer, { IsInteger: true }) => _InRange(integer, target),
        (long integer, { Kind: PrimitiveKind.Float }) => (double)(float)integer,
        (long integer, { Kind: PrimitiveKind.Double }) => (double)integer,
        (double real, { IsInteger: true }) when double.IsFinite(real) => _InRange(new BigInteger(Math.Truncate(real)), target),
        (double real, { Kind: PrimitiveKind.Float }) => float.IsFinite((float)real) ? (double)(float)real : null,
        (double real, { Kind: PrimitiveKind.Double }) => real,
        _ => null,
    };

    /// <summary>Les valeurs de toutes ces expressions, si elles sont toutes connues à la compilation.</summary>
    private static object[]? _Items(IEnumerable<IrExpression> expressions)
    {
        var items = new List<object>();
        foreach (var expression in expressions)
        {
            if (Fold(expression) is not { } value) return null;
            items.Add(value);
        }
        return [.. items];
    }

    /// <summary>Un mold dont tous les champs sont connus à la compilation.</summary>
    private static MoldValue? _Mold(IrMoldValue mold)
    {
        var fields = new object[mold.Fields.Count];
        for (var i = 0; i < fields.Length; ++i)
        {
            if (Fold(mold.Fields[i]) is not { } value) return null;
            fields[i] = value;
        }
        return new MoldValue(fields);
    }

    private static int _IndexOf(MoldType type, Symbols.MoldFieldSymbol field)
    {
        for (var i = 0; i < type.Symbol.Fields.Count; ++i)
        {
            if (type.Symbol.Fields[i].Name == field.Name) return i;
        }
        throw new InvalidOperationException($"Champ « {field.Name} » absent de « {type} ».");
    }

    private static object? _Unary(IrUnary unary) => (unary.Operator, Fold(unary.Operand)) switch
    {
        (IrUnaryOperator.Negate, long value) => _InRange(-(BigInteger)value, unary.Type),
        (IrUnaryOperator.Negate, double value) => -value,
        (IrUnaryOperator.Not, bool value) => !value,
        // ~x vaut -x - 1 pour un signé, max - x pour un non signé.
        (IrUnaryOperator.BitwiseNot, long value) => unary.Type is PrimitiveType { Kind: PrimitiveKind.UByte or PrimitiveKind.UShort or PrimitiveKind.UInt or PrimitiveKind.ULong } unsigned
            ? _InRange(_Max(unsigned.Kind) - value, unary.Type)
            : _InRange(-(BigInteger)value - 1, unary.Type),
        _ => null,
    };

    private static object? _Binary(IrBinary binary)
    {
        var left = Fold(binary.Left);
        var right = Fold(binary.Right);

        return (left, right) switch
        {
            (long l, long r) => _Integer(binary.Operator, l, r, binary.Type),
            (double l, double r) => _Float(binary.Operator, l, r),
            (bool l, bool r) => binary.Operator switch
            {
                IrBinaryOperator.And => l && r,
                IrBinaryOperator.Or => l || r,
                IrBinaryOperator.Xor => l != r,
                IrBinaryOperator.Equal => l == r,
                _ => null,
            },
            (string l, string r) when binary.Operator == IrBinaryOperator.Equal => l == r,
            (SymbolValue l, SymbolValue r) when binary.Operator == IrBinaryOperator.Equal => l == r,
            (DurationValue l, DurationValue r) => _Duration(binary.Operator, l.Milliseconds, r.Milliseconds),
            (MoldValue l, MoldValue r) when binary.Operator == IrBinaryOperator.Equal => l.Equals(r),
            (NullValue, NullValue) when binary.Operator == IrBinaryOperator.Equal => true,
            (EnumValue l, EnumValue r) when binary.Operator == IrBinaryOperator.Equal => l == r,
            (ArrayValue l, ArrayValue r) when binary.Operator == IrBinaryOperator.Equal => l.Equals(r),
            (TupleValue l, TupleValue r) when binary.Operator == IrBinaryOperator.Equal => l.Equals(r),
            (NullValue, not null) or (not null, NullValue) when binary.Operator == IrBinaryOperator.Equal => false,
            _ => null,
        };
    }

    /// <summary>Opération entre deux durées connues ; <see langword="null"/> si elle déborderait.</summary>
    private static object? _Duration(IrBinaryOperator op, long l, long r) => op switch
    {
        IrBinaryOperator.Add => _InRange((BigInteger)l + r, PrimitiveType.Long) is long sum ? new DurationValue(sum) : null,
        IrBinaryOperator.Subtract => _InRange((BigInteger)l - r, PrimitiveType.Long) is long difference ? new DurationValue(difference) : null,
        IrBinaryOperator.Divide => r == 0 || (l == long.MinValue && r == -1) ? null : l / r,
        IrBinaryOperator.Less => l < r,
        IrBinaryOperator.Greater => l > r,
        IrBinaryOperator.LessOrEqual => l <= r,
        IrBinaryOperator.GreaterOrEqual => l >= r,
        IrBinaryOperator.Equal => l == r,
        _ => null,
    };

    private static object? _Integer(IrBinaryOperator op, BigInteger l, BigInteger r, IrType type) => op switch
    {
        IrBinaryOperator.Add => _InRange(l + r, type),
        IrBinaryOperator.Subtract => _InRange(l - r, type),
        IrBinaryOperator.Multiply => _InRange(l * r, type),
        // Division et reste tronqués vers zéro, comme en C++ ; par zéro : indéfini.
        IrBinaryOperator.Divide => r.IsZero ? null : _InRange(BigInteger.Divide(l, r), type),
        IrBinaryOperator.Remainder => r.IsZero ? null : _InRange(BigInteger.Remainder(l, r), type),
        IrBinaryOperator.Less => l < r,
        IrBinaryOperator.Greater => l > r,
        IrBinaryOperator.LessOrEqual => l <= r,
        IrBinaryOperator.GreaterOrEqual => l >= r,
        IrBinaryOperator.Equal => l == r,
        IrBinaryOperator.BitAnd => _InRange(l & r, type),
        IrBinaryOperator.BitOr => _InRange(l | r, type),
        IrBinaryOperator.BitXor => _InRange(l ^ r, type),
        // Un décalage négatif ou d'au moins la largeur du type est indéfini.
        IrBinaryOperator.ShiftLeft when r >= 0 && r < _Bits(type) && l >= 0 => _InRange(l << (int)r, type),
        IrBinaryOperator.ShiftRight when r >= 0 && r < _Bits(type) && l >= 0 => _InRange(l >> (int)r, type),
        _ => null,
    };

    private static object? _Float(IrBinaryOperator op, double l, double r)
    {
        double? result = op switch
        {
            IrBinaryOperator.Add => l + r,
            IrBinaryOperator.Subtract => l - r,
            IrBinaryOperator.Multiply => l * r,
            IrBinaryOperator.Divide when r != 0 => l / r,
            _ => null,
        };
        if (result is { } value) return double.IsFinite(value) ? value : null;

        return op switch
        {
            IrBinaryOperator.Less => l < r,
            IrBinaryOperator.Greater => l > r,
            IrBinaryOperator.LessOrEqual => l <= r,
            IrBinaryOperator.GreaterOrEqual => l >= r,
            IrBinaryOperator.Equal => l == r,
            _ => null,
        };
    }

    /// <summary>La valeur d'un symbole connue à la compilation : son nom, qui suffit à son identité.</summary>
    /// <param name="Name">Le nom, sans <c>§</c>.</param>
    public sealed record SymbolValue(string Name);

    /// <summary>La valeur d'une durée connue à la compilation.</summary>
    /// <param name="Milliseconds">Ses millisecondes.</param>
    public sealed record DurationValue(long Milliseconds);

    /// <summary>Une variante d'enum connue à la compilation : l'enum et la position de la variante.</summary>
    /// <param name="Enum">L'enum.</param>
    /// <param name="Index">La position de la variante.</param>
    public sealed record EnumValue(Symbols.EnumSymbol Enum, int Index);

    /// <summary>La valeur <c>null</c> connue à la compilation.</summary>
    public sealed record NullValue
    {
        /// <summary>L'unique instance.</summary>
        public static readonly NullValue Instance = new();

        private NullValue() { }
    }

    /// <summary>Un tableau <c>[T;N]</c> connu à la compilation : ses éléments, dans l'ordre. Égal à un autre élément par élément.</summary>
    /// <param name="Items">Les valeurs des éléments.</param>
    public sealed record ArrayValue(IReadOnlyList<object> Items)
    {
        /// <inheritdoc/>
        public bool Equals(ArrayValue? other) => other is not null && Items.SequenceEqual(other.Items);

        /// <inheritdoc/>
        public override int GetHashCode() => Items.Aggregate(3, (hash, item) => HashCode.Combine(hash, item));
    }

    /// <summary>Un tuple connu à la compilation : ses éléments, dans l'ordre. Égal à un autre élément par élément.</summary>
    /// <param name="Items">Les valeurs des éléments.</param>
    public sealed record TupleValue(IReadOnlyList<object> Items)
    {
        /// <inheritdoc/>
        public bool Equals(TupleValue? other) => other is not null && Items.SequenceEqual(other.Items);

        /// <inheritdoc/>
        public override int GetHashCode() => Items.Aggregate(5, (hash, item) => HashCode.Combine(hash, item));
    }

    /// <summary>La valeur d'un mold connue à la compilation : celle de chaque champ, dans l'ordre du mold. Égale à une autre champ par champ.</summary>
    /// <param name="Fields">Les valeurs des champs.</param>
    public sealed record MoldValue(IReadOnlyList<object> Fields)
    {
        /// <inheritdoc/>
        public bool Equals(MoldValue? other) => other is not null && Fields.SequenceEqual(other.Fields);

        /// <inheritdoc/>
        public override int GetHashCode() => Fields.Aggregate(0, (hash, field) => HashCode.Combine(hash, field));
    }

    /// <summary>
    /// Indique si une valeur connue à la compilation tient dans <paramref name="target"/> : un entier
    /// dans les bornes d'un type entier, ou exactement représentable par un flottant ; un flottant
    /// dans l'étendue d'un type flottant (arrondi accepté, comme pour un littéral).
    /// </summary>
    public static bool Fits(object value, PrimitiveType target) => (target.Bounds?.Contains(value) ?? true) && (value, target.Kind) switch
    {
        (long integer, _) when target.IsInteger => _InRange(integer, target) is not null,
        (long integer, PrimitiveKind.Float) => Math.Abs(integer) <= 1L << 24,
        (long integer, PrimitiveKind.Double) => Math.Abs(integer) <= 1L << 53,
        (double real, PrimitiveKind.Float) => float.IsFinite((float)real),
        (double, PrimitiveKind.Double) => true,
        _ => false,
    };

    /// <summary>La valeur si elle tient dans le type entier <paramref name="type"/> ; <see langword="null"/> sinon (débordement).</summary>
    private static object? _InRange(BigInteger value, IrType type)
    {
        if (type is not PrimitiveType { IsInteger: true } primitive) return null;

        var (min, max) = primitive.Kind switch
        {
            PrimitiveKind.Byte => ((BigInteger)sbyte.MinValue, (BigInteger)sbyte.MaxValue),
            PrimitiveKind.UByte => (BigInteger.Zero, (BigInteger)byte.MaxValue),
            PrimitiveKind.Short => (short.MinValue, short.MaxValue),
            PrimitiveKind.UShort => (BigInteger.Zero, ushort.MaxValue),
            PrimitiveKind.Int => (int.MinValue, int.MaxValue),
            PrimitiveKind.UInt => (BigInteger.Zero, uint.MaxValue),
            PrimitiveKind.Long => (long.MinValue, long.MaxValue),
            _ => (BigInteger.Zero, (BigInteger)ulong.MaxValue),
        };

        // Un ulong au-delà de long.MaxValue n'est pas représentable ici : on renonce à le calculer.
        return value >= min && value <= max && value <= long.MaxValue ? (long)value : null;
    }

    /// <summary>Plus grande valeur d'un type entier non signé.</summary>
    private static BigInteger _Max(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.UByte => byte.MaxValue,
        PrimitiveKind.UShort => ushort.MaxValue,
        PrimitiveKind.UInt => uint.MaxValue,
        _ => ulong.MaxValue,
    };

    /// <summary>Largeur en bits d'un type entier.</summary>
    private static int _Bits(IrType type) => type switch
    {
        PrimitiveType { Kind: PrimitiveKind.Byte or PrimitiveKind.UByte } => 8,
        PrimitiveType { Kind: PrimitiveKind.Short or PrimitiveKind.UShort } => 16,
        PrimitiveType { Kind: PrimitiveKind.Int or PrimitiveKind.UInt } => 32,
        _ => 64,
    };
}
