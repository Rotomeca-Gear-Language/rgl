using System.Text;
using System.Text.RegularExpressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les composants écrits en C++ : un guard sans corps dont <c>nom.rgl.cpp</c> donne l'implémentation, ou un membre de descriptor dont le fichier forge écrit le C++. Le C++
/// généré du programme est compilé sans exceptions : il n'appelle jamais le C++ du composant directement, mais par <c>rgl_native_guard</c> (composant
/// <see cref="RuntimeComponent.Native"/>), qui attrape ce qu'il lance. Le C++ du composant et son appel (<c>thunk</c>) vivent dans une <b>unité</b> à part, compilée avec
/// les exceptions : aucune exception ne traverse donc du code compilé sans.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Les unités C++ à compiler avec les exceptions, une par composant.</summary>
    private readonly List<NativeUnit> _nativeUnits = [];
    private readonly List<GearSymbol> _nativeUnitGears = [];

    /// <summary>Le nom C++ du namespace où <c>nom.rgl.cpp</c> définit sa fonction : <c>rgl_native::nom(…)</c>.</summary>
    private const string NativeNamespace = "rgl_native";

    [GeneratedRegex(@"\$arg(\d+)")]
    private static partial Regex ArgumentPlaceholder();

    /// <summary>Le type C++ d'un objet décrit par un descriptor, si <paramref name="type"/> en est un ; <see langword="null"/> sinon.</summary>
    private static string? _DescribedClass(NativeBody? body, IrType type) =>
        type is MoldType { Symbol.NativeCppType: { } specialized } ? specialized
        : body is not null && type is MoldType { Symbol.Name: { } name } && body.Classes.TryGetValue(name, out var cpp) ? cpp : null;

    /// <summary>Le type que le contexte donne à un paramètre ou au résultat : l'adresse de l'objet pour un type décrit, sinon celui du gear.</summary>
    private string _ContextType(GearSymbol gear, IrType type, ParameterSymbol? parameter) =>
        type == PrimitiveType.Void ? "void"
        : type is PointerType pointer ? _Type(pointer.Element) + "*"
        : _DescribedClass(gear.NativeBody, type) is not null ? "std::uint64_t"
        : parameter is null ? _Type(type) : _ParameterType(parameter);

    /// <summary>
    /// Le guard d'un composant : il range ses arguments dans un contexte, appelle l'unité C++ par <c>rgl_native_guard</c> et, si une exception a été lancée, échoue avec
    /// une <c>NativeError</c> (le texte de l'exception, la position RGL de l'appel — passée en dernier argument par l'appelant —, le nom de la fonction). Le gear de fin
    /// d'un type décrit n'est pas un guard : une exception de son destructeur est ignorée.
    /// </summary>
    private static readonly (string Header, RuntimeComponent Component)[] _runtimeHeaders =
    [
        ("rgl/standard/arguments.hpp", RuntimeComponent.Arguments),
        ("rgl/standard/console.hpp", RuntimeComponent.Console),
        ("rgl/standard/file.hpp", RuntimeComponent.File),
        ("rgl/standard/process.hpp", RuntimeComponent.Process),
        ("rgl/standard/net.hpp", RuntimeComponent.Net),
        ("rgl/standard/system.hpp", RuntimeComponent.System),
        ("rgl/standard/child.hpp", RuntimeComponent.Child),
        ("rgl/standard/unicode.hpp", RuntimeComponent.Unicode),
        ("rgl/standard/http.hpp", RuntimeComponent.Http),
        ("rgl/standard/http_tls.hpp", RuntimeComponent.HttpTls),
    ];

    private void _NativeGear(StringBuilder output, GearSymbol gear)
    {
        _Use(RuntimeComponent.Native, "rgl/standard/native.hpp");
        // Un bloc qui inclut l'en-tête d'un composant du runtime le fait lier au programme.
        if (gear.InlineCpp?.Text is { } blockText)
            foreach (var (header, component) in _runtimeHeaders)
                if (blockText.Contains(header, StringComparison.Ordinal)) _Use(component, header);
        var context = $"rgl_nat_{gear.CppName}_ctx";
        var thunk = $"rgl_nat_{gear.CppName}_thunk";
        var isVoid = gear.ReturnType == PrimitiveType.Void;
        var isGuard = gear.Kind == GearKind.Guard;
        var describedResult = _DescribedClass(gear.NativeBody, gear.ReturnType);

        // Le contexte est le même des deux côtés : défini dans le programme et dans l'unité, il porte les arguments et le résultat.
        var definition = new StringBuilder($"struct {context} {{\n");
        foreach (var (parameter, index) in gear.Parameters.Select((p, i) => (p, i)))
            definition.Append(Indent).Append(_ContextType(gear, parameter.Type, parameter)).Append(" a").Append(index).Append(";\n");
        if (!isVoid) definition.Append(Indent).Append(_ContextType(gear, gear.ReturnType, null)).Append(" result{};\n");
        definition.Append("};\n");

        output.Append(definition).Append("extern \"C\" void ").Append(thunk).Append("(void*);\n");
        output.Append(_Signature(gear)).Append(" {\n");
        var arguments = string.Join(", ", gear.Parameters.Select(p => p.Type is PointerType ? _Name(p) + ".get()" : _DescribedClass(gear.NativeBody, p.Type) is null ? _Name(p) : $"{_Name(p)}.rgl_f_handle"));
        output.Append(Indent).Append(context).Append(" rgl_context{").Append(arguments).Append("};\n");
        output.Append(Indent).Append("rgl_native_status rgl_status;\n");

        var call = $"rgl_native_guard(&{thunk}, &rgl_context, &rgl_status) != 0";
        if (isGuard)
        {
            var error = gear.Errors!;
            var message = "rgl::standard::string(rgl_status.message)";
            var callee = _String(new IR.Expressions.IrStringConstant(gear.Declaration, PrimitiveType.String, gear.NativeBody?.PublicName is { } publicName ? gear.Name[..(gear.Name.LastIndexOf('.') + 1)] + publicName : gear.Name));
            output.Append(Indent).Append("if (").Append(call).Append(") return ")
                .Append(_FailWith($"{_Type(error)}{{{message}, rgl::standard::string(rgl_where), {callee}}}", error)).Append(";\n");
            output.Append(Indent).Append("return ").Append(isVoid ? _GuardSuccess() : gear.ReturnType is PointerType ? _Type(gear.ReturnType) + "(rgl_context.result)" : describedResult is not null ? $"{_Type(gear.ReturnType)}{{rgl_context.result}}" : "std::move(rgl_context.result)").Append(";\n}\n\n");
        }
        else if (gear.DeclaredUnsafe)
        {
            _includes.Add("cstdlib");
            _includes.Add("utility");
            output.Append(Indent).Append("if (").Append(call).Append(") std::abort();\n");
            if (!isVoid) output.Append(Indent).Append("return ").Append(gear.ReturnType is PointerType ? _Type(gear.ReturnType) + "(rgl_context.result)" : "std::move(rgl_context.result)").Append(";\n");
            output.Append("}\n\n");
        }
        else output.Append(Indent).Append("static_cast<void>(").Append(call).Append(");\n}\n\n");

        // L'unité : les en-têtes, le contexte, ce que le C++ du composant définit ou déclare, puis l'appel.
        _NativeBoundaryDeclarations(gear); // Enregistrer les types avant les structs du programme.
        var unit = new StringBuilder();
        unit.Append("// Généré par rgl : l'unité C++ du composant « ").Append(gear.Name).Append(" » (compilée avec les exceptions).\n");
        unit.Append("#include <cstdint>\n#include \"rgl/standard/native.hpp\"\n");
        if (gear.Parameters.Any(p => p.Type == PrimitiveType.String) || gear.ReturnType == PrimitiveType.String) unit.Append("#include \"rgl/standard/string.hpp\"\n");
        foreach (var header in gear.NativeBody?.Headers ?? []) unit.Append("#include \"").Append(header.Replace('\\', '/')).Append("\"\n");
        unit.Append("/*RGL_NATIVE_INCLUDES*/\n/*RGL_NATIVE_BOUNDARY*/\n").Append(definition).Append('\n');

        string body;
        if (gear.InlineCpp is { } inline)
        {
            var ns = "rgl::internal::cpp::block_" + gear.CppName;
            unit.Append("#include <type_traits>\n");
            var aliases = new StringBuilder();
            foreach (var parameter in gear.Parameters)
                aliases.Append("using rgl_arg_").Append(parameter.Name).Append(" = ").Append(parameter.Type is PointerType ? _ContextType(gear, parameter.Type, null) : _ParameterType(parameter)).Append(";\n");
            aliases.Append("using rgl_return = ").Append(_ContextType(gear, gear.ReturnType, null)).Append(";\n");
            unit.Append("namespace ").Append(ns).Append(" {\n").Append(aliases).Append("}\n");
            unit.Append(_ScopedCpp(inline, ns));
            var expected = string.Join(", ", gear.Parameters.Select(p => p.Type is PointerType ? _ContextType(gear, p.Type, null) : _ParameterType(p)));
            unit.Append("using rgl_expected = ").Append(_ContextType(gear, gear.ReturnType, null)).Append(" (*)(").Append(expected).Append(");\n")
                .Append("static_assert(std::is_convertible_v<decltype(&").Append(ns).Append("::rgl), rgl_expected>, \"La signature C++ rgl ne correspond pas à celle du bloc RGL\");\n");
            var userCall = $"{ns}::rgl({string.Join(", ", gear.Parameters.Select((_, i) => $"rgl_c.a{i}"))})";
            body = isVoid ? $"{userCall};" : $"rgl_c.result = {userCall};";
        }
        else if (gear.NativeBody is { } native) body = _DescriptorThunkBody(gear, native, describedResult);
        else
        {
            var declaredParameters = string.Join(", ", gear.Parameters.Select(p => $"{(p.Type is PointerType ? _ContextType(gear, p.Type, null) : _ParameterType(p))} {_Name(p)}"));
            unit.Append("namespace ").Append(NativeNamespace).Append(" {\n")
                .Append(Indent).Append(isVoid ? "void" : _ContextType(gear, gear.ReturnType, null)).Append(' ').Append(gear.Name).Append('(').Append(declaredParameters).Append(");   // à définir dans ").Append(gear.Name).Append(".rgl.cpp\n")
                .Append("}\n\n");
            var source = gear.NativeSource!.Replace('\\', '/');
            unit.Append("#line 1 \"").Append(source).Append("\"\n#include \"").Append(source).Append("\"\n\n");
            var userCall = $"{NativeNamespace}::{gear.Name}({string.Join(", ", gear.Parameters.Select((_, i) => $"rgl_c.a{i}"))})";
            body = isVoid ? $"{userCall};" : $"rgl_c.result = {userCall};";
        }

        unit.Append("extern \"C\" void ").Append(thunk).Append("(void* rgl_context) {\n")
            .Append(Indent).Append("[[maybe_unused]] auto& rgl_c = *static_cast<").Append(context).Append("*>(rgl_context);\n")
            .Append(Indent).Append(body).Append('\n')
            .Append("}\n");

        var sourcePath = gear.InlineCpp?.Source.Path ?? gear.NativeSource ?? gear.NativeBody!.Headers.FirstOrDefault() ?? gear.Name;
        var includeDirectories = (gear.NativeBody?.Headers ?? []).Select(h => Path.GetDirectoryName(h) ?? "").Where(d => d.Length > 0).Distinct().ToArray();
        _nativeUnitGears.Add(gear);
        _nativeUnits.Add(new NativeUnit(gear.CppName, unit.ToString(), sourcePath, includeDirectories));
    }

    private void _FinalizeNativeUnits()
    {
        for (var i = 0; i < _nativeUnits.Count; ++i)
        {
            var boundary = _NativeBoundaryDeclarations(_nativeUnitGears[i]);
            var includes = string.Join("\n", _includes.Order().Select(include => $"#include \"{include}\""));
            _nativeUnits[i] = _nativeUnits[i] with { Code = _nativeUnits[i].Code.Replace("/*RGL_NATIVE_INCLUDES*/", includes).Replace("/*RGL_NATIVE_BOUNDARY*/", boundary) };
        }
    }

    private string _NativeBoundaryDeclarations(GearSymbol gear)
    {
        var molds = new HashSet<MoldSymbol>();
        var enums = new HashSet<EnumSymbol>();
        var described = new HashSet<MoldSymbol>();
        var layouts = new HashSet<MoldSymbol>();
        void Visit(IrType type)
        {
            if (_DescribedClass(gear.NativeBody, type) is not null)
            {
                if (type is MoldType mold && described.Add(mold.Symbol))
                    foreach (var argument in mold.Symbol.NativeTypeArguments) Visit(argument);
                return;
            }
            _Type(type);
            switch (type)
            {
                case PointerType pointer: Visit(pointer.Element); break;
                case NullableType nullable: Visit(nullable.Inner); break;
                case IngotType ingot: Visit(ingot.Underlying); break;
                case EnumType enumeration: enums.Add(enumeration.Symbol); break;
                case ArrayType array:
                    if (_IsSoa(array)) layouts.Add(_SoaMold(array));
                    Visit(array.Element);
                    break;
                case MoldType mold when molds.Add(mold.Symbol):
                    foreach (var field in mold.Symbol.Fields) Visit(field.Type);
                    break;
            }
        }
        foreach (var parameter in gear.Parameters) Visit(parameter.Type);
        if (gear.ReturnType != PrimitiveType.Void) Visit(gear.ReturnType);
        var declarations = _Enums(enums.ToArray()) + _Structs(molds.ToArray(), allLayouts: false);
        // Les layouts des champs sont déjà écrits par _Structs ; ceux d'une cible de pointeur aussi doivent exister dans l'unité.
        layouts.ExceptWith(molds.SelectMany(m => m.Fields.SelectMany(f => _SoaMoldsIn(f.Type))));
        return declarations + string.Concat(layouts.Select(_Layout));
    }

    /// <summary>
    /// Le corps de l'appel d'un membre de descriptor : l'expression C++ écrite, où <c>$self</c> est l'objet de l'instance et <c>$argN</c> le N-ième argument (celui d'un type décrit
    /// est lui aussi l'objet) ; le résultat d'un type décrit est un objet créé par copie du résultat de l'expression, dont le programme garde l'adresse.
    /// </summary>
    private string _DescriptorThunkBody(GearSymbol gear, NativeBody native, string? describedResult)
    {
        string Object(int index) => _DescribedClass(native, gear.Parameters[index].Type) is { } cpp
            ? $"(*reinterpret_cast<{cpp}*>(rgl_c.a{index}))"
            : $"rgl_c.a{index}";

        if (native.Kind == NativeBodyKind.Destroy)
            return $"delete reinterpret_cast<{_DescribedClass(native, gear.Parameters[0].Type)}*>(rgl_c.a0);";

        var offset = native.SelfIndex >= 0 ? 1 : 0;
        var expression = native.SelfIndex >= 0 ? native.Expression.Replace("$self", Object(native.SelfIndex)) : native.Expression;
        expression = ArgumentPlaceholder().Replace(expression, match => Object(int.Parse(match.Groups[1].Value) + offset));

        // Un paramètre peut aussi s'écrire par son nom RGL, sauf après un « . », « :: » ou « -> » (un membre C++ de même nom) et quand il est suivi d'une parenthèse (un appel).
        for (var i = offset; i < gear.Parameters.Count; ++i)
            expression = Regex.Replace(expression, $@"(?<![\w.:>$]){Regex.Escape(gear.Parameters[i].Name)}\b(?!\s*\()", _ => Object(i));

        if (gear.ReturnType == PrimitiveType.Void) return $"static_cast<void>({expression});";
        if (describedResult is not null) return $"rgl_c.result = reinterpret_cast<std::uint64_t>(new {describedResult}({expression}));";
        return $"rgl_c.result = {expression};";
    }
}
