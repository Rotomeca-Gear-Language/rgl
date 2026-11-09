using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les appels de fonctions C (<see cref="IrNativeCall"/>) : les valeurs RGL passent la frontière une par une, sans rien supposer de la disposition
/// des <c>struct</c> en mémoire ni du nom des types C que le code généré n'écrit jamais.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// <c>::f(arguments)</c> : une <c>string</c> est donnée par son texte terminé par zéro, un mold par une liste d'initialisation de ses champs (le paramètre C
    /// en déduit son type), une enum par un proxy qui se convertit en la sienne. Le résultat est converti de même dans l'autre sens, et un texte C est copié.
    /// </summary>
    private string _NativeCall(IrNativeCall call, Func<IrExpression, string> operand)
    {
        var arguments = string.Join(", ", call.Arguments.Select(a => _ToC(operand(a), a.Type)));
        return _FromC($"::{call.Name}({arguments})", call.Type);
    }

    /// <summary>La valeur RGL <paramref name="expression"/> telle que la reçoit une fonction C.</summary>
    private string _ToC(string expression, IrType type)
    {
        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.String }:
                return $"({expression}).data()";

            case EnumType:
                _includes.Add("rgl/standard/native.hpp");
                return $"rgl::standard::native::enum_value{{static_cast<long long>({expression})}}";

            case MoldType mold:
                // Les champs sont lus un à un : la liste initialise le struct C dans l'ordre de ses champs, qui est celui du mold.
                return "{" + string.Join(", ", mold.Symbol.Fields.Select(f => _ToC($"({expression}).rgl_f_{f.Name}", f.Type))) + "}";

            default:
                return expression;
        }
    }

    /// <summary>La valeur <paramref name="expression"/> renvoyée par une fonction C, en valeur RGL.</summary>
    private string _FromC(string expression, IrType type)
    {
        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.String }:
                // Le constructeur copie le texte, et une adresse nulle donne la chaîne vide.
                _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
                return $"rgl::standard::string({expression})";

            case EnumType:
                return $"static_cast<{_Type(type)}>(static_cast<long long>({expression}))";

            case MoldType mold:
                return $"([&](const auto& rgl_c) {{ return {_FromCFields("rgl_c", mold.Symbol, type)}; }})({expression})";

            default:
                return expression;
        }
    }

    private string _FromCFields(string source, MoldSymbol mold, IrType type) =>
        $"{_Type(type)}{{{string.Join(", ", mold.Fields.Select(f => f.Type is MoldType nested ? _FromCFields($"{source}.{f.Name}", nested.Symbol, f.Type) : _FromCField($"{source}.{f.Name}", f.Type)))}}}";

    private string _FromCField(string expression, IrType type) =>
        type is EnumType ? $"static_cast<{_Type(type)}>(static_cast<long long>({expression}))" : expression;
}
