using Rgl.AST.Gears;
using Rgl.Diagnostics;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.AST.Extensions;
using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.IR.Expressions;
using Rgl.Text;
using System.Text.RegularExpressions;

namespace Rgl.Binding;

/// <summary>
/// Les composants écrits en C++ : un guard déclaré sans corps (<c>guard normalize(string s): string;</c>) dont le fichier <c>normalize.rgl.cpp</c>, à côté du
/// fichier forge, donne l'implémentation ; ou un membre de descriptor (<c>forge: descriptor</c>), dont le C++ est écrit dans le fichier forge. Le C++ s'exécute dans
/// une unité compilée avec les exceptions ; celle qu'il lance est une <c>NativeError</c>, la seule erreur du guard.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Le mold <c>NativeError</c> de la std ; <see langword="null"/> sans la std.</summary>
    private MoldSymbol? _nativeError;

    /// <summary>
    /// Les noms des composants déjà liés : la fonction C++ d'un composant s'appelle <c>rgl_native::nom</c> dans son unité, et deux unités du même programme qui la
    /// définiraient toutes deux ne se lieraient pas.
    /// </summary>
    private readonly HashSet<string> _nativeNames = [];

    /// <summary>Les fichiers <c>.rgl.cpp</c> du module en cours, par nom de gear.</summary>
    private IReadOnlyDictionary<string, string>? _nativeSources;

    /// <summary>Le C++ des membres de descriptor du module en cours, par position du nom du gear dans le texte des liaisons.</summary>
    private IReadOnlyDictionary<int, NativeBody>? _nativeBodies;
    private readonly Dictionary<SourceText, IReadOnlyDictionary<int, NativeBody>> _nativeMetadata = [];
    private readonly List<(ExtendDeclaration Declaration, Namespace Namespace)> _descriptorTemplates = [];
    private readonly List<IrGear> _instantiatedNativeGears = [];

    // La disposition des tableaux natifs est explicite, sans modifier les autres tableaux du programme.
    private static IrType _NativeBoundaryType(IrType type) =>
        type is ArrayType array
            ? array with { Element = _NativeBoundaryType(array.Element), Packed = array.Packed || array.Element is MoldType && array.Kind == ArrayKind.Dynamic }
            : type;

    private IrExpression? _BindPack(CallExpression call)
    {
        if (call.Arguments.Count != 1)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, "pack attend exactement un tableau", "pack(tableau)");
            return null;
        }
        var value = _BindExpression(call.Arguments[0]);
        if (value is null) return null;
        if (value.Type is not ArrayType array)
        {
            _Error(call.Span, DiagnosticCodes.TypeMismatch, "pack attend un tableau", "pas un tableau");
            return null;
        }
        return new IrArrayPack(call.Span, (ArrayType)_NativeBoundaryType(array), value);
    }

    private NativeBody? _NativeBodyFor(GearDeclaration gear)
    {
        var body = _nativeMetadata.GetValueOrDefault(gear.Name.Span.Source)?.GetValueOrDefault((int)gear.Name.Span.Start.Value);
        if (body is null || _generic is null) return body;
        string Replace(string value)
        {
            foreach (var (name, type) in _generic.Types)
                value = Regex.Replace(value, @"\b" + Regex.Escape(name) + @"\b", _ => _NativeTypeName(type));
            return value;
        }
        return body with { Expression = Replace(body.Expression), Classes = body.Classes.ToDictionary(p => p.Key, p => Replace(p.Value)) };
    }

    private static string _NativeTypeName(IrType type) => type switch
    {
        PrimitiveType { Kind: PrimitiveKind.Int } => "std::int32_t",
        PrimitiveType { Kind: PrimitiveKind.UInt } => "std::uint32_t",
        PrimitiveType { Kind: PrimitiveKind.Long } => "std::int64_t",
        PrimitiveType { Kind: PrimitiveKind.ULong } => "std::uint64_t",
        PrimitiveType { Kind: PrimitiveKind.Short } => "std::int16_t",
        PrimitiveType { Kind: PrimitiveKind.UShort } => "std::uint16_t",
        PrimitiveType { Kind: PrimitiveKind.Byte } => "std::int8_t",
        PrimitiveType { Kind: PrimitiveKind.UByte } => "std::uint8_t",
        PrimitiveType { Kind: PrimitiveKind.String } => "rgl::standard::string",
        MoldType { Symbol.NativeCppType: { } cpp } => cpp,
        MoldType mold => "rgl_m_" + mold.Symbol.CppName,
        EnumType enumeration => "rgl_e_" + enumeration.Symbol.CppName,
        IngotType ingot => _NativeTypeName(ingot.Underlying),
        ArrayType array => array.Kind switch
        {
            ArrayKind.Fixed => $"rgl::standard::fixed_array<{_NativeTypeName(array.Element)}, {array.Size}>",
            ArrayKind.Bounded => $"rgl::standard::bounded_array<{_NativeTypeName(array.Element)}, {array.Size}, {array.Max}>",
            ArrayKind.NonEmpty => $"rgl::standard::non_empty_array<{_NativeTypeName(array.Element)}>",
            _ => $"rgl::standard::array<{_NativeTypeName(array.Element)}>",
        },
        _ => type.ToString(),
    };

    /// <summary>
    /// Un guard sans corps : il n'a rien à lier, son C++ est ailleurs. Ses types sont ceux que le C++ de la liaison reçoit sans conversion (les entiers, les
    /// flottants, <c>bool</c>, <c>string</c>, et les types décrits par un descriptor du même fichier) ; sa seule erreur est la <c>NativeError</c> d'une exception.
    /// Le gear de fin d'un type décrit n'est pas un guard : il ne peut pas échouer.
    /// </summary>
    private IrGear? _BindNativeGear(GearDeclaration gear, GearSymbol symbol)
    {
        if (!_nativePolicy.AllowsCpp(gear.Span.Source.Path))
            _Error(gear.Span, DiagnosticCodes.NativePolicy, "cette liaison est interdite par no-cpp", "C++ non autorisé");
        if (symbol.NativeSource is null && symbol.NativeBody is null)
        {
            _Error(gear.Name.Span, DiagnosticCodes.NativeComponent, $"« {symbol.Name} » est déclaré sans corps, mais {symbol.Name}.rgl.cpp est introuvable", "pas de C++",
                $"un guard sans corps est implémenté par le fichier {symbol.Name}.rgl.cpp, écrit à côté du fichier forge qui le déclare");
            return null;
        }
        if (_nativeError is null && symbol.Kind == GearKind.Guard)
        {
            _Unsupported(gear.Name.Span, "un composant écrit en C++ sans la bibliothèque standard, qui définit NativeError");
            return null;
        }

        if (symbol.NativeSource is not null && !_nativeNames.Add(symbol.Name))
        {
            _Error(gear.Name.Span, DiagnosticCodes.NativeComponent, $"un autre composant écrit en C++ s'appelle déjà « {symbol.Name} »", "nom déjà pris",
                "la fonction C++ d'un composant s'appelle rgl_native::nom : deux composants du même programme ne peuvent pas porter le même nom");
            return null;
        }

        var valid = true;
        foreach (var parameter in symbol.Parameters.Where(p => !_IsNativeType(p.Type, symbol.NativeBody)))
        {
            _Error(parameter.Declaration, DiagnosticCodes.NativeComponent, $"le paramètre « {parameter.Name} » de « {symbol.Name} » est un « {parameter.Type} » : un composant en C++ ne reçoit pas encore ce type",
                "type pas encore pris en charge", "les scalaires, string, enums, ingots, molds et tableaux sans cycle de vie passent la frontière");
            valid = false;
        }
        if (symbol.ReturnType != PrimitiveType.Void && !_IsNativeType(symbol.ReturnType, symbol.NativeBody))
        {
            _Error(gear.ReturnType.Span, DiagnosticCodes.NativeComponent, $"« {symbol.Name} » renvoie un « {symbol.ReturnType} » : un composant en C++ ne renvoie pas encore ce type",
                "type pas encore pris en charge", "les scalaires, string, void, enums, ingots, molds et tableaux sans cycle de vie passent la frontière");
            valid = false;
        }
        if (!valid) return null;

        _calls[symbol] = [];
        if (symbol.Kind == GearKind.Guard) _guardErrors[symbol] = ([new MoldType(_nativeError!)], []);
        var bound = new IrGear(gear.Span, symbol, new IrBlock(gear.Span, []));
        _boundGears[symbol] = bound;
        return bound;
    }

    /// <summary>
    /// Vrai pour un type que le C++ d'un composant reçoit : entier, flottant, <c>bool</c> ou <c>string</c>, sans bornes ; et, pour un membre de descriptor, un type décrit par
    /// un descriptor du fichier (le C++ n'en voit que l'objet).
    /// </summary>
    private static bool _IsNativeType(IrType type, NativeBody? body) => _IsNativeType(type, body, []);

    private static bool _IsNativeType(IrType type, NativeBody? body, HashSet<MoldSymbol> visiting) =>
        type is PointerType pointer && (pointer.Element == PrimitiveType.Void || _IsNativeType(pointer.Element, body, visiting))
        || type == PrimitiveType.Char
        || type is PrimitiveType { Bounds: null, Kind: PrimitiveKind.Byte or PrimitiveKind.UByte or PrimitiveKind.Short or PrimitiveKind.UShort or PrimitiveKind.Int or PrimitiveKind.UInt
            or PrimitiveKind.Long or PrimitiveKind.ULong or PrimitiveKind.Float or PrimitiveKind.Double or PrimitiveKind.Bool or PrimitiveKind.String }
        || (body is not null && type is MoldType { Symbol: var described } && (described.NativeCppType is not null || described.Name is { } name && body.Classes.ContainsKey(name)))
        || type is EnumType
        || type is IngotType ingot && _IsNativeType(ingot.Underlying, body, visiting)
        || type is ArrayType array && _IsNativeType(array.Element, body, visiting) && !LifecycleRules.Has(array.Element)
        || type is MoldType mold && !mold.Symbol.HasLifecycle && visiting.Add(mold.Symbol) && mold.Symbol.Fields.All(f => _IsNativeType(f.Type, body, new HashSet<MoldSymbol>(visiting)));
    private void _InstantiateNativeDescriptor(GenericMold generic, GenericEnvironment environment, MoldSymbol symbol)
    {
        var declaration = generic.Declaration;
        var metadata = _nativeMetadata.GetValueOrDefault(declaration.Name.Span.Source)?.Values.FirstOrDefault(b => b.Classes.ContainsKey(declaration.Name.Name));
        if (metadata is null) return;
        var cpp = metadata.Classes[declaration.Name.Name];
        foreach (var (name, type) in environment.Types)
            cpp = Regex.Replace(cpp, @"\b" + Regex.Escape(name) + @"\b", _ => _NativeTypeName(type));
        symbol.NativeCppType = cpp;
        symbol.NativeTypeArguments = environment.Types.Values.ToArray();
        symbol.DeclaresDestructor = true;
        var moldType = new MoldType(symbol);
        var closer = new GearSymbol(symbol.Name + ".__fermer", GearKind.Gear,
            [new ParameterSymbol("c", moldType, declaration.Name.Span)], PrimitiveType.Void, false, declaration.Name.Span)
        {
            CppNameOverride = symbol.CppName + "__fermer",
            NativeBody = new NativeBody(NativeBodyKind.Destroy, "", 0, new Dictionary<string, string> { [symbol.Name!] = cpp }, metadata.Headers),
        };
        symbol.Destructor = closer;
        var closeBody = new IrGear(declaration.Span, closer, new IrBlock(declaration.Span, []));
        _boundGears[closer] = closeBody;
        _calls[closer] = [];
        _instantiatedNativeGears.Add(closeBody);

        _InGeneric(generic.Namespace, environment, () =>
        {
            var extension = new Extension(moldType, null);
            _typeExtensions.Add(extension);
            foreach (var template in _descriptorTemplates.Where(t => t.Namespace.Module == generic.Namespace.Module
                && t.Declaration.Target is AST.Types.NamedType target && target.Name.Name == declaration.Name.Name))
            {
                foreach (var member in template.Declaration.Members.OfType<GearDeclaration>())
                {
                    _DeclareExtensionGear(extension, symbol.Name, false, member);
                    if (extension.Gears.TryGetValue(member.Name.Name, out var bound)
                        && bound.LastOrDefault(g => g.Gear.Declaration == member.Name.Span).Gear is { } gear
                        && _BindNativeGear(member, gear) is { } body) _instantiatedNativeGears.Add(body);
                }
            }
            return true;
        });
    }

    private IrExpression? _TryDescriptorCall(CallExpression call, MemberAccessExpression member, out bool handled)
    {
        handled = false;
        var named = _NamedTypeOf(member.Target);
        var type = named ?? _SyntacticType(member.Target);
        if (type is null) return null;
        var candidates = _VisibleExtensions().Where(e => e.Target == type).SelectMany(e => e.Gears.Values).SelectMany(g => g)
            .Where(g => g.Gear.NativeBody?.PublicName == member.Member.Name && g.IsMethod == (named is null))
            .Select(g => g.Gear).ToArray();
        if (candidates.Length == 0) return null;
        handled = true;
        var receiver = named is null ? _BindExpression(member.Target) : null;
        if (named is null && receiver is null) return null;
        var offset = named is null ? 1 : 0;
        var arity = candidates.Where(g => g.Parameters.Count == call.Arguments.Count + offset).ToArray();
        var arguments = call.Arguments.Select((argument, i) => _BindExpected(argument, arity.Length == 1 ? arity[0].Parameters[i + offset].Type : null)).ToArray();
        if (arguments.Any(a => a is null)) return null;
        if (arity.Length == 1) return _BindGearCall(call.Span, member.Member, receiver, arguments, resolved: arity[0]);
        var scored = arity.Select(g => (Gear: g, Score: arguments.Select((arg, i) => arg!.Type == g.Parameters[i + offset].Type ? 0
            : _CanCoerce(arg, g.Parameters[i + offset].Type) ? 1 : 1000).Sum())).OrderBy(g => g.Score).ToArray();
        if (scored.Length == 0 || scored[0].Score >= 1000 || scored.Length > 1 && scored[0].Score == scored[1].Score)
        {
            _Error(member.Member.Span, DiagnosticCodes.NativeComponent, $"aucune surcharge unique de « {member.Member.Name} » ne correspond aux arguments", "surcharge native",
                "vérifie le nombre et les types des arguments ; une conversion explicite peut départager deux surcharges");
            return null;
        }
        return _BindGearCall(call.Span, member.Member, receiver, arguments, resolved: scored[0].Gear);
    }

}
