using Rgl.AST.Expressions;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Les arènes et le pool : <c>arena { … }</c> alloue dans une arène tout ce que le bloc crée, y compris dans les gears qu'il appelle, et la
/// rend d'un coup à sa fin ; <c>ArenaPool.set(valeur)</c> alloue ce que <c>valeur</c> construit dans le pool, dont chaque bloc revient à la
/// liste de sa classe quand son propriétaire disparaît. Le type d'une expression <c>ArenaPool.set(x)</c> est celui de <c>x</c> : rien d'autre ne
/// change dans le programme.
/// </summary>
/// <remarks>
/// Aucune règle d'échappement : le runtime copie sur le tas toute valeur d'une arène qui est copiée ou déplacée (les retours de valeur
/// sont recopiés par <c>detach</c>, que l'émetteur écrit), donc rien ne garde l'adresse d'une arène vidée. Ce que le binder refuse
/// est ce qui ferait vivre du code autrement que dans l'ordre du bloc : une attente (la boucle d'événements reprendrait d'autres tâches
/// sous l'arène, ou le bloc finirait pendant qu'une coroutine tient encore de sa mémoire) et l'appel d'un gear asynchrone (sa
/// coroutine garderait des valeurs de l'arène après sa fin).
/// </remarks>
internal sealed partial class Binder
{
    /// <summary>Le nombre de blocs <c>arena</c> ouverts autour de l'instruction qu'on lie.</summary>
    private int _arenaDepth;

    /// <summary>Le nombre de <c>ArenaPool.set(…)</c> ouverts autour de l'expression qu'on lie.</summary>
    private int _poolDepth;

    /// <summary>Un bloc <c>arena</c> : un bloc ordinaire, qui ouvre une arène de la taille voulue.</summary>
    private IrBlock _BindArenaBlock(BlockStatement block)
    {
        uint size = 0;
        if (block.ArenaSize is { } written && _BindExpression(written) is { } bound)
        {
            if (ConstantFolder.Fold(bound) is long bytes and >= 0 and <= uint.MaxValue) size = (uint)bytes;
            else
                _Error(written.Span, DiagnosticCodes.ArenaScope, "la taille d'une arène est un entier connu à la compilation, de 0 à 4 294 967 295 octets", "pas une taille",
                    "par exemple : arena(262144) { … }, ou arena { … } pour la taille par défaut (64 Ko)");
        }

        ++_arenaDepth;
        var statements = _BindStatements(block.Span, block.Statements);
        --_arenaDepth;
        return statements with { IsArena = true, FirstSegment = size };
    }

    /// <summary>
    /// Signale (E154) ce qui ne se place pas dans une arène ni dans <c>ArenaPool.set(…)</c> : <paramref name="what"/>. Renvoie vrai si c'est le cas.
    /// </summary>
    private bool _RejectInAllocatorScope(SourceSpan span, string what)
    {
        if (_arenaDepth == 0 && _poolDepth == 0) return false;
        var inside = _arenaDepth > 0 ? "un bloc arena" : "ArenaPool.set(…)";
        _Error(span, DiagnosticCodes.ArenaScope, $"{what} ne se place pas dans {inside}", "ici",
            _arenaDepth > 0
                ? "l'arène suit le thread : une attente reprendrait d'autres tâches sous elle, et une coroutine garderait de sa mémoire après sa fin"
                : "mets l'attente ou l'appel hors de ArenaPool.set(…), et passe-lui la valeur obtenue");
        return true;
    }

    /// <summary>
    /// <c>ArenaPool.set(valeur)</c> : <c>valeur</c> est construite avec le pool pour allocateur. Seuls les tableaux, les textes, les dictionnaires et ce qui en
    /// contient en ont besoin ; pour un entier ou un bool l'appel ne fait rien. Une valeur qui n'est qu'une variable n'est pas reconstruite : elle garde
    /// l'allocateur qu'elle a.
    /// </summary>
    private IrExpression? _BindPoolValue(CallExpression call)
    {
        if (call.Arguments.Count != 1)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"ArenaPool.set attend une valeur, pas {call.Arguments.Count} arguments", "une valeur",
                "par exemple : var nombres: ArenaPool.set(plages ||> double);");
            return null;
        }

        ++_poolDepth;
        var value = _BindExpression(call.Arguments[0]);
        --_poolDepth;
        return value is null ? null : new IrPoolValue(call.Span, value.Type, value);
    }
}
