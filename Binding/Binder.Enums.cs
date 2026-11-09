using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rotomeca.Core.Collections;

namespace Rgl.Binding;

/// <summary>
/// Les enums : déclaration (type sous-jacent écrit, avec une valeur par variante, ou choisi par le
/// compilateur, sans valeur), <c>Direction.North</c>, et <c>North</c> seul dans un <c>case</c> ou un
/// <c>goto</c> d'un switch sur l'enum.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Les enums déclarés, par nom.</summary>
    private readonly Dictionary<string, EnumSymbol> _enums = new(StringComparer.Ordinal);

    /// <summary>La déclaration de chaque enum.</summary>
    private readonly Dictionary<EnumSymbol, EnumDeclaration> _enumDeclarations = [];

    /// <summary>Les enums dont les valeurs sont déjà liées.</summary>
    private readonly HashSet<EnumSymbol> _enumValuesBound = [];

    /// <summary>L'enum d'un switch dont on lie les cas : <c>North</c> seul y désigne sa variante.</summary>
    private EnumSymbol? _expectedEnum;

    /// <summary>
    /// Les enums, avant les molds et les gears qui les utilisent : nom unique parmi les types (E075),
    /// type sous-jacent primitif (E083), variantes uniques (E083). Les valeurs sont liées plus tard,
    /// quand les constantes du fichier sont connues.
    /// </summary>
    private void _DeclareEnums(IEnumerable<EnumDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            var name = declaration.Name.Name;
            if (!_ClaimTypeName(declaration.Name, "un enum")) continue;

            if (declaration.Variants.Count == 0)
            {
                _Error(declaration.Name.Span, DiagnosticCodes.InvalidEnum, $"l'enum « {name} » n'a aucune variante", "vide",
                    "un enum a au moins une variante, par exemple : enum Direction { North, South }");
                continue;
            }

            PrimitiveType underlying;
            if (declaration.Underlying is { } written)
            {
                if (_BindType(written) is not PrimitiveType { Kind: not (PrimitiveKind.Void or PrimitiveKind.Bytes or PrimitiveKind.MString), Bounds: null } primitive)
                {
                    _Error(written.Span, DiagnosticCodes.InvalidEnum, "le type d'un enum est un primitif : nombre, bool, string ou symbol", "pas un primitif");
                    continue;
                }
                underlying = primitive;
            }
            else underlying = _SmallestUnderlying(declaration.Variants.Count);

            var key = _DeclKey(name);
            var symbol = new EnumSymbol(name, underlying, declaration.Name.Span, key) { HasValues = declaration.Underlying is not null };
            var variants = new RArray<EnumVariantSymbol>();
            foreach (var variant in declaration.Variants)
            {
                if (variants.AsReadOnly().Any(v => v.Name == variant.Name.Name))
                {
                    _Error(variant.Name.Span, DiagnosticCodes.InvalidEnum, $"la variante « {variant.Name.Name} » est déjà déclarée dans « {name} »", "en double");
                    continue;
                }
                var index = variants.AsReadOnly().Count;
                variants.Push(new EnumVariantSymbol(variant.Name.Name, index, variant.Name.Span) { Value = symbol.HasValues ? null : (long)index });
            }
            symbol.Variants = [.. variants.AsReadOnly()];

            _enums[key] = symbol;
            _enumDeclarations[symbol] = declaration;
        }
    }

    /// <summary>Le plus petit type qui numérote les variantes : <c>bool</c> pour deux, puis <c>ubyte</c>, <c>ushort</c>, <c>uint</c>.</summary>
    private static PrimitiveType _SmallestUnderlying(int count) => count switch
    {
        <= 2 => PrimitiveType.Bool,
        <= 256 => new PrimitiveType(PrimitiveKind.UByte),
        <= 65_536 => new PrimitiveType(PrimitiveKind.UShort),
        _ => PrimitiveType.UInt,
    };

    /// <summary>
    /// Les valeurs des variantes, une fois les constantes du fichier connues : toutes si le type est
    /// écrit, aucune sinon (E084) ; chacune du type sous-jacent (E003), connue à la compilation et
    /// différente des autres (E084).
    /// </summary>
    private void _BindEnumValues()
    {
        foreach (var (symbol, declaration) in _enumDeclarations.ToArray())
        {
            if (!_enumValuesBound.Add(symbol)) continue;
            var seen = new List<(object Value, string Name)>();
            foreach (var variant in declaration.Variants)
            {
                if (symbol[variant.Name.Name] is not { } target) continue;

                if (variant.Value is null)
                {
                    if (symbol.HasValues)
                        _Error(variant.Name.Span, DiagnosticCodes.InvalidEnumValue, $"« {variant.Name.Name} » n'a pas de valeur, alors que « {symbol.Name} » a un type écrit", "valeur manquante",
                            $"avec un type écrit, chaque variante a sa valeur, par exemple : {variant.Name.Name}: …",
                            "sans type écrit, aucune n'en a : le compilateur les numérote");
                    continue;
                }

                if (!symbol.HasValues)
                {
                    _Error(variant.Value.Span, DiagnosticCodes.InvalidEnumValue, $"« {symbol.Name} » n'a pas de type écrit : ses variantes n'ont pas de valeur", "valeur en trop",
                        $"pour donner des valeurs, écris le type : enum {symbol.Name}: int {{ … }}");
                    continue;
                }

                var value = _BindExpression(variant.Value);
                var converted = value is null ? null : _ConvertTo(value, symbol.Underlying, $"les variantes de « {symbol.Name} » sont des « {symbol.Underlying} »");
                if (converted is null) continue;

                if (ConstantFolder.Fold(converted) is not { } constant)
                {
                    _Error(variant.Value.Span, DiagnosticCodes.InvalidEnumValue, $"la valeur de « {variant.Name.Name} » n'est pas connue à la compilation", "calculée à l'exécution",
                        "une valeur de variante est une constante : un littéral ou une constante du fichier");
                    continue;
                }

                if (seen.FirstOrDefault(s => s.Value.Equals(constant)) is { Name: { } other })
                {
                    _Error(variant.Value.Span, DiagnosticCodes.InvalidEnumValue, $"« {variant.Name.Name} » a la même valeur que « {other} »", "valeur en double",
                        "chaque variante a sa propre valeur : un switch doit pouvoir les distinguer");
                    continue;
                }

                seen.Add((constant, variant.Name.Name));
                target.Value = constant;
            }
        }
    }

    /// <summary><c>Direction.North</c> : la variante, si <c>Direction</c> est un enum et pas une variable (E074 si elle n'existe pas).</summary>
    private IrExpression? _BindEnumAccess(MemberAccessExpression access, EnumSymbol symbol)
    {
        if (symbol[access.Member.Name] is { } variant) return new IrEnumValue(access.Span, new EnumType(symbol), variant.Index);

        _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"« {symbol.Name} » n'a pas de variante « {access.Member.Name} »", "variante inconnue",
            $"variantes : {string.Join(", ", symbol.Variants.Select(v => v.Name))}");
        return null;
    }

    /// <summary>Le nom d'un enum, s'il ne désigne pas une variable ou un paramètre visible.</summary>
    private EnumSymbol? _EnumNamed(IExpression expression) =>
        expression is NameExpression { Name.Name: var name } && !_scope[name].HasValue && !_parameters.ContainsKey(name) && _TryFind(_enums, name, out var symbol)
            ? symbol
            : _QualifiedName(expression) is { } qualified && _TryFind(_enums, qualified, out var imported) ? imported : null;

    /// <summary>Une valeur de <c>case</c> ou de <c>goto</c> : dans un switch sur un enum, <c>North</c> seul désigne sa variante.</summary>
    private IrExpression? _BindCaseValue(IExpression value, IrType subject)
    {
        var expected = _expectedEnum;
        _expectedEnum = subject is EnumType enumType ? enumType.Symbol : null;
        var bound = _BindExpression(value);
        _expectedEnum = expected;
        return bound;
    }

    /// <summary>
    /// Un switch sur un enum, sans <c>default</c>, qui ne couvre pas toutes ses variantes : W012
    /// (un avertissement, pas une erreur).
    /// </summary>
    /// <returns>Vrai si ses cas couvrent toutes les variantes : un cas est alors toujours choisi.</returns>
    private bool _CheckEnumCoverage(AST.Statements.SwitchStatement switchStatement, IrType subject, IEnumerable<object> covered, bool hasDefault)
    {
        if (subject is not EnumType { Symbol: var symbol }) return false;

        var seen = covered.OfType<ConstantFolder.EnumValue>().Select(v => v.Index).ToHashSet();
        var missing = symbol.Variants.Where(v => !seen.Contains(v.Index)).Select(v => v.Name).ToArray();
        if (missing.Length == 0) return true;
        if (hasDefault) return false;

        _diagnostics.Warning(switchStatement.Subject.Span, DiagnosticCodes.NonExhaustiveEnumSwitch,
            $"ce switch ne couvre pas : {string.Join(", ", missing)}", "variantes non couvertes",
            ["ajoute un cas pour chacune, ou un « default: »"]);
        return false;
    }
}
