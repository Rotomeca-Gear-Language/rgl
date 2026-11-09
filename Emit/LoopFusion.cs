using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.IR;

namespace Rgl.Emit;

/// <summary>
/// Deux déclarations qui se suivent, dont la seconde parcourt le tableau que la première vient de construire par un parcours, ne font qu'une
/// boucle : <c>[int] a: xs ||&gt; f; [int] b: a ||&gt; g;</c> s'écrit comme <c>[int] b: xs ||&gt; f ||&gt; g;</c>, sans le tableau
/// intermédiaire. Une seule chaîne de <c>||&gt;</c> est déjà une seule boucle ; ceci étend la même règle à deux instructions.
/// </summary>
/// <remarks>
/// La fusion n'a lieu que si elle ne change rien de ce qu'un programme peut observer, à une différence près : quand les deux parcours s'arrêteraient
/// sur un dépassement ou un index hors limites, le premier élément fautif n'est plus forcément celui du premier parcours. Conditions :
/// les deux déclarations se suivent ; la variable intermédiaire n'est lue nulle part ailleurs que comme source du second parcours, n'est pas
/// <c>mut</c> et se déclare avec le parcours lui-même ; aucune étape n'a d'effet, de guard, de rail ni ne modifie sur place ; le second parcours n'a pas de
/// <c>done valeur</c> (qui arrêterait l'accumulation avant que le premier parcours ait fini, et lui épargnerait ses erreurs).
/// </remarks>
internal static class LoopFusion
{
    /// <summary>Les instructions de <paramref name="statements"/>, deux parcours qui se suivent réunis en un seul ; la liste d'origine si rien ne se réunit.</summary>
    public static IReadOnlyList<IrStatement> Fuse(IReadOnlyList<IrStatement> statements, Action<OptimizationNote>? note = null)
    {
        List<IrStatement>? list = null;
        for (var i = 0; i + 1 < (list ?? statements).Count;)
        {
            var current = list ?? statements;
            if (current[i] is IrVariableDeclaration first && current[i + 1] is IrVariableDeclaration second && _TryFuse(first, second, current, i + 2, out var reasons) is { } fused)
            {
                note?.Invoke(new OptimizationNote(fused.Span, false, $"deux parcours fondus en une boucle : « {first.Variable.Name} » n'est plus construit"));
                list ??= [.. statements];
                list[i] = fused;
                list.RemoveAt(i + 1);
                // La déclaration réunie peut elle-même se fondre avec la suivante : une chaîne de trois parcours ne fait qu'une boucle.
            }
            else
            {
                if (note is not null && current[i] is IrVariableDeclaration { Value: IrEach } producer && current[i + 1] is IrVariableDeclaration { Value: IrEach { Source: IrVariableReference reference } } consumer
                    && reference.Variable == producer.Variable && _TryFuse(producer, consumer, current, i + 2, out var refused) is null && refused is { Count: > 0 })
                    note(new OptimizationNote(consumer.Span, true, $"les parcours de « {producer.Variable.Name} » et de « {consumer.Variable.Name} » restent deux boucles", refused));
                i++;
            }
        }
        return list ?? statements;
    }

    private static IrVariableDeclaration? _TryFuse(IrVariableDeclaration first, IrVariableDeclaration second, IReadOnlyList<IrStatement> statements, int rest, out IReadOnlyList<string>? reasons)
    {
        reasons = null;
        if (first.Value is not IrEach { Fold: null } producer || second.Value is not IrEach consumer) return null;
        if (consumer.Source is not IrVariableReference source || source.Variable != first.Variable) return null;
        if (first.Variable.IsMutable) return _Refuse(out reasons, $"« {first.Variable.Name} » est « mut » : un autre code peut la modifier");
        if (first.Variable.Type is not ArrayType { Kind: ArrayKind.Dynamic } || producer.Type != first.Variable.Type) return _Refuse(out reasons, $"« {first.Variable.Name} » n'est pas un tableau dynamique construit par le parcours seul");
        if (producer.Modifies || consumer.Modifies) return _Refuse(out reasons, "un parcours modifie ses éléments sur place");
        if (!producer.Stages.All(_IsPlain) || !consumer.Stages.All(_IsPlain)) return _Refuse(out reasons, "une étape a un effet (écriture, appel non pur), un guard ou un rail : l'ordre des deux parcours doit rester celui de l'écriture");
        if (consumer.Fold is { } fold && !_IsPlainFold(fold)) return _Refuse(out reasons, "l'accumulation a un effet, un guard, un rail ou un « done valeur » qui l'arrêterait avant la fin du premier parcours");

        // La variable intermédiaire ne sert qu'une fois : dans le second parcours, pour sa source, et nulle part plus loin.
        if (_Mentions(second.Value, first.Variable) != 1) return _Refuse(out reasons, $"« {first.Variable.Name} » est lue ailleurs dans le second parcours");
        for (var i = rest; i < statements.Count; i++)
        {
            if (_Mentions(statements[i], first.Variable) != 0) return _Refuse(out reasons, $"« {first.Variable.Name} » est relue plus loin : le tableau doit exister");
        }

        var fused = new IrEach(consumer.Span, consumer.Type, producer.Source, [.. producer.Stages, .. consumer.Stages], consumer.Fold);
        return new IrVariableDeclaration(second.Span, second.Variable, fused);
    }

    private static IrVariableDeclaration? _Refuse(out IReadOnlyList<string>? reasons, string reason)
    {
        reasons = [reason];
        return null;
    }

    private static bool _IsPlain(IrEachStage stage) =>
        stage.Guard is null && stage.Rail is null && !stage.Modifies && stage.Output is not null && !IrEffects.HasEffects(stage.Apply);

    private static bool _IsPlainFold(IrEachFold fold) =>
        fold.Guard is null && fold.Rail is null && !IrEffects.HasEffects(fold.Seed) && !IrEffects.HasEffects(fold.Apply)
        && !(fold.Apply is IrInvoke { Target: IrLambda lambda } && IrWalk.Statements(lambda.Body).OfType<IrReturn>().Any(r => r.StopsFold));

    /// <summary>Le nombre de fois qu'un parcours de <paramref name="expression"/> rencontre <paramref name="variable"/>, lambdas comprises.</summary>
    private static int _Mentions(IrExpression expression, VariableSymbol variable) =>
        IrWalk.Expressions(expression).Sum(e => _Own(e, variable));

    private static int _Mentions(IrStatement statement, VariableSymbol variable)
    {
        var count = IrWalk.AllExpressions(statement).Sum(e => _Own(e, variable));
        foreach (var inner in IrWalk.Statements(statement))
        {
            if (inner is IrAssignment assignment && assignment.Variable == variable) count++;
            else if (inner is IrUndefine undefine && undefine.Variable == variable) count++;
        }
        return count;
    }

    private static int _Own(IrExpression expression, VariableSymbol variable) => expression switch
    {
        IrVariableReference reference => reference.Variable == variable ? 1 : 0,
        IrIncrement increment => increment.Variable == variable ? 1 : 0,
        IrLambda lambda => _Mentions(lambda.Body, variable),
        _ => 0,
    };
}
