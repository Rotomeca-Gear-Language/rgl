using Rgl.IR.Expressions;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>
/// La modification en place du contenu d'une variable ou d'un paramètre <c>mut</c> : <c>pos.x &lt;- 3.0</c>,
/// <c>ps[i].vie &lt;- 0</c>, <c>ages["a"] &lt;- 4</c>. La cible est une suite d'accès (<see cref="IrMemberAccess"/>,
/// <see cref="IrIndex"/>, <see cref="IrTupleItem"/>) qui part de la variable ; ses index sont évalués une
/// fois, de gauche à droite, avant la valeur. Un index absent arrête le programme, ou fait échouer le guard.
/// </summary>
/// <param name="Span">L'affectation dans le source.</param>
/// <param name="Target">Ce qui est modifié.</param>
/// <param name="Value">
/// La nouvelle valeur, déjà du type de la cible ; dans une affectation composée (<c>+&lt;-</c>), elle lit
/// la valeur d'avant par <see cref="IrTargetValue"/>.
/// </param>
internal sealed record IrContentAssignment(SourceSpan Span, IrExpression Target, IrExpression Value) : IrStatement(Span)
{
    /// <summary>La variable ou le paramètre dont le contenu est modifié : la racine de la cible.</summary>
    public IrExpression Root => IrMutation.Root(Target);
}

/// <summary>La valeur d'avant de la cible d'une affectation composée en place : le <c>a[i]</c> de <c>a[i] +&lt;- 1</c>.</summary>
/// <param name="Span">La cible dans le source.</param>
/// <param name="Type">Son type.</param>
internal sealed record IrTargetValue(SourceSpan Span, IrType Type) : IrExpression(Span, Type);

/// <summary>Ce que modifient les écritures en place : la racine d'une cible, les arguments <c>mut</c> d'un appel.</summary>
internal static class IrMutation
{
    /// <summary>La variable ou le paramètre au départ d'une suite d'accès.</summary>
    public static IrExpression Root(IrExpression target) => target switch
    {
        IrMemberAccess access => Root(access.Target),
        IrIndex index => Root(index.Target),
        IrTupleItem item => Root(item.Target),
        IrRetype retype => Root(retype.Value),
        IrNonNull nonNull => Root(nonNull.Value),
        _ => target,
    };

    /// <summary>
    /// Ce que modifie <paramref name="expression"/>, sous-expressions comprises : le symbole
    /// (<see cref="Symbols.VariableSymbol"/> ou <see cref="Symbols.ParameterSymbol"/>) d'une variable
    /// incrémentée, qui grandit par <c>push</c> ou <c>add</c>, ou passée à un paramètre <c>mut</c>.
    /// </summary>
    public static IEnumerable<object> Modified(IrExpression expression)
    {
        IEnumerable<IrExpression> roots = expression switch
        {
            IrPush push => [Root(push.Target)],
            IrAdd add => [Root(add.Target)],
            IrCall call => MutableArguments(call).Select(Root),
            _ => [],
        };
        foreach (var root in roots)
        {
            if (Symbol(root) is { } symbol) yield return symbol;
        }
        if (expression is IrIncrement increment) yield return increment.Variable;
        foreach (var inner in IrEffects.Children(expression).SelectMany(Modified)) yield return inner;
    }

    /// <summary>Le symbole d'une variable ou d'un paramètre lu ; <see langword="null"/> pour une autre expression.</summary>
    public static object? Symbol(IrExpression expression) => expression switch
    {
        IrVariableReference reference => reference.Variable,
        IrParameterReference reference => reference.Parameter,
        _ => null,
    };

    /// <summary>Les arguments d'un appel passés à un paramètre <c>mut</c> : des variables que l'appel modifie.</summary>
    public static IEnumerable<IrExpression> MutableArguments(IrCall call) =>
        call.Arguments.Where((_, i) => call.Gear.Parameters[i].IsMutable);
}
