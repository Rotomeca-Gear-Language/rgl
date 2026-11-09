using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Notes;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    private int _cppId;
    private readonly HashSet<string> _wrappedCppSources = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static bool _Trusted(GearDeclaration gear) => gear.Notes?.Annotations.OfType<OtherAnnotation>().Any(a => a.Name.Name is "trusted" or "trust") == true;

    private void _CheckTrustUse(GearSymbol gear, SourceSpan span)
    {
        if (gear.IsTrusted && !_nativePolicy.AllowsTrust(gear.Declaration.Source.Path))
            _Error(span, DiagnosticCodes.NativePolicy, $"« {gear.Name} » est @trusted : son utilisation est interdite par no-trust", "fonction de confiance non autorisée",
                "autorise son fichier .forge dans cpp-trust, ou retire no-trust");
    }

    private static bool _CppType(IrType type) => type switch
    {
        NullableType nullable => _CppType(nullable.Inner),
        FunctionType function => function.Parameters.All(_CppType) && (function.Return == PrimitiveType.Void || _CppType(function.Return)),
        _ => _IsNativeType(type, null),
    };

    private IrExpression? _BindCpp(CppExpression cpp)
    {
        if (!_nativePolicy.AllowsCpp(cpp.Span.Source.Path))
        {
            _Error(cpp.Span, DiagnosticCodes.NativePolicy, "cpp {} est interdit par no-cpp dans ce fichier", "C++ non autorisé");
            return null;
        }
        foreach (var include in Syntax.CppBlockReader.Includes(cpp.Code))
        {
            if (include.Path.EndsWith(".rgl.gen.cpp", StringComparison.OrdinalIgnoreCase) || include.Path.EndsWith(".rgl.o", StringComparison.OrdinalIgnoreCase)
                || new[] { ".dll", ".so", ".a", ".lib", ".dylib" }.Contains(Path.GetExtension(include.Path), StringComparer.OrdinalIgnoreCase))
            {
                _Error(include.Span, DiagnosticCodes.InvalidCppBlock, "cpp accepte du source C++, pas un binaire ou un wrapper généré", "inclusion interdite");
                return null;
            }
            if (Path.IsPathFullyQualified(cpp.Code.Source.Path) && _wrappedCppSources.Contains(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cpp.Code.Source.Path)!, include.Path))))
            {
                _Error(include.Span, DiagnosticCodes.InvalidCppBlock, "ce fichier C/C++ est déjà lié par un forge : appelle sa liaison RGL", "composant déjà lié");
                return null;
            }
        }
        if (!_RequireUnsafe(cpp.Span)) return null;
        if (_currentGear is null)
        {
            _Error(cpp.Span, DiagnosticCodes.InvalidCppBlock, "un bloc cpp s’écrit dans un gear unsafe ou @trusted", "pas de gear");
            return null;
        }
        var pure = _currentGear.IsPure && _currentGear.IsTrusted;
        if (!pure && !_AllowEffect(cpp.Span, DiagnosticCodes.ImpureCall, "exécuter un bloc C++ sans contrat de pureté @trusted")) return null;

        var result = cpp.ReturnType is Rgl.AST.Types.PrimitiveType { Keyword: Syntax.TokenKind.VoidKeyword } ? PrimitiveType.Void : _BindType(cpp.ReturnType);
        var parameters = new List<ParameterSymbol>();
        var arguments = new List<IrExpression>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var errors = _ErrorCount;
        foreach (var argument in cpp.Arguments)
        {
            if (!names.Add(argument.Name.Name)) _Error(argument.Name.Span, DiagnosticCodes.InvalidCppBlock, "paramètre C++ déjà déclaré", "nom en double");
            var type = _BindType(argument.Type);
            if (type is null) continue;
            type = _NativeBoundaryType(type);
            if (type == PrimitiveType.Void || !_CppType(type))
            {
                _Error(argument.Type.Span, DiagnosticCodes.InvalidCppBlock, $"le type « {type} » ne passe pas la frontière C++", "type natif incompatible");
                continue;
            }
            var value = _BindExpected(argument.Value, type);
            if (value is null) continue;
            value = _ConvertTo(value, type, "argument du bloc C++");
            if (value is null) continue;
            parameters.Add(new ParameterSymbol(argument.Name.Name, type, argument.Name.Span));
            arguments.Add(value);
        }
        if (result is null || _ErrorCount != errors || parameters.Count != cpp.Arguments.Count) return null;
        result = _NativeBoundaryType(result);
        if (result != PrimitiveType.Void && !_CppType(result))
        {
            _Error(cpp.ReturnType.Span, DiagnosticCodes.InvalidCppBlock, $"le type de retour « {result} » ne passe pas la frontière C++", "type natif incompatible");
            return null;
        }
        var name = "__cpp_" + ++_cppId;
        var symbol = new GearSymbol(name, GearKind.Gear, parameters, result, pure, cpp.Span, name)
        { InlineCpp = cpp.Code, DeclaredUnsafe = true, IsUnsafe = true };
        var bound = new IrGear(cpp.Span, symbol, new IrBlock(cpp.Span, []));
        _instantiatedNativeGears.Add(bound);
        _boundGears[symbol] = bound;
        _calls[symbol] = [];
        _calls[_currentGear].Add(symbol);
        return new IrCall(cpp.Span, symbol, arguments);
    }
}
