using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// Une lambda : ses paramètres et son corps, lié comme celui d'un gear (un corps expression devient
/// un <c>return</c>). Les variables extérieures qu'elle lit sont capturées par copie à sa création :
/// la créer n'évalue que ces copies, jamais son corps.
/// </summary>
/// <param name="Span">La lambda dans le source.</param>
/// <param name="Function">Son type ; pure si son corps n'a aucun effet.</param>
/// <param name="Parameters">Ses paramètres, dans l'ordre.</param>
/// <param name="Body">Son corps.</param>
internal sealed record IrLambda(SourceSpan Span, FunctionType Function, IReadOnlyList<ParameterSymbol> Parameters, IrBlock Body) : IrExpression(Span, Function);

/// <summary>Un gear utilisé comme valeur, par son nom : <c>garder(nums, estPair)</c>. Il a le type de sa signature.</summary>
/// <param name="Span">Le nom dans le source.</param>
/// <param name="Function">Le type de sa signature.</param>
/// <param name="Gear">Le gear.</param>
internal sealed record IrGearReference(SourceSpan Span, FunctionType Function, GearSymbol Gear) : IrExpression(Span, Function);

/// <summary>L'appel d'une lambda : <c>filtre(4)</c>, <c>ops[0](3)</c>, <c>v |&gt; f</c>, arguments déjà du type de ses paramètres.</summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Target">La lambda appelée, évaluée avant les arguments.</param>
/// <param name="Arguments">Les arguments, dans l'ordre d'évaluation.</param>
internal sealed record IrInvoke(SourceSpan Span, IrExpression Target, IReadOnlyList<IrExpression> Arguments) : IrExpression(Span, ((FunctionType)Target.Type).Return)
{
    /// <summary>Le type de la lambda appelée.</summary>
    public FunctionType Function => (FunctionType)Target.Type;
}
