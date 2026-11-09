using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Train;

/// <summary>Ce qui termine un chemin : <c>done</c>, <c>fail</c>, <c>$ valeur</c>, un emit ou une reprise.</summary>
/// <param name="Span">La terminaison dans le source.</param>
internal abstract record IrTerminator(SourceSpan Span);

/// <summary>
/// <c>done</c> : succès ; pour le train d'entrée, code de sortie 0 ; pour un train appelé, sa valeur est celle
/// qui est arrivée à cette étape.
/// </summary>
/// <param name="Span">Le mot <c>done</c>.</param>
/// <param name="Value">Le type de la valeur arrivée à <c>done</c> ; <see langword="null"/> si aucune (étape void, ou train sans valeur de départ).</param>
internal sealed record IrDone(SourceSpan Span, Types.IrType? Value = null) : IrTerminator(Span);

/// <summary>
/// <c>fail [valeur]</c> : échec ; le train échoue (code de sortie 1 pour le train d'entrée). La
/// valeur, une erreur, n'est évaluée que pour ses effets.
/// </summary>
/// <param name="Span">Le <c>fail</c> et sa valeur.</param>
/// <param name="Error">L'erreur envoyée (un texte devient une Exception) ; <see langword="null"/> pour un <c>fail</c> seul.</param>
internal sealed record IrFail(SourceSpan Span, IrExpression? Error) : IrTerminator(Span);

/// <summary>
/// Un emit, dernière étape du chemin : pour le train d'entrée, un emit <c>void</c> donne le code de
/// sortie 0, un emit <c>uint</c> donne son résultat.
/// </summary>
/// <param name="Span">L'étape dans le source.</param>
/// <param name="Call">L'appel de l'emit, valeur de l'étape précédente en premier argument.</param>
internal sealed record IrEmit(SourceSpan Span, IrCall Call) : IrTerminator(Span);

/// <summary>
/// <c>$ valeur</c> : fin du train d'entrée avec ce code de sortie, ou d'un train appelé avec cette valeur.
/// </summary>
/// <param name="span">Le <c>$</c> et sa valeur.</param>
/// <param name="code">Le code de sortie, de type <c>uint</c> pour le train d'entrée ; la valeur du train sinon.</param>
internal sealed record IrExit(SourceSpan span, IrExpression code) : IrTerminator(span)
{
    /// <summary>
    /// La valeur. Dans un train appelé, elle est ramenée au type commun de toutes les fins une fois le train lié :
    /// le binder la remplace alors par sa conversion.
    /// </summary>
    public IrExpression Code { get; set; } = code;
}

/// <summary>
/// <c>^N</c> : revient au point de reprise avec la valeur courante, qui y remplace la valeur rangée.
/// Une reprise limitée compte ses passages ; la limite atteinte, <c>&lt;N&gt;</c> continue par
/// <see cref="Fallback"/>, <c>&lt;!N&gt;</c> fait échouer le train.
/// </summary>
/// <param name="Span">La reprise dans le source.</param>
/// <param name="Target">Le point visé, déjà passé par le chemin.</param>
/// <param name="Value">La valeur apportée, du type du point.</param>
/// <param name="Limit">La limite de reprises ; <see langword="null"/> sans limite.</param>
/// <param name="Counter">Le rang de son compteur parmi les reprises limitées du point.</param>
/// <param name="Fallback">La suite du chemin une fois la limite <c>&lt;N&gt;</c> atteinte ; <see langword="null"/> sinon.</param>
internal sealed record IrResume(SourceSpan Span, CheckpointSymbol Target, IrExpression Value, IrLimit? Limit, uint Counter, IrPath? Fallback) : IrTerminator(Span);
