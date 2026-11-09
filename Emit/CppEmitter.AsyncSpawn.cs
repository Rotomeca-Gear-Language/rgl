using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// <c>spawnAsync</c> : le même <c>rgl::standard::process::run</c> que <c>spawn</c>, confié au pool de threads du runtime (<c>run_blocking</c>) avec ses
/// options par défaut. Il rend le <c>ProcessResult</c> de la std ; un délai est une <c>AsyncException</c> de genre <c>Timeout</c>, tout autre échec du
/// lancement un genre <c>IoError</c>, de message celui du runtime.
/// </summary>
internal sealed partial class CppEmitter
{
    private string _AsyncSpawn(IrAsyncSpawn spawn, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.Process, "rgl/standard/process.hpp");
        _includes.Add("rgl/standard/async.hpp");

        var result = _Type(spawn.Promise.Awaited);
        var processResult = ((MoldType)((ResultType)spawn.Promise.Awaited).Value).Symbol;
        var error = (MoldType)spawn.Promise.Error;
        var kind = (EnumType)error.Symbol["kind"]!.Type;
        var cppKind = _Type(kind);

        var ok = $"{_Type(new MoldType(processResult))}{{{string.Join(", ", processResult.Fields.Select(f => f.Name switch { "exitCode" => "outcome.exit_code", "stdout" => "outcome.out", "stderr" => "outcome.err", _ => "{}" }))}}}";
        var failure = $"{_Type(error)}{{outcome.message, where, outcome.error == rgl::standard::process::failure::timeout ? {cppKind}::rgl_c_Timeout : {cppKind}::rgl_c_IoError}}";
        var arguments = string.Join(", ", spawn.Arguments.Select(operand));

        return $"rgl::standard::run_blocking<{result}>([program = {operand(spawn.Executable)}, arguments = std::vector<rgl::standard::string>{{{arguments}}}, where = {_WhereString(spawn.Span)}]() -> {result} {{ "
            + $"rgl::standard::process::options options; {_SpawnSetup(spawn.Options, "options", spawn.Span)}"
            + $"const auto outcome = rgl::standard::process::run(program, arguments, options); "
            + $"return outcome.error == rgl::standard::process::failure::none ? {result}::ok({ok}) : {result}::err({failure}); }})";
    }
}
