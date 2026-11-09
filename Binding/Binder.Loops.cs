using System.Numerics;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Steps;
using Rgl.AST.Train;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Les boucles de guard (<c>*</c>, <c>*&lt;N&gt;</c>, <c>*&lt;!N&gt;</c>) et les points de reprise
/// (<c>^N</c> devant une étape, reprises <c>^N</c>, <c>^N&lt;M&gt;</c>, <c>^N&lt;!M&gt;</c>) : seulement
/// comme étapes d'un chemin. Un point est unique dans le train ; une reprise n'y revient qu'en
/// arrière, d'où qu'elle soit : depuis le même chemin, ou depuis un rail qui en part après lui.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Le premier point de reprise de chaque numéro du train, rails compris.</summary>
    private readonly Dictionary<uint, Checkpoint> _trainCheckpoints = [];

    /// <summary>Les points de reprise déjà passés par le chemin en cours de liaison : ceux qu'une reprise peut viser.</summary>
    private Dictionary<uint, CheckpointSymbol> _passedCheckpoints = [];

    /// <summary>
    /// Relève les points de reprise du train, rails compris, avant de le lier. Une reprise vers un
    /// point relevé mais pas encore passé vise ainsi un point en avant.
    /// </summary>
    private void _DeclareCheckpoints(IReadOnlyList<IStep> steps)
    {
        foreach (var step in steps)
        {
            var (checkpoint, rail) = step switch
            {
                CallStep call => (call.Checkpoint, call.Rail),
                EachStep each => (null, each.Rail),
                _ => (null, null),
            };

            if (checkpoint is not null) _DeclareCheckpoint(checkpoint);
            if (rail is not null) _DeclareCheckpoints(rail.Steps);
        }
    }

    /// <summary>Relève un point de reprise : un numéro déjà posé dans le train, ou trop grand, est E097.</summary>
    private void _DeclareCheckpoint(Checkpoint checkpoint)
    {
        if (_CheckpointNumber(checkpoint.Number) is not { } number)
            _Error(checkpoint.Number.Span, DiagnosticCodes.InvalidCheckpoint, $"numéro de point de reprise trop grand : {checkpoint.Number.Text}", "de 0 à 4294967295");
        else if (!_trainCheckpoints.TryAdd(number, checkpoint))
            _Error(checkpoint.Span, DiagnosticCodes.InvalidCheckpoint, $"le point de reprise « ^{number} » est déjà posé dans ce train", "déjà posé",
                $"premier point « ^{number} » : ligne {_trainCheckpoints[number].Span.StartPosition.Line}",
                "chaque point de reprise a son propre numéro dans le train, rails compris");
    }

    /// <summary>
    /// La valeur d'entrée d'une étape qui porte un point de reprise ou une boucle de guard : rangée
    /// avant l'appel, qui la lit par un <see cref="IrStepInput"/>. Le point devient visible des
    /// reprises qui suivent, rail de l'étape compris.
    /// </summary>
    /// <param name="step">L'étape écrite.</param>
    /// <param name="call">Son appel lié, valeur d'entrée en premier argument.</param>
    /// <returns>L'appel qui lit la valeur rangée, et l'entrée ; <see langword="null"/> après une erreur signalée.</returns>
    private (IrCall Call, IrStepEntry Entry)? _BindStepEntry(CallStep step, IrCall call)
    {
        IrRepeat? repeat = null;
        if (step.Repeat is { } loop && (repeat = _BindRepeat(loop, call)) is null) return null;

        CheckpointSymbol? checkpoint = null;
        if (step.Checkpoint is { } point && (checkpoint = _BindCheckpoint(point, call)) is null) return null;

        var input = call.Arguments[0];
        var reading = call with { Arguments = [new IrStepInput(input.Span, input.Type), .. call.Arguments.Skip(1)] };
        return (reading, new IrStepEntry(input, checkpoint, repeat));
    }

    /// <summary>
    /// La boucle d'un guard : seul un guard boucle (E095), et sa valeur revient à sa première entrée,
    /// il renvoie donc son type (E096). Avec <c>&lt;!N&gt;</c>, l'étape peut aussi échouer par une LoopError.
    /// </summary>
    private IrRepeat? _BindRepeat(Repeat written, IrCall call)
    {
        var gear = call.Gear;
        if (gear.Kind != GearKind.Guard)
        {
            _Error(written.Span, DiagnosticCodes.RepeatNotGuard, $"seul un guard peut boucler : « {gear.Name} » est un {_KindName(gear)}", "pas un guard",
                "un guard sort de sa boucle par done ou par fail ; un gear ne sait faire ni l'un ni l'autre, rien n'arrêterait la boucle",
                $"pour répéter « {gear.Name} », appelle-le depuis un guard qui termine par done");
            return null;
        }

        if (gear.Parameters is not [var first, ..] || first.Type != gear.ReturnType)
        {
            var received = gear.Parameters.Count == 0 ? "il n'a aucun paramètre" : $"son premier paramètre « {gear.Parameters[0].Name} » est un « {gear.Parameters[0].Type} »";
            _Error(written.Span, DiagnosticCodes.RepeatTypeMismatch, $"« {gear.Name} » ne peut pas boucler : {received} et il renvoie un « {gear.ReturnType} »", "type différent",
                "à chaque tour, la valeur renvoyée par le guard revient à sa première entrée : il renvoie donc le même type qu'elle");
            return null;
        }

        IrLimit? limit = null;
        if (written.Limit is { } bound && (limit = _BindLimit(bound)) is null) return null;

        var errors = limit is { FailsWhenReached: true }
            ? UnionType.Of(gear.Errors is { } own ? [own, new MoldType(_loopError)] : [new MoldType(_loopError)])
            : gear.Errors;
        return new IrRepeat(written.Span, limit, errors);
    }

    /// <summary>
    /// Un point de reprise : devant un gear ou un guard à un seul paramètre (E097), la valeur de
    /// l'étape étant celle qu'une reprise y apporte. Un numéro en double est déjà signalé.
    /// </summary>
    private CheckpointSymbol? _BindCheckpoint(Checkpoint written, IrCall call)
    {
        var gear = call.Gear;
        if (gear.Kind == GearKind.Emit || gear.Parameters.Count != 1)
        {
            var received = gear.Kind == GearKind.Emit ? $"« {gear.Name} » est un emit, qui termine le chemin" : $"« {gear.Name} » a {gear.Parameters.Count} paramètres";
            _Error(written.Span, DiagnosticCodes.InvalidCheckpoint, $"un point de reprise se pose devant un gear ou un guard à un seul paramètre : {received}", "mal placé",
                "une reprise y revient avec une seule valeur, qui entre dans l'étape");
            return null;
        }

        if (_CheckpointNumber(written.Number) is not { } number || !ReferenceEquals(_trainCheckpoints[number], written)) return null;

        if (LifecycleRules.Has(gear.Parameters[0].Type))
        {
            _Error(written.Span, DiagnosticCodes.LifecycleUnsupported, $"« {gear.Parameters[0].Type} » a un cycle de vie : un point de reprise en garderait une copie", "pas encore pris en charge",
                "pose le point de reprise devant une étape qui reçoit autre chose qu'une valeur à cycle de vie");
            return null;
        }

        var symbol = new CheckpointSymbol(number, gear.Parameters[0].Type, written.Span);
        _passedCheckpoints[number] = symbol;
        return symbol;
    }

    /// <summary>
    /// Une reprise : vers un point déjà passé (E098), avec la valeur courante, du type du point. Une
    /// reprise sans limite ou <c>&lt;!M&gt;</c> termine le chemin ; <c>&lt;M&gt;</c> continue par
    /// <paramref name="rest"/> une fois sa limite atteinte.
    /// </summary>
    /// <param name="resume">La reprise écrite.</param>
    /// <param name="current">Le type de la valeur courante ; <see langword="null"/> sans valeur.</param>
    /// <param name="rest">Les étapes qui la suivent : la suite d'une reprise <c>&lt;M&gt;</c>.</param>
    /// <param name="span">Le chemin dans le source.</param>
    /// <param name="what">« le train » ou « le rail », pour les messages.</param>
    /// <returns>La reprise, ou <see langword="null"/> après une erreur signalée.</returns>
    private IrResume? _BindResume(ResumeStep resume, IrType? current, IReadOnlyList<IStep> rest, SourceSpan span, string what)
    {
        if (_CheckpointNumber(resume.Number) is not { } number) return null;
        if (!_passedCheckpoints.TryGetValue(number, out var target))
        {
            if (_trainCheckpoints.ContainsKey(number))
                _Error(resume.Span, DiagnosticCodes.UnknownCheckpoint, $"le point de reprise « ^{number} » n'est pas encore passé ici", "point en avant",
                    "une reprise revient en arrière : vers un point plus tôt sur son chemin, ou sur le chemin dont part son rail");
            else
                _Error(resume.Span, DiagnosticCodes.UnknownCheckpoint, $"aucun point de reprise « ^{number} » dans ce train", "point inconnu",
                    $"pose le point devant l'étape où revenir, par exemple : |> ^{number} lire !> [reparer |> ^{number}<3>]");
            return null;
        }

        if (current is null)
        {
            _Error(resume.Span, DiagnosticCodes.TypeMismatch, $"« ^{number} » reprend avec la valeur de l'étape précédente, et il n'y en a pas", "rien à reprendre",
                $"une reprise suit une étape qui renvoie un « {target.Type} »");
            return null;
        }

        var value = _ConvertTo(new IrPipelineValue(resume.Span, current), target.Type, $"le point « ^{number} » reçoit un « {target.Type} »");
        if (value is null) return null;

        IrLimit? limit = null;
        if (resume.Limit is { } bound && (limit = _BindLimit(bound)) is null) return null;

        IrPath? fallback = null;
        if (limit is { FailsWhenReached: false } && (fallback = _BindPath(span, rest, what, current)) is null) return null;

        var counter = limit is null ? 0u : target.LimitedResumes++;
        return new IrResume(resume.Span, target, value, limit, counter, fallback);
    }

    /// <summary>Une limite de tours ou de reprises : un entier de 1 à 4294967295 (E099).</summary>
    private IrLimit? _BindLimit(RepeatLimit written)
    {
        var count = LiteralDecoder.Integer(written.Count.Text);
        if (count >= BigInteger.One && count <= uint.MaxValue) return new IrLimit((uint)count, written.FailsWhenReached);

        _Error(written.Count.Span, DiagnosticCodes.InvalidRepeatLimit, $"limite invalide : {written.Count.Text}", "de 1 à 4294967295",
            "la limite compte les tours ou les reprises permis : au moins 1");
        return null;
    }

    /// <summary>Le numéro d'un point de reprise ; <see langword="null"/> s'il dépasse 4294967295 (signalé au relevé des points).</summary>
    private static uint? _CheckpointNumber(LiteralExpression number) =>
        LiteralDecoder.Integer(number.Text) is var value && value <= uint.MaxValue ? (uint)value : null;
}
