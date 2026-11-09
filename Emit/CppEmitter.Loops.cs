using System.Text;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les boucles de guard et les points de reprise d'un chemin. Une boucle <c>*</c> est un appel de
/// <c>rgl::standard::repeat</c>, qui rappelle le guard sur chaque valeur qu'il renvoie. Un point de
/// reprise <c>^N</c> est une variable <c>rgl_cpN</c> qui porte la valeur d'entrée de son étape, suivie
/// de l'étiquette <c>rgl_cpN_at</c> ; une reprise remplace la valeur puis y saute (<c>goto</c>). Elle ne
/// revient qu'en arrière, vers un bloc englobant : aucun saut ne franchit une initialisation.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Le C++ de la valeur rangée avant l'étape en cours (<see cref="IR.Expressions.IrStepInput"/>).</summary>
    private string? _stepInput;

    /// <summary>
    /// La valeur d'entrée d'une étape, rangée avant son appel ; renvoie l'appel ou la boucle à écrire.
    /// </summary>
    private string _CallWithEntry(StringBuilder output, IrStep step, IrStepEntry entry, string indent)
    {
        var call = step.Call!;
        var input = _Expression(entry.Value);
        if (entry.Checkpoint is { } checkpoint) input = _Checkpoint(output, checkpoint, input, indent);
        // Le résultat d'une étape n'est plus lu après la suivante : un paramètre mut le reçoit déplacé.
        if (entry.Repeat is { } repeat)
            return _Repeat(call, repeat, entry.Checkpoint is null && call.Gear.Parameters[0].IsMutable && input.StartsWith("rgl_t", StringComparison.Ordinal) ? $"std::move({input})" : input);


        _stepInput = input;
        var text = _Expression(call);
        _stepInput = null;
        return text;
    }

    /// <summary>
    /// Un point de reprise : sa variable, les compteurs de ses reprises limitées, puis son étiquette.
    /// Un passage en avançant les déclare de nouveau, et remet donc les compteurs à zéro ; une reprise
    /// saute après eux.
    /// </summary>
    /// <returns>Le nom de la variable, que l'étape lit.</returns>
    private string _Checkpoint(StringBuilder output, CheckpointSymbol checkpoint, string value, string indent)
    {
        var name = _CheckpointName(checkpoint);
        output.Append(indent).Append("[[maybe_unused]] ").Append(_Type(checkpoint.Type)).Append(' ').Append(name).Append(" = ").Append(value).Append(";\n");
        for (var counter = 0u; counter < checkpoint.LimitedResumes; ++counter)
            output.Append(indent).Append("std::uint32_t ").Append(name).Append("_k").Append(counter).Append(" = 0u;\n");
        output.Append(indent).Append("[[maybe_unused]] ").Append(name).Append("_at:;\n");
        return name;
    }

    /// <summary>
    /// La boucle d'un guard : <c>rgl::standard::repeat&lt;T&gt;(étape, entrée, limite)</c>, l'étape étant
    /// une lambda qui appelle le guard sur la valeur du tour ; une limite nulle n'arrête jamais la boucle.
    /// </summary>
    private string _Repeat(IR.Expressions.IrCall call, IrRepeat repeat, string input)
    {
        _includes.Add("rgl/standard/loop.hpp");
        var type = _Type(call.Gear.ReturnType);
        _stepInput = "rgl_v";
        var body = _Expression(call);
        _stepInput = null;
        // Un paramètre mut reçoit la valeur du tour par valeur : la boucle la lui déplace, il la modifie.
        var parameter = call.Gear.Parameters[0].IsMutable ? $"{type} rgl_v" : $"const {type}& rgl_v";
        return $"rgl::standard::repeat<{type}>([&]({parameter}) {{ return {body}; }}, {input}, {repeat.Limit?.Count ?? 0}u)";
    }

    /// <summary>Le test d'échec du résultat d'une étape : une boucle <c>&lt;!N&gt;</c> échoue aussi sur sa limite.</summary>
    private static string _Failed(IrStep step, string result) =>
        step.Entry?.Repeat?.Limit is { FailsWhenReached: true } ? $"({result}.exhausted() || {result}.failed())" : $"{result}.failed()";

    /// <summary>
    /// L'erreur que l'étape envoie à son rail : celle du guard, ou pour une boucle <c>&lt;!N&gt;</c> qui
    /// atteint sa limite une LoopError, toutes deux dans l'union des erreurs de l'étape.
    /// </summary>
    private string _StepError(IrStep step, string result)
    {
        if (step.Entry?.Repeat is not { Limit: { FailsWhenReached: true } limit } repeat) return $"{result}.error()";

        var errors = step.Errors!;
        var loopError = new MoldType(_loopErrorMold);
        var message = limit.Count == 1 ? "1 tour dépassé" : $"{limit.Count} tours dépassés";
        var exhausted = _IntoErrors($"{_Type(loopError)}{{{_String(new IR.Expressions.IrStringConstant(repeat.Span, PrimitiveType.String, message))}, {_WhereString(repeat.Span)}}}", loopError, errors);
        return step.Call!.Gear.Errors is { } own
            ? $"({result}.exhausted() ? {exhausted} : {_IntoErrors($"{result}.error()", own, errors)})"
            : exhausted;
    }

    /// <summary>
    /// Une reprise : la valeur apportée remplace celle du point, puis le chemin y saute. Une reprise
    /// limitée compte ses passages ; sa limite atteinte, <c>&lt;M&gt;</c> continue par la suite du chemin,
    /// <c>&lt;!M&gt;</c> fait échouer le train.
    /// </summary>
    private void _Resume(StringBuilder output, IrResume resume, string indent, bool perElement)
    {
        var name = _CheckpointName(resume.Target);
        var jump = $"{name} = {_Expression(resume.Value)}; goto {name}_at;\n";
        if (resume.Limit is not { } limit)
        {
            output.Append(indent).Append(jump);
            return;
        }

        var counter = $"{name}_k{resume.Counter}";
        output.Append(indent).Append("if (").Append(counter).Append(" < ").Append(limit.Count).Append("u) {\n");
        output.Append(indent).Append(Indent).Append("++").Append(counter).Append(";\n");
        output.Append(indent).Append(Indent).Append(jump);
        output.Append(indent).Append("}\n");
        if (resume.Fallback is { } fallback) _Path(output, fallback, indent, perElement);
        else if (_callableTrain)
        {
            var loopError = new MoldType(_loopErrorMold);
            var message = limit.Count == 1 ? "1 reprise dépassée" : $"{limit.Count} reprises dépassées";
            var value = $"{_Type(loopError)}{{{_String(new IR.Expressions.IrStringConstant(resume.Span, PrimitiveType.String, message))}, {_WhereString(resume.Span)}}}";
            output.Append(indent).Append("return ").Append(_FailWith(value, loopError)).Append(";\n");
        }
        else output.Append(indent).Append("return 1u;\n");
    }

    /// <summary>Le nom C++ d'un point de reprise : <c>rgl_cp1</c>.</summary>
    private static string _CheckpointName(CheckpointSymbol checkpoint) => $"rgl_cp{checkpoint.Number}";
}
