using Rgl.AST.Expressions;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Binding;

/// <summary>
/// Les fonctions C : un fichier <c>forge: native</c> est remplacé, avant la liaison, par un fichier de gears dont chacun enveloppe l'appel
/// <c>native f(x)</c> d'une fonction de l'en-tête. Le reste du programme les appelle comme n'importe quel gear.
/// </summary>
internal sealed partial class Binder
{
    /// <summary><c>native f(x)</c> : l'appel de la fonction C du même nom ; son type est celui du gear qui l'enveloppe.</summary>
    private IrExpression? _BindNativeCall(NativeCallExpression native)
    {
        if (native.Call is not CallExpression { Callee: NameExpression name } call || _currentGear is not { } gear)
        {
            _Unsupported(native.Span, "native hors d'une liaison engendrée");
            return null;
        }

        List<IrExpression> arguments = [];
        foreach (var argument in call.Arguments)
        {
            if (_BindExpression(argument) is not { } bound) return null;
            arguments.Add(bound);
        }
        return new IrNativeCall(native.Span, gear.ReturnType, name.Name.Name, arguments);
    }
}
