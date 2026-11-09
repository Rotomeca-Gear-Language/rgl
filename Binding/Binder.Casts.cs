using System.Globalization;
using Rgl.AST.Expressions;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Types;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Les casts et les types bornés. Entre deux nombres, <c>as</c> contrôle la valeur : connue, elle
/// doit tenir dans le type visé (E086) ; inconnue, elle est contrôlée à l'exécution — un flottant
/// vers un entier toujours (tronqué vers zéro, NaN et hors plage échouent), un entier vers un entier
/// plus petit selon la clé <c>overflow</c> (en <c>unchecked</c>, il perd ses bits de poids fort).
/// <c>as?</c> donne <c>null</c> et <c>as!</c> fait échouer le guard quand la valeur ne tient pas.
/// Un texte se lit en nombre par <c>as?</c> et <c>as!</c> seulement. Ailleurs, <c>as</c> écrit une
/// conversion implicite (duck typing, union, nullable). Une valeur qui entre dans un type borné est
/// vérifiée à la compilation si elle est connue, sinon à l'exécution.
/// </summary>
internal sealed partial class Binder
{
    #region Casts

    /// <summary>Un cast <c>as</c>, <c>as?</c> ou <c>as!</c> (<c>as!</c> dans un guard seulement, E060).</summary>
    private IrExpression? _BindCast(CastExpression cast)
    {
        var value = _BindExpression(cast.Value);
        var type = _BindType(cast.Type);
        if (value is null || type is null) return null;
        if (type == PrimitiveType.Char || value.Type == PrimitiveType.Char)
        {
            if (!_RequireUnsafe(cast.Span)) return null;
            if (cast.Kind == CastKind.Checked && (type == PrimitiveType.Char && value.Type is PrimitiveType { IsInteger: true } || value.Type == PrimitiveType.Char && type is PrimitiveType { IsInteger: true }))
                return new IrRawMemory(cast.Span, type, RawMemoryOperation.CharCast, [value]);
        }
        if (cast.Kind == CastKind.Forced && !_RequireGuard(cast.Span, "as!")) return null;

        if (cast.Kind == CastKind.Checked && _FindConversion(value.Type, type, "explicit") is { } conversion) return _OverloadCall(cast.Span, conversion, [value]);

        if (_BytesCast(cast, value, type) is { } bytesCast) return bytesCast.Value;
        if (value.Type == PrimitiveType.String && type is PrimitiveType { IsNumeric: true } parsed) return _ParseCast(cast, value, parsed);
        if (value.Type is PrimitiveType { IsNumeric: true } from && type is PrimitiveType { IsNumeric: true } to) return _NumericCast(cast, value, from, to);
        if (_DurationCast(cast, value, type) is { } durationCast) return durationCast.Value;

        if (value.Type is ArrayType fromArray && type is ArrayType toArray) return _ArrayCast(cast, value, fromArray, toArray);

        if (cast.Kind == CastKind.Checked && _CanCoerce(value, type))
        {
            if (value.Type == type)
                _diagnostics.Warning(cast.Span, DiagnosticCodes.UselessCast, $"la valeur est déjà un « {type} »", "cast inutile", ["retire le « as »"]);
            return _Coerce(value, type);
        }

        if (cast.Kind == CastKind.Checked && type == PrimitiveType.String && value.Type != PrimitiveType.String)
        {
            // Écrire en texte : la même chose que `{v}`, qui ne peut pas échouer.
            if (_IsWritable(value.Type)) return new IrTemplate(cast.Span, [value]);
            _Error(cast.Span, DiagnosticCodes.TypeMismatch, $"une valeur « {value.Type} » ne s'écrit pas en texte", $"de type « {value.Type} »",
                "comme dans un template, seuls les chaînes, nombres, bool, symboles, durées, enums, molds, tableaux… dont tout s'écrit en texte le peuvent");
            return null;
        }
        _Error(cast.Span, DiagnosticCodes.InvalidCast, $"« {_CastText(cast.Kind)} » ne convertit pas un « {value.Type} » en « {type} »", "conversion impossible",
            (value.Type, type) switch
            {
                (IngotType, _) => "un ingot perd sa marque avec °, par exemple : uid°",
                (_, IngotType ingot) => $"une valeur de la base devient un ingot par {ingot}(valeur)",
                (UnionType or ResultType, _) => "une union ou un résultat se lit avec match",
                _ when cast.Kind != CastKind.Checked => "as? et as! convertissent un nombre ou lisent un nombre dans un texte",
                _ => "as convertit un nombre en un autre, ou écrit une conversion implicite (duck typing, union, nullable)",
            });
        return null;
    }

