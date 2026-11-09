using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Train;

/// <summary>
/// La valeur qui entre dans une étape, rangée avant son appel : pour qu'un point de reprise y
/// revienne, ou qu'une boucle de guard la renvoie au guard. L'appel la lit par son premier argument,
/// un <see cref="IrStepInput"/>.
/// </summary>
/// <param name="Value">La valeur qui entre, déjà du type du premier paramètre.</param>
/// <param name="Checkpoint">Le point de reprise posé devant l'étape ; <see langword="null"/> sans point.</param>
/// <param name="Repeat">La boucle du guard ; <see langword="null"/> pour un seul appel.</param>
internal sealed record IrStepEntry(IrExpression Value, CheckpointSymbol? Checkpoint, IrRepeat? Repeat);

/// <summary>
/// La boucle d'un guard (<c>*</c>) : il reçoit à chaque tour la valeur qu'il vient de renvoyer,
/// jusqu'à un <c>done</c>, un <c>fail</c> ou la limite.
/// </summary>
/// <param name="Span">L'étoile et sa limite.</param>
/// <param name="Limit">La limite de tours ; <see langword="null"/> sans limite.</param>
/// <param name="Errors">Les erreurs de l'étape : celles du guard, plus une LoopError pour <c>*&lt;!N&gt;</c> ; <see langword="null"/> si elle ne peut pas échouer.</param>
internal sealed record IrRepeat(SourceSpan Span, IrLimit? Limit, IrType? Errors);

/// <summary>La limite d'une boucle ou d'une reprise : <c>&lt;N&gt;</c> passe à la suite, <c>&lt;!N&gt;</c> échoue.</summary>
/// <param name="Count">Le nombre de tours ou de reprises permis, au moins 1.</param>
/// <param name="FailsWhenReached">Vrai pour <c>&lt;!N&gt;</c>.</param>
internal sealed record IrLimit(uint Count, bool FailsWhenReached);
