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
/// Nullables, unions et match : les valeurs lues une seule fois (_WithTemporary), la coalescence,
/// l'accès sûr, le champ commun d'une union, et le match en instruction ou en valeur.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// Une expression qui lit plusieurs fois une valeur, évaluée une seule fois : elle est rangée dans
    /// une constante au début d'une lambda appelée sur place (une expression-instruction dans un
    /// guard). <paramref name="body"/> reçoit le nom de la constante.
    /// </summary>
    private string _WithTemporary(IrType type, IrExpression value, Func<string, string> body)
    {
        var name = $"rgl_t{++_temporaryCount}";
        var store = $"const auto& {name} = {_Expression(value)};";
        if (_UseStatementExpressions)
        {
            _usesStatementExpressions = true;
            return $"({{ {store} {body(name)}; }})";
        }
        return $"[{(_atFileLevel ? "" : "&")}]() -> {_Type(type)} {{ {store} return {body(name)}; }}()";
    }

    /// <summary>
    /// <c>valeur.champ</c> sur une union de molds : le champ du membre présent, choisi par son index,
    /// mis dans le type de l'accès (l'union des types du champ s'ils diffèrent).
    /// </summary>
    private string _UnionField(IrUnionField access)
    {
        return _WithTemporary(access.Type, access.Target, value =>
        {
            string Branch(int i)
            {
                var field = $"rgl::standard::get<{i}>({value}).rgl_f_{access.Fields[i].Name}";
                var from = access.Fields[i].Type;
                return from == access.Type ? field
                    : access.Type is not UnionType union ? throw new InvalidOperationException($"Champ « {from} » hors de « {access.Type} ».")
                    : from is UnionType ? $"rgl::standard::widen<{_Type(union)}>({field})"
                    : $"{_Type(union)}(rgl::standard::member<{union.IndexOf(from)}>, {field})";
            }

            var text = Branch(access.Fields.Count - 1);
            for (var i = access.Fields.Count - 2; i >= 0; --i) text = $"({value}.index() == {i} ? {Branch(i)} : {text})";
            return text;
        });
    }

    /// <summary><c>a ?° b</c> : <c>a</c> s'il n'est ni <c>null</c> ni vide, sinon <c>b</c>.</summary>
    private string _EmptyCoalesce(IrEmptyCoalesce coalesce)
    {
        var nullable = coalesce.Left.Type is NullableType;
        return _WithTemporary(coalesce.Type, coalesce.Left, value =>
        {
            var filled = nullable ? $"({value}.has_value() && !(*{value}).empty())" : $"!{value}.empty()";
            var kept = nullable && coalesce.Type is not NullableType ? "*" + value : value;
            return $"{filled} ? {kept} : {_Expression(coalesce.Right)}";
        });
    }

    /// <summary>
    /// La condition qui choisit un bras, sur la valeur rangée dans <paramref name="subject"/> ;
    /// <see langword="null"/> pour <c>default</c>.
    /// </summary>
    private string? _ArmTest(IrMatchArm arm, string subject) => arm.Kind switch
    {
        IrMatchKind.Case => _Expression(arm.Test!),
        IrMatchKind.Member => $"{subject}.index() == {arm.Index}",
        IrMatchKind.Ok => $"{subject}.is_ok()",
        IrMatchKind.Err => $"!{subject}.is_ok()",
        _ => null,
    };

    /// <summary>
    /// Le nom de la variable qui range la valeur d'un match : celle que lisent les tests d'un match de valeurs (<c>rgl_swN</c>, comme
    /// un switch), une temporaire sinon.
    /// </summary>
    private string _MatchSubjectName(IrMatch match) => match.SwitchId > 0 ? $"rgl_sw{match.SwitchId}" : $"rgl_t{++_temporaryCount}";

    /// <summary>La valeur attrapée par un bras, dans sa variable : <c>const auto&amp; rgl_v_i = …;</c>, ou rien sans nom.</summary>
    private string _ArmBinding(IrMatchArm arm, string subject)
    {
        if (arm.Binding is not { } binding) return "";
        var value = arm.Kind switch
        {
            IrMatchKind.Member => $"rgl::standard::get<{arm.Index}>({subject})",
            // « default x » : le seul membre qui reste, ou l'union de ceux qui restent.
            IrMatchKind.Default when arm.Index >= 0 => $"rgl::standard::get<{arm.Index}>({subject})",
            IrMatchKind.Default => $"rgl::standard::narrow<{_Type(binding.Type)}>({subject})",
            IrMatchKind.Ok => $"{subject}.value()",
            _ => $"{subject}.error()",
        };
        return $"[[maybe_unused]] const auto& {_Declare(binding)} = {value};";
    }

    /// <summary>
    /// Les bras dans l'ordre où ils sont testés : <c>default</c> en dernier, et le dernier bras sans
    /// condition (un bras est toujours choisi : le binder a vérifié que tout est couvert).
    /// </summary>
    private static IReadOnlyList<IrMatchArm> _ArmOrder(IrMatch match) =>
        [.. match.Arms.Where(a => a.Kind != IrMatchKind.Default), .. match.Arms.Where(a => a.Kind == IrMatchKind.Default)];

    /// <summary>
    /// Un match en instruction : sa valeur rangée une fois, puis un <c>if</c> par bras ; un bras bloc
    /// peut quitter le gear (return, fail), puisqu'il n'est pas dans une lambda.
    /// </summary>
    private void _MatchStatement(StringBuilder output, IrMatch match, string indent)
    {
        var subject = _MatchSubjectName(match);
        var inner = indent + Indent;
        _Line(output, match.Span);
        output.Append(indent).Append("{ // match\n");
        // Un bras qui réaffecte la variable lue détruirait la valeur attrapée : on en garde alors une copie.
        var assigned = match.Arms.Where(a => a.Body is not null).SelectMany(a => _AssignedVariables(a.Body!)).ToHashSet();
        var copy = _Descendants(match.Subject).OfType<IrVariableReference>().Any(r => assigned.Contains(r.Variable));
        output.Append(inner).Append(copy ? "const auto " : "const auto& ").Append(subject).Append(" = ").Append(_Expression(match.Subject)).Append(";\n");

        var arms = _ArmOrder(match);
        for (var i = 0; i < arms.Count; ++i)
        {
            var arm = arms[i];
            var test = i == arms.Count - 1 ? null : _ArmTest(arm, subject);
            output.Append(inner).Append(i == 0 ? "" : "} else ").Append(test is null ? "{\n" : $"if ({test}) {{\n");
            var binding = _ArmBinding(arm, subject);
            if (binding.Length > 0) output.Append(inner).Append(Indent).Append(binding).Append('\n');

            if (arm.Body is { } body) _Body(output, body, inner);
            else
            {
                _Line(output, arm.Value!.Span);
                var value = _Expression(arm.Value!);
                output.Append(inner).Append(Indent).Append(arm.Value!.Type == PrimitiveType.Void ? value : $"static_cast<void>({value})").Append(";\n");
            }
        }
        output.Append(inner).Append("}\n");
        output.Append(indent).Append("}\n");
    }

    /// <summary>Les variables affectées ou libérées dans une instruction et ce qu'elle contient.</summary>
    private static IEnumerable<VariableSymbol> _AssignedVariables(IrStatement statement) => statement switch
    {
        IrAssignment assignment => [assignment.Variable],
        IrContentAssignment { Root: IrVariableReference reference } => [reference.Variable],
        IrExpressionStatement { Expression: IrCall call } => IrMutation.MutableArguments(call).OfType<IrVariableReference>().Select(r => r.Variable),
        IrUndefine undefine => [undefine.Variable],
        IrBlock block => block.Statements.SelectMany(_AssignedVariables),
        IrIf ifStatement => [.. _AssignedVariables(ifStatement.Then), .. ifStatement.Else is null ? [] : _AssignedVariables(ifStatement.Else)],
        IrSwitch switchStatement => switchStatement.Sections.SelectMany(s => _AssignedVariables(s.Body)),
        _ => [],
    };

    /// <summary>
    /// Un match qui donne une valeur : une lambda appelée sur place (une expression-instruction dans
    /// un guard, dont un échec dans un bras doit pouvoir sortir), qui renvoie la valeur du bras choisi.
    /// </summary>
    private string _MatchValue(IrMatch match)
    {
        var subject = _MatchSubjectName(match);
        var type = _Type(match.Type);
        var guard = _UseStatementExpressions;
        var result = $"rgl_t{++_temporaryCount}";

        var code = new StringBuilder();
        code.Append("const auto& ").Append(subject).Append(" = ").Append(_Expression(match.Subject)).Append("; ");
        if (guard) code.Append(type).Append(' ').Append(result).Append("{}; ");

        var arms = _ArmOrder(match);
        for (var i = 0; i < arms.Count; ++i)
        {
            var arm = arms[i];
            var test = i == arms.Count - 1 ? null : _ArmTest(arm, subject);
            code.Append(i == 0 ? "" : "else ").Append(test is null ? "{ " : $"if ({test}) {{ ").Append(_ArmBinding(arm, subject)).Append(' ');
            var value = _Expression(arm.Value!);
            code.Append(guard ? $"{result} = {value}; }} " : $"return {value}; }} ");
        }

        if (guard)
        {
            _usesStatementExpressions = true;
            return $"({{ {code}{result}; }})";
        }
        return $"[{(_atFileLevel ? "" : "&")}]() -> {type} {{ {code}}}()";
    }

    /// <summary><c>texte as? int</c> : <c>rgl::standard::try_parse_number&lt;T&gt;(texte)</c>, un nullable.</summary>
    private string _TryParseNumber(IrTryParseNumber parse, string text)
    {
        _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
        _includes.Add("rgl/standard/convert.hpp");
        return $"rgl::standard::try_parse_number<{_Type(parse.Result.Inner)}>({text})";
    }

    /// <summary>Le membre d'union en cours de conversion (<see cref="IrUnionMemberValue"/>).</summary>
    private string? _unionMember;

    /// <summary>
    /// Une union d'erreurs convertie membre par membre : un <c>std::visit</c> dont chaque branche (choisie
    /// par le type du membre) écrit sa conversion.
    /// </summary>
    private string _UnionConvert(IrUnionConvert convert)
    {
        var source = (UnionType)convert.Value.Type;
        var name = $"rgl_t{++_temporaryCount}";
        var type = _Type(convert.Type);
        var before = _unionMember;
        _unionMember = name;

        var code = new StringBuilder();
        for (var i = 0; i < source.Members.Count; i++)
        {
            var branch = _Expression(convert.Branches[i]);
            if (i == source.Members.Count - 1) code.Append($"return {branch};");
            else code.Append($"if constexpr (std::is_same_v<std::decay_t<decltype({name})>, {_Type(source.Members[i])}>) return {branch}; else ");
        }

        _unionMember = before;
        return $"std::visit([&](const auto& {name}) -> {type} {{ {code} }}, {_Expression(convert.Value)})";
    }
}
