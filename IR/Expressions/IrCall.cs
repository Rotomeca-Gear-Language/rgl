using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// L'appel d'un gear, arguments déjà du type de ses paramètres ; un pipeline <c>v |&gt; g(a)</c>
/// devient l'appel <c>g(v, a)</c>.
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Gear">Le gear appelé.</param>
/// <param name="Arguments">Les arguments, dans l'ordre d'évaluation (de gauche à droite).</param>
/// <param name="AsResult">
/// Vrai pour l'appel, dans une expression, d'un train qui peut échouer : sa valeur est le résultat
/// <c>T ! E</c>, au lieu de la valeur seule d'un guard appelé comme étape.
/// </param>
internal sealed record IrCall(SourceSpan Span, GearSymbol Gear, IReadOnlyList<IrExpression> Arguments, bool AsResult = false)
    : IrExpression(Span, AsResult ? new Types.ResultType(Gear.ReturnType, Gear.Errors!) : Gear.ReturnType);

/// <summary>
/// La valeur qui entre dans une étape d'un chemin, son premier argument : la valeur de départ du
/// train, le résultat de l'étape précédente, ou l'Exception reçue par un rail.
/// </summary>
/// <param name="Span">L'étape qui la reçoit.</param>
/// <param name="Type">Son type.</param>
internal sealed record IrPipelineValue(SourceSpan Span, Types.IrType Type) : IrExpression(Span, Type);

/// <summary>
/// La valeur rangée avant une étape (<see cref="Train.IrStepEntry"/>), premier argument de son appel :
/// celle du point de reprise, ou celle du tour en cours d'une boucle de guard.
/// </summary>
/// <param name="Span">L'étape qui la reçoit.</param>
/// <param name="Type">Son type, celui du premier paramètre.</param>
internal sealed record IrStepInput(SourceSpan Span, Types.IrType Type) : IrExpression(Span, Type);
