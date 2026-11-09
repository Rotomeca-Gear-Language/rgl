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
/// Le train d'entrée : sa valeur de départ, ses étapes, les rails de ses guards et ses terminaisons.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// Le train d'entrée : sa valeur de départ, puis son chemin principal ; il renvoie toujours le
    /// code de sortie.
    /// </summary>
    private void _Train(StringBuilder output, IrEntryTrain entry)
    {
        _TrainStart(output, entry.StartValue, entry.Main);
        _pipelineValue = entry.StartValue is null ? null : "rgl_start";
        _Path(output, entry.Main, Indent);
        _pipelineValue = null;
    }

    /// <summary>
    /// Le corps d'un train appelable : comme le train d'entrée, mais ses terminaisons renvoient une valeur ou une
    /// erreur, et un guard sans rail qui échoue fait échouer le train avec son erreur.
    /// </summary>
    private void _TrainBody(StringBuilder output, IrTrainBody body)
    {
        _callableTrain = true;
        _TrainStart(output, body.StartValue, body.Main);
        _pipelineValue = body.StartValue is null ? null : "rgl_start";
        _Path(output, body.Main, Indent);
        _pipelineValue = null;
        _callableTrain = false;
    }

    /// <summary>Vrai pendant l'écriture du corps d'un train appelable : ses échecs sont ceux d'un guard, ses fins renvoient une valeur.</summary>
    private bool _callableTrain;

    /// <summary>La valeur de départ d'un train, rangée dans <c>rgl_start</c>.</summary>
    private void _TrainStart(StringBuilder output, IrExpression? start, IrPath main)
    {
        if (start is null) return;

        // Un intervalle que la première étape parcourt reste une vue : aucun tableau n'est construit.
        var value = start is IrRange range && main.Steps is [{ Value: IrEach }, ..] ? _RangeView(range) : _Expression(start);
        _Line(output, start.Span);
        // Une première étape qui modifie la valeur (paramètre mut) reçoit la copie du train.
        var modified = main.Steps is [{ Entry: null, Call: { Arguments: [IrPipelineValue, ..] } first }, ..] && first.Gear.Parameters[0].IsMutable;
        output.Append(Indent).Append(modified ? "auto rgl_start = " : "[[maybe_unused]] const auto& rgl_start = ").Append(value).Append(";\n");
    }

    /// <summary>
    /// Ce que renvoie un train qui échoue avec <paramref name="error"/>, de type <paramref name="type"/> : le code de sortie 1
    /// pour le train d'entrée, l'échec du guard qu'est le train appelable sinon.
    /// </summary>
    private string _TrainFailure(string error, IrType type) => _callableTrain ? _FailWith(error, type) : "1u";

    /// <summary>
    /// La fin d'un train appelable : il renvoie <paramref name="value"/>, ou rien (<see langword="null"/>). Un guard void réussit ;
    /// un <c>done</c> de guard arrête en plus la boucle qui l'appelle.
    /// </summary>
    private void _Return(StringBuilder output, string indent, string? value, bool isDone)
    {
        var guard = _gear is { Kind: GearKind.Guard };
        if (value is null)
            output.Append(indent).Append(guard ? $"return {_GuardSuccess()};\n" : "return;\n");
        else
            output.Append(indent).Append("return ").Append(guard && isDone ? _Done(value) : value).Append(";\n");
    }

    /// <summary>Un appel de train qui peut échouer, dans une expression : son résultat <c>T ! E</c>.</summary>
    private string _TrainResult(IrCall call, Func<IrExpression, string> operand)
    {
        var type = _Type(call.Type);
        var outcome = $"rgl_t{++_temporaryCount}";
        return $"[{(_atFileLevel ? "" : "&")}]() -> {type} {{ auto {outcome} = {_CallText(call, operand)}; "
            + $"if ({outcome}.failed()) return {type}::err(std::move({outcome}.error())); return {type}::ok(std::move({outcome}.value())); }}()";
    }

    /// <summary>
    /// Les étapes d'un chemin, puis sa terminaison : chaque étape range son résultat dans une
    /// constante, qui entre dans l'étape suivante. Un guard qui échoue entre dans son rail, un bloc
    /// imbriqué qui reçoit son erreur et termine le train ; sans rail, le train échoue (code 1). Un
    /// parcours est une boucle (<see cref="_EachLoop"/>).
    /// </summary>
    /// <param name="output">Où écrire.</param>
    /// <param name="path">Le chemin.</param>
    /// <param name="indent">L'indentation de ses instructions.</param>
    /// <param name="perElement">
    /// Vrai pour le rail d'un élément raté par <c>||&gt;</c>, écrit dans la boucle du parcours : <c>done</c>
    /// et un emit <c>void</c> y passent à l'élément suivant (<c>continue</c>) au lieu de terminer le train.
    /// </param>
    private void _Path(StringBuilder output, IrPath path, string indent, bool perElement = false)
    {
        foreach (var step in path.Steps)
        {
            if (step.Value is IrEach each)
            {
                _Line(output, each.Span);
                _pipelineValue = _EachLoop(output, each, indent, EachContext.Train);
                continue;
            }

            if (step.Value is IrInvoke invocation)
            {
                _Line(output, invocation.Span);
                var invocationText = _Expression(invocation);
                if (invocation.Type == PrimitiveType.Void)
                {
                    output.Append(indent).Append(invocationText).Append(";\n");
                    _pipelineValue = null;
                }
                else
                {
                    var result = $"rgl_t{++_temporaryCount}";
                    output.Append(indent).Append("[[maybe_unused]] auto ").Append(result).Append(" = ").Append(invocationText).Append(";\n");
                    _pipelineValue = result;
                }
                continue;
            }

            var call = step.Call!;
            _Line(output, call.Span);
            // Un paramètre mut reçoit la valeur du train elle-même : une référence, modifiable.
            if (step.Entry is null && call.Arguments is [IrPipelineValue, ..] && call.Gear.Parameters[0].IsMutable)
            {
                var value = $"rgl_t{++_temporaryCount}";
                output.Append(indent).Append("auto&& ").Append(value).Append(" = ").Append(_pipelineValue).Append(";\n");
                _pipelineValue = value;
            }
            var text = step.Entry is { } entry ? _CallWithEntry(output, step, entry, indent) : _Expression(call);
            if (call.Gear.Kind == GearKind.Guard)
            {
                var result = $"rgl_t{++_temporaryCount}";
                output.Append(indent).Append("[[maybe_unused]] auto ").Append(result).Append(" = ").Append(text).Append(";\n");
                if (step.Rail is { } rail)
                {
                    output.Append(indent).Append("if (").Append(_Failed(step, result)).Append(") { // rail de ").Append(call.Gear.Name).Append('\n');
                    _pipelineValue = _StepError(step, result);
                    _Path(output, rail, indent + Indent, perElement);
                    output.Append(indent).Append("}\n");
                }
                else if (step.Errors is { } errors)
                    output.Append(indent).Append("if (").Append(_Failed(step, result)).Append(") return ").Append(_TrainFailure(_StepError(step, result), errors)).Append(";\n");
                _pipelineValue = call.Type == PrimitiveType.Void ? null : $"{result}.value()";
            }
            else if (call.Type == PrimitiveType.Void)
            {
                output.Append(indent).Append(text).Append(";\n");
                _pipelineValue = null;
            }
            else
            {
                var result = $"rgl_t{++_temporaryCount}";
                output.Append(indent).Append("[[maybe_unused]] auto ").Append(result).Append(" = ").Append(text).Append(";\n");
                _pipelineValue = result;

            }
        }

        _Line(output, path.End.Span);
        switch (path.End)
        {
            case IrDone done when _callableTrain && !perElement:
                _Return(output, indent, done.Value is null || done.Value == PrimitiveType.Void ? null : _pipelineValue, isDone: true);
                break;

            case IrDone:
                output.Append(indent).Append(perElement ? "continue;\n" : "return 0u;\n");
                break;

            case IrExit exit:
                output.Append(indent).Append("return ").Append(_Expression(exit.Code)).Append(";\n");
                break;

            // L'emit async qui termine le train : la boucle d'événements tourne jusqu'à sa fin, et sa valeur est le code de sortie.
            case IrEmit { Call.Gear.Async: AsyncMode.Async } emit:
                output.Append(indent).Append("return rgl::standard::run_main(").Append(_Expression(emit.Call)).Append(");\n");
                break;

            case IrEmit emit when emit.Call.Type == PrimitiveType.Void:
                output.Append(indent).Append(_Expression(emit.Call)).Append(";\n");
                if (_callableTrain && !perElement) _Return(output, indent, null, isDone: false);
                else output.Append(indent).Append(perElement ? "continue;\n" : "return 0u;\n");
                break;

            case IrEmit emit:
                output.Append(indent).Append("return ").Append(_Expression(emit.Call)).Append(";\n");
                break;

            case IrResume resume:
                _Resume(output, resume, indent, perElement);
                break;

            case IrFail { Error: { } failure } when _callableTrain:
                output.Append(indent).Append("return ").Append(_FailWith(_Expression(failure), failure.Type)).Append(";\n");
                break;

            case IrFail fail:
                // Le train échoue : seul l'effet de la valeur envoyée compte.
                if (fail.Error is { } error && IrEffects.HasEffects(error))
                    output.Append(indent).Append("static_cast<void>(").Append(_Expression(error)).Append(");\n");
                output.Append(indent).Append("return 1u;\n");
                break;

            default:
                throw new InvalidOperationException($"Terminaison sans traduction : {path.End.GetType().Name}. Ajouter son cas dans CppEmitter.");
        }
    }
}
