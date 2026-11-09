using Rgl.IR.Expressions;

namespace Rgl.Emit;

/// <summary>
/// Les regex : un littéral devient un petit struct qui donne ses deux moteurs CTRE (la première occurrence, toutes), écrit une fois par
/// motif et par flags avant le code ; la valeur est <c>rgl::standard::make_regex&lt;struct, global&gt;()</c> (<c>regex.hpp</c>).
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Un littéral regex : son struct (écrit une fois) et la valeur qui l'utilise.</summary>
    private string _Regex(IrRegexConstant constant)
    {
        var flags = constant.Flags.Replace("g", "");
        var key = (constant.Pattern, flags);
        var index = _regexes.IndexOf(key);
        if (index < 0)
        {
            _regexes.Add(key);
            index = _regexes.Count - 1;
        }
        return $"rgl::standard::make_regex<rgl_re_{index}, {(constant.Global ? "true" : "false")}>()";
    }

    /// <summary>Le struct d'un motif : <c>first</c> et <c>all</c>, le motif en UTF-8 dans une chaîne brute.</summary>
    private static string _RegexStruct(int index, string pattern, string flags)
    {
        var delimiter = "rgl";
        for (var i = 0; pattern.Contains($"){delimiter}\"", StringComparison.Ordinal); ++i) delimiter = "rgl" + i;
        var literal = $"u8R\"{delimiter}({pattern}){delimiter}\"";

        var multiline = flags.Contains('m');
        var caseless = flags.Contains('i') ? ", ctre::case_insensitive" : "";
        var prefix = multiline ? "multiline_" : "";
        return $"struct rgl_re_{index} {{\n"
            + $"    static constexpr auto first = ctre::{prefix}search<{literal}{caseless}>;\n"
            + $"    static constexpr auto all = ctre::{prefix}search_all<{literal}{caseless}>;\n"
            + "};\n";
    }

    /// <summary><c>r.test(s)</c> et ses trois sœurs : une méthode de <c>rgl::standard::regex</c>.</summary>
    private string _RegexOperation(IrRegexOp operation, Func<IrExpression, string> operand)
    {
        _Type(operation.Target.Type);
        var method = operation.Operation switch
        {
            RegexOperation.Test => "test",
            RegexOperation.Match => "match",
            RegexOperation.Find => "find",
            _ => "replace",
        };
        _Type(operation.Type);
        var arguments = operation.Replacement is null ? operand(operation.Subject) : $"{operand(operation.Subject)}, {operand(operation.Replacement)}";
        return $"({operand(operation.Target)}).{method}({arguments})";
    }
}
