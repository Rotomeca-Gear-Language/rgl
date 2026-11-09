using System.Numerics;
using Rgl.AST.Collections;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Types;

namespace Rgl.Binding;

/// <summary>
/// Les intervalles d'entiers : <c>[0..10]</c> (fin comprise), <c>[0..&lt;10]</c> (fin exclue),
/// <c>[0..10 by 2]</c>, <c>[10..0]</c>. Les bornes sont des entiers, ramenés à un type commun ; le
/// pas est un <c>long</c>, jamais nul. Sans pas écrit, l'intervalle monte, sauf quand ses bornes
/// connues à la compilation descendent : il faut sinon un pas négatif pour descendre. Un intervalle
/// est un tableau comme un autre, de taille fixe quand tout est connu à la compilation.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Un intervalle ; le type attendu peut donner le type de ses éléments (<c>[uint] r: [0..10];</c>).</summary>
    private IrExpression? _BindRange(RangeExpression range, IrType? expected)
    {
        var start = _BindExpression(range.Start);
        var end = _BindExpression(range.End);
        var step = range.Step is null ? null : _BindExpression(range.Step);
        if (start is null || end is null || (range.Step is not null && step is null)) return null;

        if (_RangeBounds(range, start, end, _ExpectedOf<ArrayType>(expected)?.Element) is not { } bounds) return null;
        var (first, last) = bounds;

        IrExpression stride;
        if (step is null)
            stride = new IrIntegerConstant(range.Span, PrimitiveType.Long, _Known(first) > _Known(last) ? -1 : 1);
        else if (_RangeStep(step) is { } given) stride = given;
        else return null;

        var count = _RangeCount(range, first, last, stride);
        if (count is null) return null;

        var element = first.Type;
        var type = count is { } known && known >= BigInteger.One && known <= uint.MaxValue
            ? new ArrayType(element, ArrayKind.Fixed, (uint)known)
            : new ArrayType(element);
        return new IrRange(range.Span, type, first, last, stride, range.IsExclusive);
    }

    /// <summary>
    /// Les deux bornes, des entiers (E100 sinon) ramenés au type des éléments attendus quand elles y
    /// tiennent, sinon à leur type commun (E100 s'il n'y en a pas : un cast le donne).
    /// </summary>
    private (IrExpression First, IrExpression Last)? _RangeBounds(RangeExpression range, IrExpression start, IrExpression end, IrType? expectedElement)
    {
        start = _Unbounded(_Unbranded(start));
        end = _Unbounded(_Unbranded(end));
        foreach (var bound in new[] { start, end })
        {
            if (bound.Type is PrimitiveType { IsInteger: true }) continue;
            _Error(bound.Span, DiagnosticCodes.InvalidRange, $"une borne d'intervalle est un entier, pas un « {bound.Type} »", $"de type « {bound.Type} »",
                "un intervalle compte d'un entier à un autre, par exemple : [0..10] ; il n'y a pas d'intervalle de flottants");
            return null;
        }

        if (expectedElement is PrimitiveType { IsInteger: true } wanted && _Coerce(start, wanted) is { } first && _Coerce(end, wanted) is { } last)
            return (first, last);

        // Un littéral prend le type de l'autre borne, nommée : [0..<MAX] compte en « uint » si MAX en est un.
        if (start is IrIntegerConstant && end is not IrIntegerConstant && _Coerce(start, end.Type) is { } literalStart) return (literalStart, end);
        if (end is IrIntegerConstant && start is not IrIntegerConstant && _Coerce(end, start.Type) is { } literalEnd) return (start, literalEnd);
        if (_Unify(start, end) is { } common) return common;

        _Error(range.Span, DiagnosticCodes.InvalidRange, $"les bornes « {start.Type} » et « {end.Type} » n'ont pas de type commun", "bornes de types différents",
            $"convertis une borne avec as, par exemple : [{range.Start.Span.Text} as {end.Type}..{range.End.Span.Text}]");
        return null;
    }

    /// <summary>Le pas écrit : un entier qui tient dans un <c>long</c> (E100 sinon), jamais nul quand il est connu.</summary>
    private IrExpression? _RangeStep(IrExpression step)
    {
        step = _Unbounded(_Unbranded(step));
        if (step.Type is PrimitiveType { IsInteger: true } && _Coerce(step, PrimitiveType.Long) is { } stride)
        {
            if (_Known(stride) != BigInteger.Zero) return stride;
            _Error(step.Span, DiagnosticCodes.InvalidRange, "le pas d'un intervalle n'est jamais nul", "pas nul",
                "un pas positif monte, un pas négatif descend : [10..0 by -2]");
            return null;
        }

        _Error(step.Span, DiagnosticCodes.InvalidRange, $"le pas d'un intervalle est un entier, pas un « {step.Type} »", $"de type « {step.Type} »",
            "par exemple : [0..10 by 2]");
        return null;
    }

    /// <summary>
    /// Le nombre de valeurs quand tout est connu à la compilation ; <see cref="BigInteger.MinusOne"/>
    /// quand il ne l'est pas. Un pas qui va dans l'autre sens que des bornes connues est E100
    /// (<see langword="null"/>) : l'intervalle serait toujours vide.
    /// </summary>
    private BigInteger? _RangeCount(RangeExpression range, IrExpression first, IrExpression last, IrExpression stride)
    {
        if (_Known(first) is not { } from || _Known(last) is not { } to || _Known(stride) is not { } step) return BigInteger.MinusOne;

        if ((step > 0 && to < from) || (step < 0 && to > from))
        {
            _Error(stride.Span, DiagnosticCodes.InvalidRange, $"le pas {step} va dans l'autre sens que les bornes {from} et {to}", "sens opposé",
                $"l'intervalle serait toujours vide : écris [{range.Start.Span.Text}..{range.End.Span.Text} by {-step}]");
            return null;
        }

        var span = BigInteger.Abs(to - from) - (range.IsExclusive ? 1 : 0);
        return span < 0 ? BigInteger.Zero : span / BigInteger.Abs(step) + 1;
    }

    /// <summary>La valeur entière d'une expression connue à la compilation ; <see langword="null"/> sinon.</summary>
    private static BigInteger? _Known(IrExpression value) => ConstantFolder.Fold(value) switch
    {
        long number => number,
        ulong number => number,
        _ => null,
    };
}
