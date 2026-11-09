using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les gears dont la récursion est terminale : chacun de leurs appels à eux-mêmes est un <c>return f(…);</c>. Un tel gear s'écrit en C++
/// comme une boucle (<c>for (;;)</c>) : ses paramètres reçoivent les nouveaux arguments et le tour suivant recommence, sans nouvelle pile et sans
/// <c>recursion_guard</c> (qui, en plus de compter, empêchait Clang de transformer lui-même l'appel). Une récursion terminale de plusieurs
/// millions de tours ne dépasse donc plus la limite de profondeur ni la pile.
/// </summary>
/// <remarks>
/// La transformation est prudente : un gear n'est retenu que si aucun de ses appels récursifs n'est autre chose qu'un <c>return</c> direct
/// du résultat, s'il n'appelle aucun autre gear récursif (une récursion mutuelle garde ses gardes) et s'il n'a ni paramètre <c>mut</c>,
/// ni valeur par défaut, ni paramètre qu'on ne peut pas copier (cycle de vie, pointeur).
/// </remarks>
internal static class TailCalls
{
    /// <summary>Les gears du programme qui s'écrivent en boucle.</summary>
    public static HashSet<GearSymbol> Of(IrProgram program)
    {
        var result = new HashSet<GearSymbol>();
        foreach (var gear in program.Gears)
        {
            if (Reason(gear) is null) result.Add(gear.Symbol);
        }
        return result;
    }

    /// <summary>Vrai si <paramref name="statement"/> est <c>return gear(…);</c> : un appel terminal.</summary>
    public static bool IsTailCall(IrStatement statement, GearSymbol gear) =>
        statement is IrReturn { IsDone: false, StopsFold: false, Value: IrCall { AsResult: false } call } && call.Gear == gear;

    /// <summary>Pourquoi ce gear récursif ne s'écrit pas en boucle ; <see langword="null"/> s'il s'y écrit (ou s'il n'est pas récursif).</summary>
    public static string? Reason(IrGear gear)
    {
        var symbol = gear.Symbol;
        if (!symbol.IsRecursive) return "il n'est pas récursif";
        if (symbol.Kind != GearKind.Gear || symbol.IsNative || symbol.IsTrain || symbol.Async != AsyncMode.None) return "seul un gear ordinaire (ni guard, emit, train, asynchrone ou natif) s'écrit en boucle";
        if (gear.Taps is { Count: > 0 }) return "il porte des @tap";
        if (gear.Defaults is { Count: > 0 } || symbol.Parameters.Any(p => p.HasDefault || p.IsParams)) return "un paramètre a une valeur par défaut ou ramasse les derniers arguments";
        if (symbol.ReturnType == PrimitiveType.Void || symbol.Parameters.Count == 0) return "il ne renvoie rien ou n'a pas de paramètre";
        // Les paramètres deviennent des variables du gear : copiables, sans modification à la place de l'appelant.
        if (symbol.Parameters.Any(p => p.IsMutable || p.Type is PointerType || LifecycleRules.Has(p.Type))) return "un paramètre est « mut », un pointeur ou une valeur à cycle de vie, qu'on ne peut pas recopier à chaque tour";

        var tails = IrWalk.Statements(gear.Body).Where(s => IsTailCall(s, symbol)).Cast<IrReturn>().Select(r => (IrCall)r.Value!).ToList();
        if (tails.Count == 0 || tails.Any(call => call.Arguments.Count != symbol.Parameters.Count || call.Arguments.Any(a => a is IrOmittedArgument))) return "aucun appel récursif n'est un « return f(…); » direct";

        var tailCalls = new HashSet<IrCall>(tails, ReferenceEqualityComparer.Instance);
        return _Expressions(gear.Body, false).All(item => _Accepts(item.Expression, item.InLambda, symbol, tailCalls))
            ? null
            : "un appel récursif n'est pas terminal (ou est dans une lambda), ou le gear en appelle un autre qui est récursif";
    }

    /// <summary>Un appel ou une référence à un gear récursif n'est permis que s'il est l'un des appels terminaux du gear lui-même, hors d'une lambda.</summary>
    private static bool _Accepts(IrExpression expression, bool inLambda, GearSymbol self, HashSet<IrCall> tailCalls) => expression switch
    {
        IrCall call when call.Gear == self => !inLambda && tailCalls.Contains(call),
        IrCall call => !call.Gear.IsRecursive,
        IrGearReference reference => !reference.Gear.IsRecursive,
        _ => true,
    };

    /// <summary>Toutes les expressions d'un corps, celles des lambdas qu'il écrit comprises (marquées).</summary>
    private static IEnumerable<(IrExpression Expression, bool InLambda)> _Expressions(IrStatement body, bool inLambda)
    {
        foreach (var expression in IrWalk.AllExpressions(body))
        {
            yield return (expression, inLambda);
            if (expression is IrLambda lambda)
            {
                foreach (var inner in _Expressions(lambda.Body, true)) yield return inner;
            }
        }
    }
}
