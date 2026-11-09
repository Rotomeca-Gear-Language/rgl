using System.Globalization;
using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Les casts et les types bornés : conversions numériques contrôlées, as?, lecture d'un nombre
/// dans un texte, et contrôle des bornes.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// <c>texte as! T</c>, dans un guard : le nombre lu, ou l'échec du guard si le texte n'en est pas
    /// un (<c>rgl::standard::parse_number</c>, <c>rgl::standard::cast_failure</c>).
    /// </summary>
    private string _ParseNumber(IrParseNumber parse, string text)
    {
        _usesStatementExpressions = true;
        _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
        _includes.Add("rgl/standard/convert.hpp");
        var (input, number) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var message = $"rgl::standard::cast_message({input}, {_Quote(parse.Type.ToString())})";
        return $"({{ const auto& {input} = {text}; {_Type(parse.Type)} {number}{{}}; "
            + $"if (!rgl::standard::parse_number({input}, {number})) return {_StandardFailure(_castErrorMold, message, parse.Span, input, _String(new IR.Expressions.IrStringConstant(parse.Span, PrimitiveType.String, parse.Type.ToString())))}; {number}; }})";
    }

    /// <summary>Le type qui représente une valeur en C++ : la base d'un ingot, sous toutes ses marques.</summary>
    private static IrType _Representation(IrType type) => type is IngotType ingot ? ingot.Underlying : type;

    /// <summary>
    /// <c>v as T</c> entre deux nombres : directement si la valeur tient toujours (ou si elle est
    /// connue), contrôlée sinon — arrêt du programme (<c>rgl::standard::checked_cast</c>), ou échec du
    /// guard (<c>rgl::standard::fits</c>). Un double devient un float par <c>narrow_float</c>, qui
    /// donne l'infini au-delà du plus grand float.
    /// </summary>
    private string _NumericCast(IrNumericCast cast, string value)
    {
        var to = _Type(cast.Target);
        if (ConstantFolder.Fold(cast) is long known) return _Integer(known, cast.Target);

        if (cast.Target.Kind == PrimitiveKind.Float && cast.Value.Type is PrimitiveType { Kind: PrimitiveKind.Double })
        {
            _includes.Add("rgl/standard/cast.hpp");
            return $"rgl::standard::narrow_float<float>({value})";
        }
        if (!cast.Checked) return $"static_cast<{to}>({value})";

        _includes.Add("rgl/standard/cast.hpp");
        var message = $"conversion impossible : la valeur ne tient pas dans un {cast.Target}";
        if (!_InGuard) return $"rgl::standard::checked_cast<{to}>({value}, {_Quote(message)}, {_Where(cast.Span)})";

        _usesStatementExpressions = true;
        var temporary = $"rgl_t{++_temporaryCount}";
        return $"({{ const auto {temporary} = {value}; if (!rgl::standard::fits<{to}>({temporary})) "
            + $"return {_StandardFailure(_castErrorMold, _String(new IrStringConstant(cast.Span, PrimitiveType.String, message)), cast.Span)}; static_cast<{to}>({temporary}); }})";
    }

    /// <summary>
    /// Une valeur qui entre dans un type borné : <c>rgl::standard::checked_bounds</c> (arrêt hors des
    /// bornes), ou l'échec du guard qui l'évalue.
    /// </summary>
    private string _BoundCheck(IrBoundCheck check, string value)
    {
        _includes.Add("rgl/standard/cast.hpp");
        var type = _Type(check.Target);
        var (min, max) = _Bounds(check.Target);
        var message = _OutOfBoundsMessage(check.Target);
        if (!_InGuard) return $"rgl::standard::checked_bounds<{type}>({value}, {min}, {max}, {_Quote(message)}, {_Where(check.Span)})";

        _usesStatementExpressions = true;
        var temporary = $"rgl_t{++_temporaryCount}";
        return $"({{ const auto {temporary} = {value}; if (!rgl::standard::in_bounds<{type}>({temporary}, {min}, {max})) "
            + $"return {_StandardFailure(_castErrorMold, _String(new IrStringConstant(check.Span, PrimitiveType.String, message)), check.Span)}; {temporary}; }})";
    }

    /// <summary><c>v as? T</c> : <c>rgl::standard::try_cast</c>, puis <c>try_bounds</c> pour un type borné.</summary>
    private string _TryCast(IrTryCast cast, string value)
    {
        _includes.Add("rgl/standard/cast.hpp");
        var target = (PrimitiveType)cast.Result.Inner;
        var type = _Type(target);
        // Un texte déjà lu par as? est un nullable : il ne reste que les bornes.
        var tried = cast.Value.Type is NullableType ? value : $"rgl::standard::try_cast<{type}>({value})";
        if (target.Bounds is null) return tried;

        var (min, max) = _Bounds(target);
        return $"rgl::standard::try_bounds<{type}>({tried}, {min}, {max})";
    }

    /// <summary>Les bornes d'un type borné, écrites dans son type C++.</summary>
    private (string Min, string Max) _Bounds(PrimitiveType type)
    {
        string Bound(object value) => value switch
        {
            long integer => _Integer(integer, type.Unbounded),
            double real when type.Kind == PrimitiveKind.Float => $"static_cast<float>({_Float(real)})",
            double real => _Float(real),
            _ => throw new InvalidOperationException($"Borne inattendue : {value}."),
        };
        return (Bound(type.Bounds!.Min), Bound(type.Bounds.Max));
    }

    /// <summary>Le message d'une valeur hors des bornes, en ASCII : il est aussi écrit tel quel par le runtime.</summary>
    private static string _OutOfBoundsMessage(PrimitiveType type) => $"valeur hors des bornes de {type}";
}
