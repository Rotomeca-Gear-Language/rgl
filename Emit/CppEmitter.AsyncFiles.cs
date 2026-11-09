using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// <c>readAsync</c> et <c>writeAsync</c> : le travail bloquant — les mêmes fonctions du composant <c>File</c> que <c>raw</c> — est confié au pool de threads du
/// runtime (<c>rgl::standard::run_blocking</c>) ; il travaille sur ses copies du chemin et du texte, et rend le résultat de la promesse : une
/// <c>AsyncException</c> de genre <c>IoError</c> quand le système refuse.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// <c>task f(x)</c> : les arguments sont évalués tout de suite et copiés dans une lambda que le pool exécute ; elle rend le résultat de l'appel, ou rien.
    /// </summary>
    private string _TaskCall(IrTaskCall task, Func<IrExpression, string> operand)
    {
        _includes.Add("rgl/standard/async.hpp");
        var result = _Type(task.Promise.Awaited);
        var captures = new List<string>();
        var names = new Dictionary<IrExpression, string>(ReferenceEqualityComparer.Instance);
        foreach (var argument in task.Call.Arguments.Where(a => a is not IrOmittedArgument))
        {
            var name = $"rgl_t{++_temporaryCount}";
            names[argument] = name;
            captures.Add($"{name} = {operand(argument)}");
        }
        var call = _CallText(task.Call, argument => names.TryGetValue(argument, out var name) ? name : operand(argument));
        var body = task.Call.Type == PrimitiveType.Void ? $"{call}; return {result}{{}};" : $"return {result}::ok({call});";
        return $"rgl::standard::run_blocking<{result}>([{string.Join(", ", captures)}]() -> {result} {{ {body} }})";
    }

    /// <summary><c>whenAny(promesses)</c> : <c>rgl::standard::when_any</c> du résultat des promesses du tableau.</summary>
    private string _WhenAny(IrWhenAny any, Func<IrExpression, string> operand)
    {
        _includes.Add("rgl/standard/async.hpp");
        return $"rgl::standard::when_any<{_Type(any.Promise.Awaited)}>({operand(any.Promises)})";
    }

    /// <summary><c>sleep(durée)</c> : une promesse que la boucle d'événements termine à l'échéance.</summary>
    private string _Sleep(IrSleep sleep, Func<IrExpression, string> operand)
    {
        _includes.Add("rgl/standard/async.hpp");
        return $"rgl::standard::async_sleep<{_Type(sleep.Promise.Awaited)}>({operand(sleep.Duration)})";
    }

    /// <summary>Dans un gear <c>@sync</c> : la boucle tourne jusqu'à la fin de la promesse, dont la valeur est celle de l'expression.</summary>
    private string _BlockOn(IrBlockOn block, Func<IrExpression, string> operand)
    {
        _includes.Add("rgl/standard/async.hpp");
        return $"rgl::standard::block_until({operand(block.Operand)})";
    }

    private string _AsyncFile(IrAsyncFile file, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.File, "rgl/standard/file.hpp");
        _includes.Add("rgl/standard/async.hpp");
        var result = _Type(file.Promise.Awaited);
        var error = (MoldType)file.Promise.Error;
        var kind = (EnumType)error.Symbol["kind"]!.Type;
        var failure = $"{_Type(error)}{{rgl::standard::file::message(status), where, {_Type(kind)}::rgl_c_IoError}}";
        var where = _WhereString(file.Span);

        // Les travaux du même fichier gardent leur ordre (run_file) : une lecture lancée après une écriture la voit.
        if (file.Text is null)
        {
            var isText = file.Promise.Value is PrimitiveType;
            return $"rgl::standard::run_file<{result}>({operand(file.Path)}, [where = {where}](const rgl::standard::string& path) -> {result} {{ {_Type(file.Promise.Value)} content; "
                + $"const auto status = rgl::standard::file::{(isText ? "read_text" : "read_bytes")}(path, content); if (status != rgl::standard::file::status::ok) return {result}::err({failure}); "
                + $"return {result}::ok(std::move(content)); }})";
        }
        // Le texte est évalué avant le chemin : l'ordre d'écriture des arguments.
        var write = file.Text.Type is PrimitiveType ? "write_text" : "write_bytes";
        if (file.Text.Type is not PrimitiveType) _includes.Add("rgl/standard/bytes.hpp");
        return $"rgl::standard::run_file<{result}>({operand(file.Path)}, [text = {operand(file.Text)}, where = {where}](const rgl::standard::string& path) -> {result} {{ "
            + $"const auto status = rgl::standard::file::{write}(path, text); if (status != rgl::standard::file::status::ok) return {result}({failure}); return {result}{{}}; }})";
    }
}