    /// <summary>
    /// Les conversions entre texte et octets : <c>texte as bytes</c> (toujours possible, les octets UTF-8) et
    /// <c>octets as? string</c> / <c>octets as! string</c> (UTF-8 valide : sinon <c>null</c>, ou l'échec du guard ;
    /// <c>as</c> seul est E085). Rend <see langword="null"/> si ce n'est pas une conversion de ce genre, et
    /// <c>(null)</c> après avoir signalé une erreur.
    /// </summary>
    private (IrExpression? Value, bool Handled)? _BytesCast(CastExpression cast, IrExpression value, IrType type)
    {
        var fromText = value.Type is PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString };
        var toBytes = type is ArrayType { Kind: ArrayKind.Dynamic, Element: PrimitiveType { Kind: PrimitiveKind.UByte } };
        if (fromText && toBytes)
        {
            if (cast.Kind != CastKind.Checked)
            {
                _Error(cast.Span, DiagnosticCodes.InvalidCast, $"« {_CastText(cast.Kind)} » ne sert pas ici : un texte devient toujours des octets", "toujours possible",
                    $"écris {cast.Value.Span.Text} as bytes");
                return (null, true);
            }
            return (new IrStringToBytes(cast.Span, _Coerce(value, PrimitiveType.String)!, _bytes), true);
        }

        var fromBytes = value.Type is ArrayType { Element: PrimitiveType { Kind: PrimitiveKind.UByte } };
        if (fromBytes && type == PrimitiveType.String)
        {
            switch (cast.Kind)
            {
                case CastKind.Optional:
                    return (new IrBytesToString(cast.Span, value, new NullableType(PrimitiveType.String)), true);
                case CastKind.Forced:
                    return (new IrBytesToString(cast.Span, value, PrimitiveType.String), true);
                default:
                    _Error(cast.Span, DiagnosticCodes.InvalidCast, "« as » ne lit pas des octets en texte : ils ne sont pas toujours de l'UTF-8 valide", "lecture",
                        $"écris {cast.Value.Span.Text} as? string (null si ce n'est pas de l'UTF-8), ou dans un guard {cast.Value.Span.Text} as! string");
                    return (null, true);
            }
        }

