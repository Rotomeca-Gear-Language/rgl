using System.Globalization;
using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Les noms et les types C++ : préfixes des noms RGL, noms uniques des variables, et type C++
/// de chaque type RGL (qui note au passage l'en-tête du runtime qu'il demande).
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Nom C++ d'un paramètre : préfixé, pour ne jamais heurter un mot-clé du C++ (<c>this</c>, <c>register</c>…).</summary>
    private static string _Name(ParameterSymbol parameter) => "rgl_p_" + parameter.Name;

    /// <summary>
    /// Nom C++ d'une nouvelle variable : <c>rgl_v_nom</c>, puis <c>rgl_v_nom_2</c>, <c>rgl_v_nom_3</c>…
    /// pour les variables suivantes de même nom.
    /// </summary>
    private string _Declare(VariableSymbol variable)
    {
        var count = _nameCounts.GetValueOrDefault(variable.Name) + 1;
        _nameCounts[variable.Name] = count;

        var name = count == 1 ? "rgl_v_" + variable.Name : $"rgl_v_{variable.Name}_{count}";
        _variableNames[variable] = name;
        return name;
    }

    /// <summary>Type C++ d'un type RGL ; note au passage l'en-tête du runtime qu'il demande.</summary>
    private string _Type(IrType type)
    {
        switch (type)
        {
            case PointerType pointer:
                _includes.Add("rgl/standard/pointer.hpp");
                return $"rgl::standard::pointer<{_Type(pointer.Element)}, {(pointer.Owned ? "true" : "false")}>";
            case PrimitiveType { Kind: PrimitiveKind.Char }: return "char";
            case PrimitiveType { Kind: PrimitiveKind.String }:
                _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
                return "rgl::standard::string";

            case PrimitiveType { Kind: PrimitiveKind.MString }:
                _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
                return "rgl::standard::mstring";

            case PrimitiveType { Kind: PrimitiveKind.FileStream }:
                _Use(RuntimeComponent.File, "rgl/standard/file.hpp");
                return "rgl::standard::file::stream";

            case PrimitiveType { Kind: PrimitiveKind.Regex }:
                // Le moteur (CTRE) et la valeur regex : l'en-tête seul, les chaînes et tableaux demandent leurs composants.
                _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
                _Use(RuntimeComponent.Memory, "rgl/standard/array.hpp");
                _includes.Add("rgl/standard/regex.hpp");
                return "rgl::standard::regex";

            case PrimitiveType { Kind: PrimitiveKind.DateTime }:
                _includes.Add("rgl/standard/datetime.hpp");
                return "rgl::standard::datetime";

            case PrimitiveType { Kind: PrimitiveKind.Duration }:
                _includes.Add("rgl/standard/duration.hpp");
                return "rgl::standard::duration";

            case PrimitiveType { Kind: PrimitiveKind.Symbol }:
                // Seulement l'en-tête : les littéraux n'ont pas besoin de la table des symboles.
                _includes.Add("rgl/standard/symbol.hpp");
                return "rgl::standard::symbol";

            case ArrayType array when _IsSoa(array):
                // Un [mold] en colonnes ; son Layout est écrit avec les structs.
                _Use(RuntimeComponent.Memory, "rgl/standard/soa.hpp");
                _Type(array.Element);
                _soaMolds.Add(_SoaMold(array));
                // Un [mold;N] est un bloc de N éléments dont les colonnes sont rangées dans l'objet ; [[mold;N]] est un array de blocs (AoSoA).
                return array.Kind == ArrayKind.Fixed
                    ? $"rgl::standard::soa_fixed_array<{_LayoutName(_SoaMold(array))}, {array.Size}>"
                    : $"rgl::standard::soa_array<{_LayoutName(_SoaMold(array))}>";

            case ArrayType array:
                // array<T> alloue avec l'allocateur par défaut du runtime (composant Memory).
                _Use(RuntimeComponent.Memory, "rgl/standard/array.hpp");
                var element = _Type(array.Element);
                return array.Kind switch
                {
                    ArrayKind.Fixed => $"rgl::standard::fixed_array<{element}, {array.Size}>",
                    ArrayKind.Bounded => $"rgl::standard::bounded_array<{element}, {array.Size}, {array.Max}>",
                    ArrayKind.NonEmpty => $"rgl::standard::non_empty_array<{element}>",
                    _ => $"rgl::standard::array<{element}>",
                };

            case DictionaryType dictionary:
                _Use(RuntimeComponent.Memory, "rgl/standard/dictionary.hpp");
                return $"rgl::standard::dictionary<{_Type(dictionary.Key)}, {_Type(dictionary.Value)}>";

            case FunctionType function:
                // Une grande lambda est rangée sur le tas, par l'allocateur par défaut (composant Memory).
                _Use(RuntimeComponent.Memory, "rgl/standard/function.hpp");
                var returned = function.Return == PrimitiveType.Void ? "void" : _Type(function.Return);
                return $"rgl::standard::function<{returned}({string.Join(", ", function.Parameters.Select(_ParameterType))})>";

            case TupleType tuple:
                _includes.Add("rgl/standard/tuple.hpp");
                return $"rgl::standard::tuple<{string.Join(", ", tuple.Elements.Select(_Type))}>";

            case MoldType mold:
                _usedMolds.Add(mold.Symbol);
                return _moldNames[mold.Symbol];

            case EnumType enumeration:
                _usedEnums.Add(enumeration.Symbol);
                return $"rgl_e_{enumeration.Symbol.CppName}";

            case NullableType nullable:
                _includes.Add("rgl/standard/nullable.hpp");
                // Une valeur que le programme n'utilise jamais tient lieu de null : pas d'indicateur à côté de la valeur.
                return _NicheOf(nullable.Inner) is { } niche
                    ? $"rgl::standard::niche_nullable<{_Type(nullable.Inner)}, {niche}>"
                    : $"rgl::standard::nullable<{_Type(nullable.Inner)}>";

            case UnionType union:
                _includes.Add("rgl/standard/union.hpp");
                return $"rgl::standard::union_of<{string.Join(", ", union.Members.Select(_Type))}>";

            case ResultType result:
                _includes.Add("rgl/standard/union.hpp");
                return $"rgl::standard::result<{_Type(result.Value)}, {_Type(result.Error)}>";

            // Une promesse : la poignée d'une tâche, qui donnera ce que `await` donne.
            case PromiseType promise:
                _includes.Add("rgl/standard/async.hpp");
                return $"rgl::standard::promise<{_Type(promise.Awaited)}>";

            // Un ingot n'existe qu'en RGL : en C++, c'est sa base.
            case IngotType ingot:
                return _Type(ingot.Base);
        }

        return type switch
        {
            PrimitiveType { Kind: PrimitiveKind.Byte } => "std::int8_t",
            PrimitiveType { Kind: PrimitiveKind.UByte } => "std::uint8_t",
            PrimitiveType { Kind: PrimitiveKind.Short } => "std::int16_t",
            PrimitiveType { Kind: PrimitiveKind.UShort } => "std::uint16_t",
            PrimitiveType { Kind: PrimitiveKind.Int } => "std::int32_t",
            PrimitiveType { Kind: PrimitiveKind.UInt } => "std::uint32_t",
            PrimitiveType { Kind: PrimitiveKind.Long } => "std::int64_t",
            PrimitiveType { Kind: PrimitiveKind.ULong } => "std::uint64_t",
            PrimitiveType { Kind: PrimitiveKind.Float } => "float",
            PrimitiveType { Kind: PrimitiveKind.Double } => "double",
            PrimitiveType { Kind: PrimitiveKind.Bool } => "bool",
            _ => throw new InvalidOperationException($"Type sans traduction : {type}. Ajouter son cas dans CppEmitter."),
        };
    }

    /// <summary>Chemin du fichier RGL en littéral de chaîne C++, pour les <c>#line</c>.</summary>
    private static string _Quote(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
