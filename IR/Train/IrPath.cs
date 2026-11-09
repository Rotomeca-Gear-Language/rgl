using Rgl.IR.Expressions;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Train;

/// <summary>
/// Un chemin d'exécution : le chemin principal d'un train, ou un rail. Ses étapes sont des appels
/// de gears et de guards, dans l'ordre : chacune reçoit le résultat de la précédente en premier
/// argument (<see cref="IrPipelineValue"/>). Un guard qui échoue part sur son rail, ou fait échouer
/// le train s'il n'en a pas ; sinon, la terminaison finit le chemin.
/// </summary>
/// <param name="Span">Le chemin dans le source.</param>
/// <param name="Steps">Les étapes, avant la terminaison.</param>
/// <param name="End">Ce qui termine le chemin.</param>
internal sealed record IrPath(SourceSpan Span, IReadOnlyList<IrStep> Steps, IrTerminator End);

/// <summary>
/// Une étape d'un chemin : l'appel d'un gear ou d'un guard, et le rail d'un guard
/// (<c>|&gt; lire !&gt; [reparer |&gt; $ 3]</c>), qui reçoit son erreur et termine le train ; ou un
/// parcours (<see cref="IrEach"/>), dont chaque étape porte son propre rail.
/// </summary>
/// <param name="Value">L'appel, ou le parcours.</param>
/// <param name="Rail">Le rail du guard ; <see langword="null"/> sans rail : son échec fait échouer le train.</param>
/// <param name="Entry">La valeur d'entrée rangée avant l'appel, pour un point de reprise ou une boucle ; <see langword="null"/> sinon.</param>
internal sealed record IrStep(IrExpression Value, IrPath? Rail, IrStepEntry? Entry = null)
{
    /// <summary>L'appel de l'étape ; <see langword="null"/> pour un parcours.</summary>
    public IrCall? Call => Value as IrCall;

    /// <summary>Les erreurs que l'étape envoie à son rail : celles du guard appelé, ou de sa boucle ; <see langword="null"/> si elle ne peut pas échouer.</summary>
    public IrType? Errors => Entry?.Repeat is { } repeat ? repeat.Errors : Call?.Gear.Errors;
}