        return null;
    }

    /// <summary>
    /// Un cast entre deux sortes de tableaux de mêmes éléments (E085 sinon) : <c>as</c> et <c>as!</c>
    /// contrôlent la taille (arrêt, ou échec du guard) ; <c>as?</c> donne <c>null</c> si elle ne convient pas.
    /// </summary>
    private IrExpression? _ArrayCast(CastExpression cast, IrExpression value, ArrayType from, ArrayType to)
    {
        if (from.Element != to.Element)
        {
            _Error(cast.Span, DiagnosticCodes.InvalidCast, $"« {_CastText(cast.Kind)} » ne convertit pas un « {from} » en « {to} »", "conversion impossible",
                $"les éléments d'un tableau gardent leur type : des « {from.Element} » ne deviennent pas des « {to.Element} »");
            return null;
        }
        if (value.Type == to)
            _diagnostics.Warning(cast.Span, DiagnosticCodes.UselessCast, $"la valeur est déjà un « {to} »", "cast inutile", ["retire le « as »"]);

        if (cast.Kind == CastKind.Optional) return new IrArrayConvert(cast.Span, new NullableType(to), value, to, ArrayConversion.Optional);
        if (_CoerceArray(value, to) is { } converted) return converted with { Span = cast.Span };

        _Error(cast.Span, DiagnosticCodes.OutOfRange, $"ce tableau n'a pas une taille permise par « {to} »", "mauvaise taille",
            $"un « {to} » a {_CountText(to)} élément(s)");
        return null;
    }

    private static string _CastText(CastKind kind) => kind switch
    {
        CastKind.Optional => "as?",
        CastKind.Forced => "as!",
        _ => "as",
    };

    /// <summary>
    /// <c>texte as? int</c> (null si le texte n'est pas un nombre du type) ou <c>texte as! int</c> (le
    /// guard échoue) ; <c>as</c> seul ne lit pas un nombre (E085). Les bornes d'un type borné sont
    /// contrôlées ensuite.
    /// </summary>
    private IrExpression? _ParseCast(CastExpression cast, IrExpression text, PrimitiveType target)
    {
        var number = target.Unbounded;
        switch (cast.Kind)
        {
            case CastKind.Optional:
                var parsed = new IrTryParseNumber(cast.Span, new NullableType(number), text);
                return target.Bounds is null ? parsed : new IrTryCast(cast.Span, new NullableType(target), parsed);

            case CastKind.Forced:
                var read = new IrParseNumber(cast.Span, number, text);
                return target.Bounds is null ? read : new IrBoundCheck(cast.Span, target, read);

            default:
                _Error(cast.Span, DiagnosticCodes.InvalidCast, $"« as » ne lit pas un nombre dans un texte : la lecture peut échouer", "lecture",
                    $"écris {cast.Value.Span.Text} as? {target} (null si ce n'est pas un nombre), ou dans un guard {cast.Value.Span.Text} as! {target}");
                return null;
        }
    }

    /// <summary>
    /// Les conversions entre durée et nombre, comptées en millisecondes : <c>d as int</c> (comme un cast
    /// numérique depuis le <c>long</c> des millisecondes : contrôlé, <c>as?</c> et <c>as!</c> compris) et
    /// <c>n as duration</c> / <c>n as! duration</c> (<c>as?</c> n'existe pas : une durée n'a pas de nullable de cast).
    /// Rend <see langword="null"/> si ce n'est pas une conversion de ce genre.
    /// </summary>
    private (IrExpression? Value, bool Handled)? _DurationCast(CastExpression cast, IrExpression value, IrType type)
    {
        var milliseconds = new PrimitiveType(PrimitiveKind.Long);
        if (value.Type == PrimitiveType.Duration && type is PrimitiveType { IsNumeric: true } to)
            return (_NumericCast(cast, new IrDurationMilliseconds(value.Span, value), milliseconds, to), true);

        if (value.Type == PrimitiveType.DateTime && type is PrimitiveType { IsNumeric: true } toNumber)
            return (_NumericCast(cast, new IrDurationMilliseconds(value.Span, value), milliseconds, toNumber), true);

        if (type == PrimitiveType.DateTime && value.Type is PrimitiveType { IsNumeric: true } fromNumber)
        {
            if (cast.Kind == CastKind.Optional)
            {
                _Error(cast.Span, DiagnosticCodes.InvalidCast, "« as? » ne convertit pas en datetime", "conversion impossible",
                    "écris as (contrôlé : arrêt hors des 64 bits) ou, dans un guard, as!");
                return (null, true);
            }
            var epoch = _NumericCast(cast, value, fromNumber, milliseconds);
            return (epoch is null ? null : new IrDateTimeOf(cast.Span, epoch), true);
        }

        if (type == PrimitiveType.Duration && value.Type is PrimitiveType { IsNumeric: true } from)
        {
            if (cast.Kind == CastKind.Optional)
            {
                _Error(cast.Span, DiagnosticCodes.InvalidCast, "« as? » ne convertit pas en duration", "conversion impossible",
                    "écris as (contrôlé : arrêt hors des 64 bits) ou, dans un guard, as!");
                return (null, true);
            }
            var count = _NumericCast(cast, value, from, milliseconds);
            return (count is null ? null : new IrDurationOf(cast.Span, count), true);
        }
        return null;
    }

    /// <summary>Un cast entre deux types numériques, bornés ou non.</summary>
    private IrExpression? _NumericCast(CastExpression cast, IrExpression value, PrimitiveType from, PrimitiveType to)
    {
        var target = to.Unbounded;
        var source = _Unbounded(value);
        if (value.Type == to)
            _diagnostics.Warning(cast.Span, DiagnosticCodes.UselessCast, $"la valeur est déjà un « {to} »", "cast inutile", ["retire le « as »"]);

        // Une valeur connue : elle tient dans le type visé, ou c'est une erreur pour « as ».
        if (ConstantFolder.Fold(source) is { } known && cast.Kind != CastKind.Optional)
        {
            var converted = ConstantFolder.Cast(known, target);
            if (converted is null || !(to.Bounds?.Contains(converted) ?? true))
            {
                if (cast.Kind == CastKind.Checked)
                {
                    _Error(cast.Span, DiagnosticCodes.OutOfRange, $"{_ValueText(known)} ne tient pas dans « {to} »", "hors limites",
                        double.IsNaN(known as double? ?? 0) ? "NaN n'est aucun entier" : $"les valeurs d'un « {to} » vont de {_RangeText(to)}");
                    return null;
                }
            }
            else
            {
                IrExpression constant = target == source.Type ? source : new IrNumericCast(cast.Span, target, source, Checked: false);
                return to.Bounds is null ? constant : new IrRetype(cast.Span, to, constant);
            }
        }

        if (cast.Kind == CastKind.Optional) return new IrTryCast(cast.Span, new NullableType(to), source);

        // Un flottant vers un entier est toujours contrôlé : le C++ ne définit rien hors plage.
        var mayNotFit = target.IsInteger && !_AlwaysFits(from, target);
        var isChecked = mayNotFit && (cast.Kind == CastKind.Forced || !from.IsInteger || _overflow == OverflowMode.Checked);
        IrExpression result = target == source.Type ? source : new IrNumericCast(cast.Span, target, source, isChecked);

        if (to.Bounds is null) return result;
        return from.Bounds is { } own && to.Bounds.Contains(own) && (target.IsInteger || !from.IsInteger)
            ? new IrRetype(cast.Span, to, result)
            : new IrBoundCheck(cast.Span, to, result);
    }

    /// <summary>Indique si toute valeur de <paramref name="from"/> (ses bornes comprises) tient dans le type entier <paramref name="to"/>.</summary>
    private static bool _AlwaysFits(PrimitiveType from, PrimitiveType to)
    {
        if (from.Bounds is { } bounds)
            return ConstantFolder.Cast(bounds.Min, to) is not null && ConstantFolder.Cast(bounds.Max, to) is not null;
        return from.IsInteger && _IsLossless(from.Kind, to.Kind);
    }

    /// <summary>L'étendue d'un type numérique, pour les messages : <c>-128 à 127</c>.</summary>
    private static string _RangeText(PrimitiveType type)
    {
        if (type.Bounds is { } bounds) return $"{_ValueText(bounds.Min)} à {_ValueText(bounds.Max)}";
        return type.Kind switch
        {
            PrimitiveKind.Byte => "-128 à 127",
            PrimitiveKind.UByte => "0 à 255",
            PrimitiveKind.Short => "-32768 à 32767",
            PrimitiveKind.UShort => "0 à 65535",
            PrimitiveKind.Int => "-2147483648 à 2147483647",
            PrimitiveKind.UInt => "0 à 4294967295",
            PrimitiveKind.Long => "-9223372036854775808 à 9223372036854775807",
            PrimitiveKind.ULong => "0 à 18446744073709551615",
            _ => "-3.4e38 à 3.4e38",
        };
    }

    /// <summary>Une valeur connue à la compilation, telle qu'on l'écrirait en RGL.</summary>
    private static string _ValueText(object value) => value switch
    {
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    #endregion

    #region Types bornés

    /// <summary>
    /// <c>int&lt;0..100&gt;</c> : un type numérique (E087 sinon), deux bornes connues à la compilation,
    /// entières pour un entier, dans le type, la basse au plus égale à la haute.
    /// </summary>
    private PrimitiveType? _BindBoundedType(AstTypes.BoundedType bounded)
    {
        var basis = _BindType(bounded.Base) as PrimitiveType;
        var min = _BindExpression(bounded.Min);
        var max = _BindExpression(bounded.Max);
        if (basis is null || min is null || max is null) return null;

        if (!basis.IsNumeric)
        {
            _Error(bounded.Base.Span, DiagnosticCodes.InvalidBounds, $"seul un type numérique a des bornes, pas un « {basis} »", "pas un nombre");
            return null;
        }

        object? Bound(IrExpression bound)
        {
            var value = ConstantFolder.Fold(bound);
            if (value is not (long or double))
            {
                _Error(bound.Span, DiagnosticCodes.InvalidBounds, "une borne est un nombre connu à la compilation", "pas une constante",
                    $"par exemple : {basis}<0..100>");
                return null;
            }
            if (basis.IsInteger && value is double)
            {
                _Error(bound.Span, DiagnosticCodes.InvalidBounds, $"les bornes d'un « {basis} » sont des entiers", "flottant");
                return null;
            }
            if (!ConstantFolder.Fits(value, basis))
            {
                _Error(bound.Span, DiagnosticCodes.InvalidBounds, $"{_ValueText(value)} ne tient pas dans « {basis} »", "hors du type",
                    $"les valeurs d'un « {basis} » vont de {_RangeText(basis)}");
                return null;
            }
            return basis.IsInteger ? value : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        var (low, high) = (Bound(min), Bound(max));
        if (low is null || high is null) return null;

        var bounds = new NumericBounds(low, high);
        if (!bounds.Contains(low) || !bounds.Contains(high))
        {
            _Error(bounded.Span, DiagnosticCodes.InvalidBounds, $"la borne basse {_ValueText(low)} dépasse la borne haute {_ValueText(high)}", "bornes inversées",
                $"écris {basis}<{_ValueText(high)}..{_ValueText(low)}>");
            return null;
        }

        return new PrimitiveType(basis.Kind, bounds);
    }

    #endregion
}
