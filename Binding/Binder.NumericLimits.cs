using Rgl.Diagnostics;
using Rgl.AST.Expressions;
using Rgl.IR.Expressions;
using Rgl.IR.Types;

namespace Rgl.Binding;

/// <summary>
/// <c>int.min</c>, <c>int.max</c>, <c>double.max</c>… : les bornes d'un type numérique. Pour un flottant,
/// <c>min</c> est la plus petite valeur finie (la plus négative), comme <c>double.MinValue</c> en C#.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Signale <c>int.autre</c> : un type numérique n'a que <c>min</c> et <c>max</c>. Vrai si une erreur a été écrite.</summary>
    private bool _ReportUnknownLimit(MemberAccessExpression access)
    {
        if (access.IsSafe || access.Member.IsMissing || access.Member.Name is "min" or "max") return false;
        if (access.Target is not NameExpression { Name.Name: var typeName } || _scope[typeName].HasValue || _parameters.ContainsKey(typeName)) return false;
        if (typeName is not ("byte" or "ubyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double")) return false;

        _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"le type « {typeName} » n'a pas de membre « {access.Member.Name} »", "inconnu",
            new[] { $"un type numérique n'a que min et max : {typeName}.max" });
        return true;
    }

    /// <summary>La borne désignée par <paramref name="access"/> ; <see langword="null"/> si ce n'est pas <c>type.min</c> ou <c>type.max</c>.</summary>
    private IrExpression? _NumericLimit(MemberAccessExpression access)
    {
        if (access.IsSafe || access.Member.Name is not ("min" or "max")) return null;
        if (access.Target is not NameExpression { Name.Name: var typeName }) return null;
        if (_scope[typeName].HasValue || _parameters.ContainsKey(typeName)) return null;

        var isMax = access.Member.Name == "max";
        var span = access.Span;
        switch (typeName)
        {
            case "byte": return new IrIntegerConstant(span, new PrimitiveType(PrimitiveKind.Byte), isMax ? sbyte.MaxValue : sbyte.MinValue);
            case "ubyte": return new IrIntegerConstant(span, new PrimitiveType(PrimitiveKind.UByte), isMax ? byte.MaxValue : 0);
            case "short": return new IrIntegerConstant(span, new PrimitiveType(PrimitiveKind.Short), isMax ? short.MaxValue : short.MinValue);
            case "ushort": return new IrIntegerConstant(span, new PrimitiveType(PrimitiveKind.UShort), isMax ? ushort.MaxValue : 0);
            case "int": return new IrIntegerConstant(span, PrimitiveType.Int, isMax ? int.MaxValue : int.MinValue);
            case "uint": return new IrIntegerConstant(span, PrimitiveType.UInt, isMax ? uint.MaxValue : 0);
            case "long": return new IrIntegerConstant(span, PrimitiveType.Long, isMax ? long.MaxValue : long.MinValue);
            case "ulong":
                var type = new PrimitiveType(PrimitiveKind.ULong);
                var zero = new IrIntegerConstant(span, type, 0);
                return isMax ? new IrUnary(span, type, IrUnaryOperator.BitwiseNot, zero) : zero;
            case "float":
                return new IrConversion(span, new PrimitiveType(PrimitiveKind.Float), new IrFloatConstant(span, isMax ? float.MaxValue : float.MinValue));
            case "double": return new IrFloatConstant(span, isMax ? double.MaxValue : double.MinValue);
            default: return null;
        }
    }
}
