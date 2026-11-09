using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Le mode sûr (clé <c>safe</c> de <c>rgl.json</c>) : un train ne cache aucune erreur. Chaque pas d'un train ou d'un rail qui peut
/// échouer a un rail qui traite ses erreurs ; faire échouer le train s'écrit <c>|&gt; fail</c>. Les erreurs sont celles que
/// l'analyse des guards calcule déjà (<c>GearSymbol.Errors</c>) : le mode sûr ne change ni la syntaxe, ni le C++ généré, il
/// refuse des programmes que le mode libre accepte en silence.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Les vérifications des modes du projet sur un train lié : le mode sûr (<c>safe</c>) et les boucles bornées (<c>loop</c>).</summary>
    private void _CheckTrainMode(IrPath main)
    {
        if (_safe)
        {
            ++_safeTrains;
            _CheckSafePath(main, inRail: false);
        }
        if (_boundedLoops) _CheckBoundedLoops(main);
    }

    /// <summary>
    /// Boucles bornées : une boucle de guard (<c>g*</c>) ou une reprise (<c>^N</c>) sans limite peut tourner sans fin. Elles s'écrivent
    /// avec une limite : <c>g*&lt;10&gt;</c>, <c>g*&lt;!10&gt;</c>, <c>^1&lt;3&gt;</c>, <c>^1&lt;!3&gt;</c>.
    /// </summary>
    private void _CheckBoundedLoops(IrPath path)
    {
        foreach (var step in path.Steps)
        {
            if (step.Value is IrEach each)
            {
                foreach (var stage in each.Stages.Where(s => s.Rail is not null)) _CheckBoundedLoops(stage.Rail!);
                if (each.Fold?.Rail is { } foldRail) _CheckBoundedLoops(foldRail);
                continue;
            }

            if (step.Entry?.Repeat is { Limit: null } repeat)
                _Error(repeat.Span, DiagnosticCodes.UnboundedLoop, "cette boucle de guard n'a pas de limite : elle peut tourner sans fin", "sans limite",
                    "écris une limite : g*<10> continue avec la dernière valeur, g*<!10> échoue au dixième tour (la clé « loop » de rgl.json vaut « safe »)");
            if (step.Rail is { } rail) _CheckBoundedLoops(rail);
        }

        if (path.End is IrResume resume)
        {
            if (resume.Limit is null)
                _Error(resume.Span, DiagnosticCodes.UnboundedLoop, "cette reprise n'a pas de limite : elle peut rejouer l'étape sans fin", "sans limite",
                    "écris une limite : ^1<3> continue après trois reprises, ^1<!3> échoue (la clé « loop » de rgl.json vaut « safe »)");
            if (resume.Fallback is { } fallback) _CheckBoundedLoops(fallback);
        }
    }

    /// <summary>
    /// Vérifie un chemin lié et ses rails.
    /// </summary>
    /// <param name="path">Le chemin d'un train, d'un rail ou d'une reprise.</param>
    /// <param name="inRail">Vrai pour un chemin qui est (ou suit) un rail : une reprise bornée y est refusée.</param>
    private void _CheckSafePath(IrPath path, bool inRail)
    {
        foreach (var step in path.Steps)
        {
            if (step.Value is IrEach each)
            {
                foreach (var stage in each.Stages)
                {
                    if (stage.Guard is { } stageGuard) _CheckNoFire(stage.Span, stageGuard);
                    if (stage.Rail is { } stageRail) _CheckSafeRail(stageRail);
                    else if (stage.Guard?.Errors is { } stageErrors)
                        _UnhandledError(stage.Span, stage.Guard.Name, stageErrors, stage.IsStrict
                            ? "ajoute un rail à ce parcours : |!|> g !> [traiter |> done]"
                            : "un élément qui échoue est écarté sans rien dire : ajoute un rail au parcours, par exemple ||> g !> [signaler]");
                }
                if (each.Fold is { } fold)
                {
                    if (fold.Rail is { } foldRail) _CheckSafeRail(foldRail);
                    else if (fold.Guard?.Errors is { } foldErrors)
                        _UnhandledError(fold.Span, fold.Guard.Name, foldErrors, "ajoute un rail à l'accumulation : |+> départ with g !> [traiter |> done]");
                }
                continue;
            }

            if (step.Call is { } called) _CheckNoFire(step.Value.Span, called.Gear);
            if (step.Rail is { } rail) _CheckSafeRail(rail);
            else if (step.Errors is { } errors)
                _UnhandledError(step.Value.Span, step.Call?.Gear.Name ?? "cette étape", errors, $"ajoute un rail : {step.Call?.Gear.Name ?? "g"} !> [traiter |> done]");
        }

        if (path.End is IrResume resume)
        {
            if (inRail && resume.Limit is { FailsWhenReached: false })
                _Error(resume.Span, DiagnosticCodes.SilentResumeInRail, "cette reprise s'épuise en silence : le chemin continue alors avec la valeur non réparée", "épuisement silencieux",
                    "dans un rail, écris la limite avec un point d'exclamation, ^N<!M>, pour échouer à l'épuisement, ou termine le chemin explicitement");
            if (resume.Fallback is { } fallback) _CheckSafePath(fallback, inRail);
        }
    }

    /// <summary>Les trains et les rails que le mode sûr a vérifiés, et ceux qui finissent sans avoir lu leur erreur.</summary>
    private int _safeTrains, _safeRails;

    private readonly List<string> _unreadRails = [];

    /// <summary>
    /// Le certificat d'un programme que le mode sûr accepte : ce qui a été vérifié, et la liste des rails qui avalent ou perdent l'erreur reçue,
    /// seule part du traitement des erreurs que le compilateur ne garantit pas.
    /// </summary>
    private void _ReportCertificate()
    {
        var swallowing = _unreadRails.Count == 0 ? "aucun rail n'avale l'erreur qu'il reçoit" : $"{_unreadRails.Count} rail(s) n'ont pas lu l'erreur reçue : {string.Join(", ", _unreadRails)}";
        _diagnostics.Add(new Diagnostic(DiagnosticLevel.Info, $"mode sûr : {_safeTrains} train(s) et {_safeRails} rail(s) vérifiés, toute erreur est traitée par un rail ou fait échouer le train ; {swallowing}", Code: DiagnosticCodes.SafeCertificate));
    }

    /// <summary>
    /// Un <c>@fire</c> est détaché : personne n'attend son résultat, ses erreurs n'arrivent à aucun rail. Un pas de train qui l'atteint par ses
    /// appels (E143).
    /// </summary>
    private void _CheckNoFire(SourceSpan span, GearSymbol gear)
    {
        var visited = new HashSet<(GearSymbol, bool)> { (gear, false) };
        var pending = new Stack<(GearSymbol, bool)>([(gear, false)]);
        while (pending.Count > 0)
        {
            var (current, trustedPath) = pending.Pop();
            if (current.IsUnsafe && !trustedPath)
            {
                _Error(span, DiagnosticCodes.UnsafeRequired, $"« {gear.Name} » atteint du code unsafe dans un train safe", "code non sûr");
                return;
            }
            if (current.Async == AsyncMode.Fire)
            {
                _Error(span, DiagnosticCodes.FireInSafeTrain, current == gear ? $"« {gear.Name} » est un @fire : détaché, ses erreurs n'arrivent à aucun rail" : $"« {gear.Name} » atteint « {current.Name} », un @fire : détaché, ses erreurs n'arrivent à aucun rail", "lance un @fire",
                    "en mode sûr, un train n'appelle pas de @fire : fais-en un gear @async que le train attend, ou sors-le du chemin du train");
                return;
            }
            foreach (var callee in _calls.GetValueOrDefault(current) ?? [])
                if (visited.Add((callee, trustedPath || current.IsTrusted))) pending.Push((callee, trustedPath || current.IsTrusted));
        }
    }

    /// <summary>
    /// Vérifie un rail comme un chemin, et avertit s'il avale l'erreur qu'il reçoit : il finit en succès sans l'avoir lue (W065), ou fait échouer le
    /// train avec une autre erreur sans l'avoir lue (W066).
    /// </summary>
    private void _CheckSafeRail(IrPath rail)
    {
        ++_safeRails;
        _CheckSafePath(rail, inRail: true);
        if (rail.End is IrResume || _ReceivedErrorRead(rail)) return;

        _unreadRails.Add($"ligne {rail.Span.StartPosition.Line}");
        if (rail.End is IrFail)
            _diagnostics.Warning(rail.Span, DiagnosticCodes.ErrorLost, "ce rail fait échouer le train avec une autre erreur : celle qu'il reçoit disparaît", "erreur perdue",
                ["écris « |> fail » seul pour envoyer l'erreur reçue, ou lis-la pour construire la nouvelle"]);
        else
            _diagnostics.Warning(rail.Span, DiagnosticCodes.ErrorIgnored, "ce rail termine le train en succès sans avoir lu l'erreur qu'il reçoit", "erreur ignorée",
                ["lis l'erreur (un paramètre utilisé dans le handler), ou fais échouer le train avec « |> fail »", "pour avaler l'erreur exprès, marque le handler : [ignoreError] emit ignorer(Exception e): void { … }"]);
    }

    /// <summary>
    /// Vrai si l'erreur que reçoit le rail est lue : son premier consommateur, un gear dont le paramètre est utilisé dans le corps, ou un
    /// <c>fail</c> qui la renvoie. Un parcours, un gear dont le corps n'est pas connu et un handler marqué <c>[ignoreError]</c> comptent comme
    /// une lecture : le compilateur ne les signale pas.
    /// </summary>
    private bool _ReceivedErrorRead(IrPath rail)
    {
        GearSymbol? consumer;
        if (rail.Steps.Count > 0)
        {
            if (rail.Steps[0].Value is not IrCall call) return true;
            consumer = call.Gear;
        }
        else if (rail.End is IrEmit emit) consumer = emit.Call.Gear;
        else if (rail.End is IrFail { Error: { } error }) return IrWalk.Expressions(error).Any(e => e is IrPipelineValue);
        else return rail.End is IrFail;

        if (consumer.IgnoresError || consumer.Parameters.Count == 0 || !_boundGears.TryGetValue(consumer, out var gear)) return true;

        var received = consumer.Parameters[0];
        return IrWalk.AllExpressions(gear.Body).Any(e => e is IrParameterReference reference && reference.Parameter == received);
    }

    private void _UnhandledError(SourceSpan span, string name, IrType errors, string help) =>
        _Error(span, DiagnosticCodes.UnhandledError, $"« {name} » peut échouer avec {_ErrorNames(errors)} et aucun rail ne traite cette erreur", "erreur non traitée",
            help, "faire échouer le train s'écrit explicitement : un rail qui se termine par « |> fail »");

    /// <summary>Les types d'une union d'erreurs, ou le type seul, séparés par des virgules.</summary>
    private static string _ErrorNames(IrType errors) => errors is UnionType union ? string.Join(", ", union.Members.Select(m => m.ToString())) : errors.ToString()!;
}
