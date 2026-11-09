using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// <c>spawn</c> : <c>rgl::standard::process::run</c> (process.hpp) rend un <c>outcome</c> que le code généré met dans le résultat RGL — le
/// <c>ProcessResult</c> de la std, ou son <c>SpawnError</c> avec la variante de <c>SpawnFailure</c> qui correspond à l'échec.
/// </summary>
internal sealed partial class CppEmitter
{
    private string _Spawn(IrSpawn spawn, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.Process, "rgl/standard/process.hpp");
        _usesStatementExpressions = true;

        var result = (ResultType)spawn.Type;
        var processResult = ((MoldType)result.Value).Symbol;
        var spawnError = ((MoldType)result.Error).Symbol;
        var failure = (EnumType)spawnError["kind"]!.Type;
        var resultType = _Type(spawn.Type);

        var (options, outcome) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var setup = _SpawnSetup(spawn.Options, options, spawn.Span);

        var arguments = string.Join(", ", spawn.Arguments.Select(operand));
        var cppFailure = _Type(failure);
        string Kind(string runtime, string variant) => $"{outcome}.error == rgl::standard::process::failure::{runtime} ? {cppFailure}::rgl_c_{variant} : ";
        var kind = $"({Kind("exec_not_found", "ExecNotFound")}{Kind("permission_denied", "PermissionDenied")}{Kind("timeout", "Timeout")}{cppFailure}::rgl_c_IoError)";

        var okValue = $"{_Type(new MoldType(processResult))}{{{string.Join(", ", processResult.Fields.Select(f => f.Name switch { "exitCode" => $"{outcome}.exit_code", "stdout" => $"{outcome}.out", "stderr" => $"{outcome}.err", _ => "{}" }))}}}";
        var errValue = $"{_Type(new MoldType(spawnError))}{{{string.Join(", ", spawnError.Fields.Select(f => f.Name switch { "kind" => kind, "message" => $"{outcome}.message", _ => "{}" }))}}}";

        return $"({{ rgl::standard::process::options {options}; {setup}"
            + $"const auto {outcome} = rgl::standard::process::run({operand(spawn.Executable)}, std::vector<rgl::standard::string>{{{arguments}}}, {options}); "
            + $"{outcome}.error == rgl::standard::process::failure::none ? {resultType}::ok({okValue}) : {resultType}::err({errValue}); }})";
    }

    /// <summary>Les instructions C++ qui règlent <paramref name="options"/> (une <c>process::options</c>) sur celles d'un <c>spawn</c> ; rien pour les options par défaut.</summary>
    private string _SpawnSetup(SpawnOptions o, string options, Rgl.Text.SourceSpan span)
    {
        var setup = new System.Text.StringBuilder();
        if (o.TimeoutMs != 0) setup.Append($"{options}.timeout_ms = {o.TimeoutMs}u; ");
        if (o.WorkingDirectory is { } cwd) setup.Append($"{options}.cwd = {_String(new IrStringConstant(span, PrimitiveType.String, cwd))}; ");
        foreach (var (name, value) in o.Environment)
            setup.Append($"{options}.env.emplace_back({_String(new IrStringConstant(span, PrimitiveType.String, name))}, {_String(new IrStringConstant(span, PrimitiveType.String, value))}); ");
        if (o.Input == SpawnInput.None) setup.Append($"{options}.in = rgl::standard::process::input::none; ");
        if (o.Output != SpawnOutput.Pipe) setup.Append($"{options}.out = rgl::standard::process::output::{_OutputName(o.Output)}; ");
        if (o.Error != SpawnOutput.Pipe) setup.Append($"{options}.err = rgl::standard::process::output::{_OutputName(o.Error)}; ");
        return setup.ToString();
    }

    private static string _OutputName(SpawnOutput output) => output switch
    {
        SpawnOutput.Inherit => "inherit",
        SpawnOutput.Null => "null",
        _ => "pipe",
    };
}
