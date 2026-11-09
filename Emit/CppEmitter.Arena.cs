using Rgl.IR.Expressions;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les arènes et le pool : un bloc <c>arena</c> ouvre <c>rgl::standard::arena_scope</c> (l'arène devient l'allocateur ambiant du thread jusqu'à
/// la fin du bloc), <c>ArenaPool.set(x)</c> évalue <c>x</c> sous <c>rgl::standard::allocator_scope</c> du pool. Les valeurs qui sortent d'un bloc
/// arena par <c>return</c>, <c>done</c> ou <c>fail</c> passent par <c>rgl::standard::detach</c> : le C++ élide la construction d'un retour
/// (la valeur serait bâtie dans l'appelant, donc dans l'arène) ; <c>detach</c> la refait par le constructeur, dont les règles mettent sur le
/// tas ce qui vient d'une arène.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Le nombre de blocs <c>arena</c> ouverts autour de l'instruction écrite.</summary>
    private int _arenaDepth;

    /// <summary>
    /// Indique si une valeur de ce type peut tenir de la mémoire allouée (un texte, un tableau, un dictionnaire, ou un type qui en
    /// contient) : un nombre, un bool, un enum ou une durée n'a rien à détacher d'une arène, pas plus qu'une fonction, qui n'utilise jamais l'arène.
    /// </summary>
    private static bool _MayOwnMemory(IrType type) => _Representation(type) switch
    {
        PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString or PrimitiveKind.Bytes } => true,
        PrimitiveType or EnumType or FunctionType or PromiseType => false,
        MoldType mold => mold.Symbol.Fields.Any(f => _MayOwnMemory(f.Type)),
        _ => true,
    };

    /// <summary>
    /// La valeur <paramref name="text"/> d'un <c>return</c>, <c>done</c> ou <c>fail</c>, détachée d'une arène si le bloc écrit en ouvre une : une variable locale
    /// est déplacée, une constante ou toute autre valeur est copiée ou déplacée selon ce qu'elle est.
    /// </summary>
    private string _Detached(IrExpression value, string text)
    {
        if (_arenaDepth == 0 || !_MayOwnMemory(value.Type) || LifecycleRules.Has(value.Type)) return text;
        _Use(RuntimeComponent.Memory, "rgl/standard/arena.hpp");
        return value is IrVariableReference { Variable.IsConstant: false } local
            ? $"rgl::standard::detach(std::move({_variableNames[local.Variable]}))"
            : $"rgl::standard::detach({text})";
    }

    /// <summary><c>ArenaPool.set(x)</c> : <c>x</c> construite avec le pool pour allocateur, qui reste celui de ce qu'elle a construit.</summary>
    private string _PoolValue(IrPoolValue pool, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.Memory, "rgl/standard/pool.hpp");
        var type = _Type(pool.Type);
        var value = operand(pool.Value);
        var id = ++_temporaryCount;

        // Une valeur qui peut quitter l'expression par return (un guard appelé dedans) ne tient pas dans une lambda : une expression-instruction la laisse faire.
        if (_UseStatementExpressions)
        {
            _usesStatementExpressions = true;
            return $"({{ rgl::standard::allocator_scope rgl_pool{id}(rgl::standard::pool_allocator()); {type} rgl_pv{id} = {value}; std::move(rgl_pv{id}); }})";
        }
        return $"[{(_atFileLevel ? "" : "&")}]() -> {type} {{ rgl::standard::allocator_scope rgl_pool{id}(rgl::standard::pool_allocator()); return {value}; }}()";
    }
}
