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
/// Les instructions : déclarations, affectations, blocs, if, switch, undefine, écriture sur la
/// console, et la directive #line qui précède chacune.
/// </summary>
internal sealed partial class CppEmitter
{
    private void _Statement(StringBuilder output, IrStatement statement, string indent)
    {
        switch (statement)
        {
            case IrEntryTrain train:
                _Train(output, train);
                break;

            case IrTrainBody body:
                _TrainBody(output, body);
                break;

            case IrVariableDeclaration declaration when _TryTable(output, declaration, indent):
                break;

            case IrVariableDeclaration declaration:
                var variable = declaration.Variable;
                // Au niveau du fichier (indentation vide), une constante est static : propre à ce fichier C++.
                var qualifier = (indent.Length == 0 ? "static " : "") + (variable.IsCompileTime ? "constexpr " : variable.IsConstant ? "const " : "");
                _Line(output, declaration.Span);
                output.Append(indent).Append("[[maybe_unused]] ").Append(qualifier).Append(_Type(variable.Type)).Append(' ')
                    .Append(_Declare(variable)).Append(" = ").Append(_Consumed(declaration.Value)).Append(";\n");
                break;

            case IrAssignment assignment:
                _Line(output, assignment.Span);
                output.Append(indent).Append(_variableNames[assignment.Variable]).Append(" = ").Append(_Consumed(assignment.Value)).Append(";\n");
                break;

            case IrContentAssignment assignment:
                _ContentAssignment(output, assignment, indent);
                break;

            case IrUndefine undefine:
                _Undefine(output, undefine, indent);
                break;

            case IrIf ifStatement:
                _If(output, ifStatement, indent);
                break;

            case IrSwitch switchStatement:
                _Switch(output, switchStatement, indent);
                break;

            case IrBreak breakStatement:
                _Line(output, breakStatement.Span);
                output.Append(indent).Append("goto rgl_sw").Append(breakStatement.SwitchId).Append("_end;\n");
                break;

            case IrGoto gotoStatement:
                _Line(output, gotoStatement.Span);
                output.Append(indent).Append("goto rgl_sw").Append(gotoStatement.SwitchId).Append("_c").Append(gotoStatement.SectionIndex).Append(";\n");
                break;

            case IrReturn returnStatement:
                _Line(output, returnStatement.Span);
                if (_gear is { } looping && _tailGears.Contains(looping) && TailCalls.IsTailCall(returnStatement, looping))
                    _TailCall(output, (IrCall)returnStatement.Value!, indent);
                else if (returnStatement.StopsFold)
                    output.Append(indent).Append("{ ").Append(_foldStop ?? throw new InvalidOperationException("done valeur hors d'une accumulation.")).Append(" = true; return ").Append(_Expression(returnStatement.Value!)).Append("; }\n");
                else if (returnStatement.IsDone) output.Append(indent).Append("return ").Append(_Done(_Detached(returnStatement.Value!, _Expression(returnStatement.Value!)))).Append(";\n");
                // Une coroutine termine sa promesse par co_return : sa valeur, ou « rien » (tout s'est bien passé) pour un gear sans valeur.
                else if (returnStatement.Value is not null) output.Append(indent).Append(_InCoroutine ? "co_return " : "return ").Append(_Returned(returnStatement.Value)).Append(";\n");
                else output.Append(indent).Append(_InGuard ? $"return {_GuardSuccess()};\n" : _InCoroutine ? "co_return {};\n" : "return;\n");
                break;

            case IrFailStatement fail:
                // Le guard s'arrête : son résultat porte l'erreur, dont where reçoit la position du fail.
                _Line(output, fail.Span);
                var error = $"rgl_t{++_temporaryCount}";
                output.Append(indent).Append("{\n");
                output.Append(indent).Append(Indent).Append("auto ").Append(error).Append(" = ").Append(_Detached(fail.Error, _Expression(fail.Error))).Append(";\n");
                output.Append(indent).Append(Indent).Append(error).Append(".rgl_f_where = ").Append(_WhereString(fail.Span)).Append(";\n");
                output.Append(indent).Append(Indent).Append("return ").Append(_FailWith(error, fail.Error.Type)).Append(";\n");
                output.Append(indent).Append("}\n");
                break;

            case IrPanic panic:
                // L'arrêt fatal : le texte et la position partent sur stderr, la console est vidée, code 101.
                _Line(output, panic.Span);
                _includes.Add("rgl/standard/panic.hpp");
                if (_Representation(panic.Value.Type) is not PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString })
                {
                    _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
                    _includes.Add("rgl/standard/format.hpp");
                    _MarkWritten(panic.Value.Type);
                    var failure = $"rgl_t{++_temporaryCount}";
                    output.Append(indent).Append("{\n");
                    output.Append(indent).Append(Indent).Append("auto ").Append(failure).Append(" = ").Append(_Expression(panic.Value)).Append(";\n");
                    output.Append(indent).Append(Indent).Append(failure).Append(".rgl_f_where = ").Append(_WhereString(panic.Span)).Append(";\n");
                    output.Append(indent).Append(Indent).Append("rgl::standard::panic(rgl::standard::interpolate(").Append(failure).Append("), nullptr);\n");
                    output.Append(indent).Append("}\n");
                }
                else output.Append(indent).Append("rgl::standard::panic(").Append(_Expression(panic.Value)).Append(", ").Append(_Where(panic.Span)).Append(");\n");
                break;

            case IrWrite write:
                _Line(output, write.Span);
                output.Append(indent).Append(_Write(write.Value)).Append(";\n");
                break;

            case IrWriteXml write:
                _Line(output, write.Span);
                output.Append(indent).Append(_WriteXml(write)).Append("\n");
                break;

            case IrWriteJson write:
                _Line(output, write.Span);
                output.Append(indent).Append(_WriteJson(write)).Append("\n");
                break;

            case IrWriteFile write:
                _Line(output, write.Span);
                output.Append(indent).Append(_WriteFile(write)).Append("\n");
                break;

            case IrExpressionStatement { Expression: IrMatch match }:
                _MatchStatement(output, match, indent);
                break;

            case IrExpressionStatement expression:
                _Line(output, expression.Span);
                output.Append(indent).Append(_Expression(expression.Expression)).Append(";\n");
                break;

            case IrBlock block:
                output.Append(indent).Append("{\n");
                if (block.IsArena)
                {
                    // L'arène est l'allocateur ambiant jusqu'à l'accolade fermante : ses locaux sont détruits avant elle, elle se vide ensuite.
                    _Use(RuntimeComponent.Memory, "rgl/standard/arena.hpp");
                    output.Append(indent).Append(Indent).Append("rgl::standard::arena_scope rgl_arena").Append(++_temporaryCount).Append('(').Append(block.FirstSegment).Append("u);\n");
                    ++_arenaDepth;
                }
                foreach (var inner in LoopFusion.Fuse(block.Statements, _AddNote)) _Statement(output, inner, indent + Indent);
                if (block.IsArena) --_arenaDepth;
                output.Append(indent).Append("}\n");
                break;

            default:
                throw new InvalidOperationException($"Instruction sans traduction : {statement.GetType().Name}. Ajouter son cas dans CppEmitter.");
        }
    }

    /// <summary>
    /// Une valeur recopiée dans une variable ou un tableau : la variable elle-même est déplacée
    /// (<c>std::move</c>) quand c'est son dernier usage, sans copie.
    /// </summary>
    private string _Consumed(IrExpression value) =>
        value is IrVariableReference reference && _moves.Contains(reference) ? $"std::move({_variableNames[reference.Variable]})" : _Expression(value);

    /// <summary>Les instructions d'un bloc, sans ses accolades (écrites par l'appelant), un cran plus loin.</summary>
    private void _Body(StringBuilder output, IrBlock block, string indent)
    {
        foreach (var statement in LoopFusion.Fuse(block.Statements, _AddNote)) _Statement(output, statement, indent + Indent);
    }

    /// <summary>
    /// La condition d'un <c>if</c> C++, entre parenthèses. Une comparaison ou une opération logique
    /// est déjà entièrement parenthésée : on n'en ajoute pas, car <c>if ((a == b))</c> fait avertir
    /// Clang (<c>-Wparentheses-equality</c>).
    /// </summary>
    private string _Condition(IrExpression condition)
    {
        var code = _Expression(condition);
        var wrapped = condition is IrBinary or IrUnary { Operator: IrUnaryOperator.Not } && !_NeedsSequencing(condition);
        return wrapped ? code : $"({code})";
    }

    /// <summary><c>if (…) { … } else { … }</c> ; un <c>else if</c> devient un <c>if</c> dans le bloc <c>else</c>.</summary>
    private void _If(StringBuilder output, IrIf ifStatement, string indent)
    {
        _Line(output, ifStatement.Condition.Span);
        output.Append(indent).Append("if ").Append(_Condition(ifStatement.Condition)).Append(" {\n");
        _Body(output, ifStatement.Then, indent);

        switch (ifStatement.Else)
        {
            case null:
                output.Append(indent).Append("}\n");
                break;

            case IrBlock block:
                output.Append(indent).Append("} else {\n");
                _Body(output, block, indent);
                output.Append(indent).Append("}\n");
                break;

            default:
                output.Append(indent).Append("} else {\n");
                _Statement(output, ifStatement.Else, indent + Indent);
                output.Append(indent).Append("}\n");
                break;
        }
    }

    /// <summary>
    /// Un switch, pour tous les types : sa valeur est rangée une fois, puis chaque test, dans
    /// l'ordre (default en dernier), saute à l'étiquette de son cas. Les cas se suivent, ce qui
    /// donne la poursuite sans <c>break</c> ; <c>break</c> saute à l'étiquette de fin et
    /// <c>goto v</c> à celle du cas visé. Chaque cas est un bloc : aucun saut ne passe par-dessus
    /// l'initialisation d'une variable.
    /// </summary>
    private void _Switch(StringBuilder output, IrSwitch switchStatement, string indent)
    {
        var label = $"rgl_sw{switchStatement.Id}";
        var inner = indent + Indent;

        output.Append(indent).Append("{ // switch\n");
        _Line(output, switchStatement.Subject.Span);
        output.Append(inner).Append("[[maybe_unused]] const auto& ").Append(label).Append(" = ").Append(_Expression(switchStatement.Subject)).Append(";\n");

        var fallback = $"{label}_end";
        for (var i = 0; i < switchStatement.Sections.Count; ++i)
        {
            if (switchStatement.Sections[i].Test is { } test)
            {
                _Line(output, test.Span);
                output.Append(inner).Append("if ").Append(_Condition(test)).Append(" goto ").Append(label).Append("_c").Append(i).Append(";\n");
            }
            else fallback = $"{label}_c{i}";
        }
        output.Append(inner).Append("goto ").Append(fallback).Append(";\n");

        for (var i = 0; i < switchStatement.Sections.Count; ++i)
        {
            output.Append(inner).Append("[[maybe_unused]] ").Append(label).Append("_c").Append(i).Append(": {\n");
            _Body(output, switchStatement.Sections[i].Body, inner);
            output.Append(inner).Append("}\n");
        }

        output.Append(inner).Append("[[maybe_unused]] ").Append(label).Append("_end: ;\n");
        output.Append(indent).Append("}\n");
    }

    /// <summary>La valeur d'un <c>return</c> : une variable locale à cycle de vie se déplace dans le résultat d'un guard ou d'une coroutine (un gear ordinaire la rend, Clang la déplace de lui-même), toute autre valeur s'écrit comme elle est.</summary>
    private string _Returned(IrExpression value) =>
        value is IrVariableReference reference && (_InGuard || _InCoroutine) && LifecycleRules.Has(value.Type) ? $"std::move({_variableNames[reference.Variable]})" : _Detached(value, _Expression(value));

    /// <summary>
    /// <c>undefine</c> : la variable n'est plus jamais utilisée ensuite. Une variable qui tient de la
    /// mémoire (chaîne, tableau) est remplacée par une valeur vide, ce qui la libère tout de suite ;
    /// un nombre ou un bool n'a rien à libérer, pas plus qu'une constante, qui reste en place
    /// jusqu'à la fin de son bloc.
    /// </summary>
    private void _Undefine(StringBuilder output, IrUndefine undefine, string indent)
    {
        var variable = undefine.Variable;
        // Un [T;N], un [T;N..M] ou un [T+] n'a pas de valeur vide : il garde ses éléments jusqu'à la fin du bloc.
        var holdsMemory = _HasEmptyValue(variable.Type) && _Representation(variable.Type) is ArrayType or DictionaryType or MoldType or NullableType or UnionType or ResultType or PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString or PrimitiveKind.FileStream };

        _Line(output, undefine.Span);
        // Une valeur à cycle de vie est déplacée dans un temporaire, qui la ferme tout de suite en disparaissant.
        if (variable.Type is PointerType)
            output.Append(indent).Append(_variableNames[variable]).Append(".free();\n");
        else if (LifecycleRules.Has(variable.Type) && !variable.IsConstant)
            output.Append(indent).Append("{ auto rgl_closed = std::move(").Append(_variableNames[variable]).Append("); }\n");
        else if (holdsMemory && !variable.IsConstant)
            output.Append(indent).Append(_variableNames[variable]).Append(" = ").Append(_Type(variable.Type)).Append("();\n");
        else
            output.Append(indent).Append("// undefine ").Append(variable.Name).Append(" : rien à libérer\n");
    }

    /// <summary>
    /// L'écriture d'une valeur sur la console : une chaîne telle quelle, toute autre valeur d'abord
    /// écrite en texte comme dans un template.
    /// </summary>
    private string _Write(IrExpression value)
    {
        _Use(RuntimeComponent.Console, "rgl/standard/console.hpp");
        _MarkWritten(value.Type);
        var text = _Expression(value);
        if (_Representation(value.Type) is not PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString })
        {
            _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
            _includes.Add("rgl/standard/format.hpp");
            text = $"rgl::standard::interpolate({text})";
        }
        return $"rgl::standard::console::write({text})";
    }

    /// <summary>Directive <c>#line</c> vers la ligne de <paramref name="span"/> dans le fichier RGL.</summary>
    private void _Line(StringBuilder output, SourceSpan span) =>
        output.Append("#line ").Append(span.Source.OriginalLine(span.StartPosition.Line).ToString()).Append(' ').Append(_FileOf(span)).Append('\n');

    /// <summary>Le chemin du fichier d'un élément, en littéral C++ : les éléments d'un fichier importé pointent vers lui.</summary>
    private string _FileOf(SourceSpan span)
    {
        var path = span.Source.Path;
        if (!_quotedFiles.TryGetValue(path, out var quoted)) _quotedFiles[path] = quoted = path == _sourcePath ? _file : _Quote(path);
        return quoted;
    }

    /// <summary>Les chemins de fichiers déjà écrits en littéral C++.</summary>
    private readonly Dictionary<string, string> _quotedFiles = new(StringComparer.Ordinal);
}
