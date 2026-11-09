using Rgl.IR.Statements;

namespace Rgl.Binding;

/// <summary>
/// Analyse du flot de contrôle de l'IR : une instruction peut-elle finir normalement, c'est-à-dire
/// laisser l'exécution passer à l'instruction suivante ? <c>return</c>, <c>fail</c>, <c>goto</c> et
/// <c>break</c> ne finissent jamais normalement ; un switch finit normalement si un <c>break</c> en sort, si
/// aucun <c>default</c> n'attrape le reste, ou si son dernier cas peut finir.
/// </summary>
internal static class ControlFlow
{
    /// <summary>Indique si <paramref name="statement"/> peut laisser l'exécution passer à la suite.</summary>
    public static bool CanCompleteNormally(IrStatement statement) => statement switch
    {
        IrReturn or IrFailStatement or IrPanic or IrGoto or IrBreak => false,
        // Un match en instruction : un bras est toujours choisi ; il continue si l'un de ses bras continue.
        IrExpressionStatement { Expression: IR.Expressions.IrMatch match } =>
            match.Arms.Any(arm => arm.Body is null || CanCompleteNormally(arm.Body)),
        IrBlock block => block.Statements.All(CanCompleteNormally),
        IrIf ifStatement => ifStatement.Else is null || CanCompleteNormally(ifStatement.Then) || CanCompleteNormally(ifStatement.Else),
        IrSwitch switchStatement =>
            (switchStatement.Sections.All(s => s.Test is not null) && !switchStatement.CoversAll)
            || switchStatement.Sections.Count == 0
            || CanCompleteNormally(switchStatement.Sections[^1].Body)
            || _BreaksOutOf(switchStatement, switchStatement.Id),
        _ => true,
    };

    /// <summary>
    /// Indique si un <c>break</c> atteignable de <paramref name="statement"/> sort du switch
    /// <paramref name="switchId"/>. Dans un bloc, ce qui suit une instruction qui ne finit jamais
    /// normalement est inaccessible : un <c>break</c> écrit après un <c>return</c> ne compte pas.
    /// </summary>
    private static bool _BreaksOutOf(IrStatement statement, int switchId) => statement switch
    {
        IrBreak breakStatement => breakStatement.SwitchId == switchId,
        IrBlock block => _BlockBreaksOutOf(block, switchId),
        IrIf ifStatement => _BreaksOutOf(ifStatement.Then, switchId) || (ifStatement.Else is { } otherwise && _BreaksOutOf(otherwise, switchId)),
        IrSwitch switchStatement => switchStatement.Sections.Any(s => _BreaksOutOf(s.Body, switchId)),
        _ => false,
    };

    private static bool _BlockBreaksOutOf(IrBlock block, int switchId)
    {
        foreach (var statement in block.Statements)
        {
            if (_BreaksOutOf(statement, switchId)) return true;
            if (!CanCompleteNormally(statement)) return false;
        }
        return false;
    }
}
