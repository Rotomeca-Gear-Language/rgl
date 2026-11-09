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
/// L'arithmétique entière sûre : opérations contrôlées ou en roulement selon le mode du
/// programme, décalages, increment, et division dans un guard.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// Une division ou un reste entier dans un guard, par un diviseur inconnu à la compilation : un
    /// diviseur nul fait échouer le guard (« division entière par zéro ») au lieu d'arrêter le
    /// programme. Les opérandes sont évalués une fois, de gauche à droite.
    /// </summary>
    private string _GuardedDivision(IrBinary division, string left, string right)
    {
        _usesStatementExpressions = true;
        var (a, b) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var name = division.Operator == IrBinaryOperator.Divide ? "div" : "rem";
        var message = _String(new IrStringConstant(division.Span, PrimitiveType.String, "division entière par zéro"));
        return $"({{ const auto {a} = {left}; const auto {b} = {right}; if ({b} == 0) return {_StandardFailure(_exceptionMold, message, division.OperatorSpan)}; "
            + $"{_Arithmetic(name, division, a, b)}; }})";
    }

    /// <summary>
    /// <c>increment v</c> et ses variantes : modifie la variable et vaut sa nouvelle valeur, ou son
    /// ancienne pour une forme <c>post_</c> (<c>rgl::standard::checked_post_increment&lt;T&gt;(v, 1, …)</c>).
    /// </summary>
    private string _Increment(IrIncrement increment)
    {
        var name = (increment.IsPost ? "post_" : "") + (increment.IsDecrement ? "decrement" : "increment");
        var type = (PrimitiveType)_Representation(increment.Variable.Type);
        var variable = _variableNames[increment.Variable];
        var step = type.IsInteger ? _Integer(increment.Step, type.Unbounded) : _Float(increment.Step);
        var call = _Arithmetic(name, increment, variable, step);
        if (type.Bounds is null) return call;

        // Une variable bornée : sa nouvelle valeur est contrôlée, la valeur rendue reste celle de l'opération.
        _includes.Add("rgl/standard/cast.hpp");
        var result = $"rgl_t{++_temporaryCount}";
        var (min, max) = _Bounds(type);
        var cpp = _Type(type);
        if (_InGuard)
        {
            _usesStatementExpressions = true;
            return $"({{ const auto {result} = {call}; if (!rgl::standard::in_bounds<{cpp}>({variable}, {min}, {max})) "
                + $"return {_StandardFailure(_castErrorMold, _String(new IrStringConstant(increment.Span, PrimitiveType.String, _OutOfBoundsMessage(type))), increment.Span)}; {result}; }})";
        }
        return $"[&]() -> {cpp} {{ const auto {result} = {call}; "
            + $"static_cast<void>(rgl::standard::checked_bounds<{cpp}>({variable}, {min}, {max}, {_Quote(_OutOfBoundsMessage(type))}, {_Where(increment.Span)})); return {result}; }}()";
    }

    /// <summary>
    /// Un décalage dont le nombre de bits est connu et dans les limites (de 0 à la largeur du type
    /// exclue) ne peut pas échouer : il s'écrit directement, sans vérification. Pour un type plus
    /// petit qu'un <c>int</c>, que le C++ élargit en <c>int</c>, le résultat est ramené à son type.
    /// </summary>
    private static bool _IsValidShift(IrBinary shift) =>
        ConstantFolder.Fold(shift.Right) is long bits && bits >= 0 && bits < _Bits(shift.Type);

    /// <summary>Largeur en bits d'un type entier.</summary>
    private static int _Bits(IrType type) => type switch
    {
        PrimitiveType { Kind: PrimitiveKind.Byte or PrimitiveKind.UByte } => 8,
        PrimitiveType { Kind: PrimitiveKind.Short or PrimitiveKind.UShort } => 16,
        PrimitiveType { Kind: PrimitiveKind.Int or PrimitiveKind.UInt } => 32,
        _ => 64,
    };

    /// <summary>
    /// Une opération entière peut déborder (ou diviser par zéro, ou décaler hors limites), sauf si
    /// sa valeur est déjà calculable à la compilation : le calcul a alors réussi.
    /// </summary>
    private static bool _MayOverflow(IrExpression operation) =>
        operation.Type is PrimitiveType { IsInteger: true } && operation is not IrBinary { Left.Type: PrimitiveType { Kind: PrimitiveKind.Duration } }
        && ConstantFolder.Fold(operation) is null;

    /// <summary>
    /// Vrai pour un diviseur entier connu à la compilation, ni nul (arrêt) ni -1 (seul cas où une division
    /// déborde) : la division et le reste n'ont alors rien à contrôler.
    /// </summary>
    private static bool _IsSafeDivisor(IrExpression divisor) => ConstantFolder.Fold(divisor) switch
    {
        long value => value is not (0 or -1),
        ulong value => value != 0,
        _ => false,
    };

    /// <summary>
    /// Une opération entière qui peut échouer, selon le mode du programme : <c>checked_add</c>
    /// (arrêt, avec l'endroit dans le source RGL) ou <c>wrapping_add</c> (roulement). La division et
    /// le reste reçoivent l'endroit dans les deux modes : un diviseur nul arrête toujours le programme.
    /// </summary>
    /// <param name="name">Nom de l'opération : <c>add</c>, <c>sub</c>, <c>mul</c>, <c>div</c>, <c>rem</c>, <c>shl</c>, <c>shr</c>, <c>negate</c>, <c>increment</c>…</param>
    /// <param name="operation">L'opération, pour son type et sa position.</param>
    /// <param name="operands">Les opérandes déjà traduits.</param>
    private string _Arithmetic(string name, IrExpression operation, params string[] operands)
    {
        _includes.Add("rgl/standard/arithmetic.hpp");
        // Dans une boucle dont les étapes n'ont aucun effet, le débordement s'accumule et se teste une fois, après la boucle.
        if (_groupedOverflow is { } flag && _overflow == OverflowMode.Checked && name is "add" or "sub" or "mul")
        {
            _groupedUsed = true;
            return $"rgl::standard::grouped_{name}<{_Type(operation.Type)}>({string.Join(", ", operands)}, {flag})";
        }
        var call = $"rgl::standard::{(_overflow == OverflowMode.Checked ? "checked" : "wrapping")}_{name}<{_Type(operation.Type)}>({string.Join(", ", operands)}";
        if (_overflow == OverflowMode.Unchecked && name is not ("div" or "rem")) return call + ")";

        // L'endroit signalé est l'opérateur : dans a + b - c, on sait lequel a échoué.
        return $"{call}, {_Position(operation is IrBinary binary ? binary.OperatorSpan : operation.Span)})";
    }
}
