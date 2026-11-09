using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Train;

namespace Rgl.IR;

/// <summary>Parcours d'un corps de l'IR : toutes ses instructions et toutes ses expressions, sous-expressions comprises.</summary>
internal static class IrWalk
{
    /// <summary>Les instructions de <paramref name="statement"/>, elle comprise, dans l'ordre du source.</summary>
    public static IEnumerable<IrStatement> Statements(IrStatement statement)
    {
        yield return statement;
        IEnumerable<IrStatement> inner = statement switch
        {
            IrBlock block => block.Statements,
            IrIf ifStatement => ifStatement.Else is null ? [ifStatement.Then] : [ifStatement.Then, ifStatement.Else],
            IrSwitch switchStatement => switchStatement.Sections.Select(s => s.Body),
            _ => [],
        };
        foreach (var child in inner.SelectMany(Statements)) yield return child;

        // Les blocs des bras d'un match sont des instructions dans une expression.
        foreach (var expression in Own(statement).SelectMany(Expressions))
        {
            if (expression is not IrMatch match) continue;
            foreach (var body in match.Arms.Where(a => a.Body is not null))
                foreach (var child in Statements(body.Body!)) yield return child;
        }
    }

    /// <summary>Toutes les expressions de <paramref name="statement"/> et de ses instructions, sous-expressions comprises.</summary>
    public static IEnumerable<IrExpression> AllExpressions(IrStatement statement) =>
        Statements(statement).SelectMany(Own).SelectMany(Expressions);

    /// <summary>Les expressions écrites dans <paramref name="statement"/> et ses instructions, sans leurs sous-expressions.</summary>
    public static IEnumerable<IrExpression> Roots(IrStatement statement) => Statements(statement).SelectMany(Own);

    /// <summary><paramref name="expression"/> et toutes ses sous-expressions.</summary>
    public static IEnumerable<IrExpression> Expressions(IrExpression expression)
    {
        var inner = IrEffects.Children(expression).SelectMany(Expressions);
        // Les rails d'un parcours sont des chemins de train : leurs expressions en font partie.
        if (expression is IrEach each)
            inner = inner.Concat(each.Stages.Select(s => s.Rail).Append(each.Fold?.Rail).OfType<IrPath>().SelectMany(PathRoots).SelectMany(Expressions));
        return inner.Prepend(expression);
    }

    /// <summary>
    /// Les expressions écrites dans un chemin de train, sans leurs sous-expressions : valeurs rangées avant une
    /// étape, étapes, rails, valeur de la terminaison, suite d'une reprise limitée.
    /// </summary>
    public static IEnumerable<IrExpression> PathRoots(IrPath path)
    {
        foreach (var step in path.Steps)
        {
            if (step.Entry is { } entry) yield return entry.Value;
            yield return step.Value;
            if (step.Rail is { } rail) foreach (var inner in PathRoots(rail)) yield return inner;
        }
        IrExpression? end = path.End switch
        {
            IrExit exit => exit.Code,
            IrFail fail => fail.Error,
            IrEmit emit => emit.Call,
            IrResume resume => resume.Value,
            _ => null,
        };
        if (end is not null) yield return end;
        if (path.End is IrResume { Fallback: { } fallback }) foreach (var inner in PathRoots(fallback)) yield return inner;
    }

    /// <summary>Les expressions écrites directement dans une instruction (pas dans ses sous-instructions).</summary>
    public static IEnumerable<IrExpression> Own(IrStatement statement) => statement switch
    {
        IrVariableDeclaration declaration => [declaration.Value],
        IrAssignment assignment => [assignment.Value],
        IrContentAssignment assignment => [assignment.Target, assignment.Value],
        IrIf ifStatement => [ifStatement.Condition],
        IrSwitch switchStatement => [switchStatement.Subject, .. switchStatement.Sections.Where(s => s.Test is not null).Select(s => s.Test!)],
        IrReturn { Value: { } value } => [value],
        IrFailStatement fail => [fail.Error],
        IrPanic panic => [panic.Value],
        IrWrite write => [write.Value],
        IrWriteFile write => [write.Value, write.Path],
        IrWriteJson write => [write.Value, write.Path],
        IrWriteXml write => [write.Value, write.Path],
        IrExpressionStatement expression => [expression.Expression],
        IrTrainBody body => body.StartValue is { } start ? [start, .. PathRoots(body.Main)] : PathRoots(body.Main),
        _ => [],
    };
}
