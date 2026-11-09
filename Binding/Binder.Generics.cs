using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Generics;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Train;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Les génériques (slice 4.4). Un gear ou un mold générique est un modèle : chaque combinaison d'arguments le fait
/// instancier une fois (monomorphisation), en liant sa déclaration avec les paramètres génériques remplacés par les
/// types et les valeurs reçus. Un générique jamais utilisé n'est pas lié. Décisions : les arguments d'un gear se
/// déduisent de ses arguments (les valeurs constantes en dernier, pour qu'un littéral s'adapte à ce que les autres ont
/// donné) ou s'écrivent <c>f&lt;int&gt;(x)</c> ; un paramètre de valeur s'écrit toujours ; les contraintes sont <c>number</c>,
/// <c>error</c> et un type mold anonyme, aucune ne porte sur un opérateur ; <c>T is X</c> et <c>T accept X</c> valent un
/// booléen connu, et un <c>if</c> dessus ne lie que la branche prise ; seuls les gears (pas les guards ni les emits) et les
/// molds sont génériques.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Les valeurs des paramètres génériques d'une instance : les types, et les valeurs constantes.</summary>
    private sealed class GenericEnvironment(IReadOnlyList<TypeParameter> parameters)
    {
        /// <summary>Les paramètres du générique, dans l'ordre.</summary>
        public IReadOnlyList<TypeParameter> Parameters { get; } = parameters;

        /// <summary>Le type de chaque paramètre de type connu.</summary>
        public Dictionary<string, IrType> Types { get; } = new(StringComparer.Ordinal);

        /// <summary>Les arguments numériques constants qui ont seuls donné son type à un paramètre : le type peut encore s'élargir.</summary>
        public Dictionary<string, List<IrExpression>> Constants { get; } = new(StringComparer.Ordinal);

        /// <summary>La valeur de chaque paramètre de valeur connu.</summary>
        public Dictionary<string, IrExpression> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>Vrai si <paramref name="name"/> est un paramètre de type du générique.</summary>
        public bool IsTypeParameter(string name) => Parameters.Any(p => p.ValueType is null && p.Name.Name == name);

        /// <summary>Les arguments, dans l'ordre des paramètres : un <see cref="IrType"/> ou la valeur connue.</summary>
        public IEnumerable<object> Arguments(Func<IrExpression, object?> fold) =>
            Parameters.Select(p => p.ValueType is null ? (object)Types[p.Name.Name] : fold(Values[p.Name.Name]) ?? 0);
    }

    private sealed record GenericGear(GearDeclaration Declaration, Namespace Namespace, string Key);

    private sealed record GenericMold(MoldDeclaration Declaration, Namespace Namespace, string Key);

    private sealed record PendingInstance(GenericGear Generic, GearSymbol Symbol, GenericEnvironment Environment);

    /// <summary>Les gears génériques, par nom unique.</summary>
    private readonly Dictionary<string, GenericGear> _genericGears = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GenericGear>> _genericGearOverloads = new(StringComparer.Ordinal);

    /// <summary>Les molds génériques, par nom unique.</summary>
    private readonly Dictionary<string, GenericMold> _genericMolds = new(StringComparer.Ordinal);

    /// <summary>Les instances déjà créées d'un gear générique ; <see langword="null"/> si sa signature contenait une erreur.</summary>
    private readonly Dictionary<string, GearSymbol?> _gearInstances = new(StringComparer.Ordinal);

    /// <summary>Les instances déjà créées d'un mold générique.</summary>
    private readonly Dictionary<string, MoldSymbol?> _moldInstances = new(StringComparer.Ordinal);

    /// <summary>Le mold générique et les types arguments de chaque instance de mold, pour déduire un argument d'un type de mold.</summary>
    private readonly Dictionary<MoldSymbol, (string Generic, IrType[] Arguments)> _moldInstanceArguments = [];

    /// <summary>Les instances de gears dont le corps reste à lier.</summary>
    private readonly Queue<PendingInstance> _pendingInstances = new();

    /// <summary>Les paramètres génériques de ce qu'on lie, ou <see langword="null"/> hors d'une instance.</summary>
    private GenericEnvironment? _generic;

    /// <summary>L'environnement où chaque valeur par défaut d'un champ de mold générique a été écrite.</summary>
    private readonly Dictionary<MoldFieldSymbol, GenericEnvironment?> _defaultGenerics = [];

    /// <summary>Profondeur des instanciations en cours, pour ne vérifier les molds qu'une fois la dernière finie.</summary>
    private int _instantiationDepth;

    /// <summary>Nombre d'instances créées, pour arrêter une instanciation sans fin (<c>f&lt;T&gt;</c> qui s'appelle avec <c>[T]</c>).</summary>
    private int _instanceCount;

    private const int MaxInstances = 512;

    #region Déclarations

    /// <summary>
    /// Les paramètres génériques d'une déclaration : un nom unique (E117), pas de valeur dans un mold (E117), une contrainte
    /// connue (E117).
    /// </summary>
    private bool _CheckTypeParameters(IReadOnlyList<TypeParameter> parameters, bool isMold, IEnumerable<string> otherNames)
    {
        var valid = true;
        var seen = new HashSet<string>(otherNames, StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!seen.Add(parameter.Name.Name))
            {
                _Error(parameter.Name.Span, DiagnosticCodes.InvalidGenericDeclaration, $"« {parameter.Name.Name} » est déjà utilisé comme paramètre ou comme paramètre générique", "en double",
                    "les paramètres génériques et les paramètres du gear ont des noms distincts");
                valid = false;
            }

            if (parameter.ValueType is not null && isMold)
            {
                _Error(parameter.Span, DiagnosticCodes.InvalidGenericDeclaration, "un mold n'accepte que des paramètres de type", "paramètre de valeur",
                    "un paramètre de valeur (int max) s'écrit sur un gear : gear borne<int max>(int v): int => …");
                valid = false;
            }

            if (parameter.Constraint is null or AstTypes.AnonymousMoldType or AstTypes.NamedType { Name.Name: "number" or "error", TypeArguments: null }) continue;

            _Error(parameter.Constraint.Span, DiagnosticCodes.InvalidGenericDeclaration, $"contrainte « {parameter.Constraint.Span.Text} » inconnue", "contrainte inconnue",
                "les contraintes sont number (un type numérique), error (un type d'erreur) et un type mold {TYPE champ} (au moins ces champs)",
                "une contrainte sur un opérateur n'existe pas : écris un gear pour chaque cas, ou teste le type avec T is / T accept");
            valid = false;
        }
        return valid;
    }

    /// <summary>Un gear générique : son nom est réservé, son corps sera lié à chaque instance.</summary>
    private void _DeclareGenericGear(GearDeclaration gear)
    {
        var key = _DeclKey(gear.Name.Name);
        var first = !_genericGearOverloads.TryGetValue(key, out var overloads) && !_gearOverloads.ContainsKey(key);
        if (first && !_ingots.ContainsKey(key) && !_ClaimName(gear.Name, "un gear", "gear")) return;

        if (gear.Keyword != TokenKind.GearKeyword)
        {
            _Unsupported(gear.Name.Span, $"un {gear.Keyword.ToString().Replace("Keyword", "").ToLowerInvariant()} générique");
            return;
        }
        if (!_CheckTypeParameters(gear.TypeParameters!, false, gear.Parameters.Select(p => p.Name.Name))) return;

        overloads ??= [];
        // Les types écrits sont comparés ici ; les paramètres génériques homonymes occupent la même position.
        if (overloads.Any(g => g.Declaration.TypeParameters!.Count == gear.TypeParameters!.Count
            && g.Declaration.Parameters.Count == gear.Parameters.Count
            && g.Declaration.Parameters.Zip(gear.Parameters).All(p => p.First.Type.Span.Text == p.Second.Type.Span.Text
                && p.First.IsParams == p.Second.IsParams)))
        {
            _Error(gear.Name.Span, DiagnosticCodes.DuplicateGear, $"le gear générique « {gear.Name.Name} » a déjà cette signature", "signature déjà déclarée");
            return;
        }
        var generic = new GenericGear(gear, _ns, key + "_overload" + overloads.Count);
        overloads.Add(generic);
        _genericGearOverloads[key] = overloads;
        _genericGears.TryAdd(key, generic);
    }

    /// <summary>Un mold générique : son nom est réservé, ses champs seront liés à chaque instance.</summary>
    private void _DeclareGenericMold(MoldDeclaration mold)
    {
        if (!_ClaimTypeName(mold.Name, mold.IsError ? "une error" : "un mold")) return;
        if (!_CheckTypeParameters(mold.TypeParameters!, true, [])) return;

        var key = _DeclKey(mold.Name.Name);
        _genericMolds[key] = new GenericMold(mold, _ns, key);
    }

    #endregion

    #region Contexte et instances

    /// <summary>Exécute <paramref name="action"/> dans les noms où un générique est écrit, avec ses paramètres remplacés par leurs valeurs.</summary>
    private T _InGeneric<T>(Namespace ns, GenericEnvironment? environment, Func<T> action)
    {
        var (current, scope, fileScope, generic, gear, parameters, lambda, expected) = (_ns, _scope, _fileScope, _generic, _currentGear, _parameters, _lambda, _expected);
        _EnterNamespace(ns);
        _generic = environment;
        (_currentGear, _parameters, _lambda, _expected) = (null, new(StringComparer.Ordinal), null, null);
        try { return action(); }
        finally
        {
            _ns = current;
            (_scope, _fileScope, _generic, _currentGear, _parameters, _lambda, _expected) = (scope, fileScope, generic, gear, parameters, lambda, expected);
        }
    }

    /// <summary>Le type d'un argument, pour nommer une instance : deux types différents ne donnent jamais la même clé.</summary>
    private static string _TypeKey(IrType type) => type switch
    {
        MoldType mold => mold.Symbol.QualifiedName ?? mold.ToString(),
        EnumType enumeration => enumeration.Symbol.QualifiedName,
        IngotType ingot => ingot.Symbol.Name,
        NullableType nullable => _TypeKey(nullable.Inner) + "?",
        ArrayType array => $"[{_TypeKey(array.Element)};{array.Kind};{array.Size};{array.Max};{array.Packed}]",
        DictionaryType dictionary => $"[{_TypeKey(dictionary.Key)}:{_TypeKey(dictionary.Value)}]",
        TupleType tuple => $"({string.Join(",", tuple.Elements.Select(_TypeKey))})",
        UnionType union => string.Join("|", union.Members.Select(_TypeKey)),
        ResultType result => $"{_TypeKey(result.Value)}!{_TypeKey(result.Error)}",
        PromiseType promise => $"Promise<{_TypeKey(promise.Value)}!{_TypeKey(promise.Error)}>",
        FunctionType function => $"{(function.IsPure ? "pure" : "")}({string.Join(",", function.Parameters.Select(_TypeKey))})=>{_TypeKey(function.Return)}",
        _ => type.ToString(),
    };

    /// <summary>La clé d'une instance : le nom du générique et ses arguments.</summary>
    private static string _InstanceKey(string key, GenericEnvironment environment) =>
        key + "<" + string.Join(",", environment.Arguments(v => ConstantFolder.Fold(v)).Select(a => a is IrType type ? _TypeKey(type) : Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture))) + ">";

    /// <summary>Le nom écrit d'une instance, pour les messages : <c>clamp&lt;int&gt;</c>.</summary>
    private static string _InstanceName(string name, GenericEnvironment environment) =>
        name + "<" + string.Join(", ", environment.Arguments(v => ConstantFolder.Fold(v)).Select(a => a is IrType type ? type.ToString() : Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture))) + ">";

    private bool _CheckInstanceLimit(SourceSpan span, string name)
    {
        if (++_instanceCount <= MaxInstances) return true;
        _Error(span, DiagnosticCodes.TypeArguments, $"« {name} » demande trop d'instances génériques (plus de {MaxInstances})", "instanciation sans fin",
            "un générique qui s'appelle avec un type toujours plus grand ne se termine pas : par exemple f<T> qui appelle f<[T]>");
        return false;
    }

    #endregion

    #region Arguments : explicites, déduits, contraintes

    /// <summary>
    /// Donne leurs valeurs aux paramètres d'un générique : ceux écrits, puis ceux déduits des arguments <paramref name="arguments"/>
    /// (null quand il n'y en a pas : un mold). Vérifie ensuite leurs contraintes. <see langword="null"/> après une erreur signalée.
    /// </summary>
    private GenericEnvironment? _BindGenericArguments(string name, SourceSpan span, Namespace ns, IReadOnlyList<TypeParameter> parameters, IReadOnlyList<ITypeNode>? written,
        IReadOnlyList<Parameter>? declared, IReadOnlyList<IrExpression>? arguments, Action<GenericEnvironment, List<string>>? inferMore = null)
    {
        var environment = new GenericEnvironment(parameters);
        written ??= [];
        if (written.Count > parameters.Count)
        {
            _Error(span, DiagnosticCodes.TypeArguments, $"« {name} » a {parameters.Count} paramètre(s) générique(s), pas {written.Count}", $"{written.Count} argument(s)",
                $"paramètres : {string.Join(", ", parameters.Select(p => p.Name.Name))}");
            return null;
        }

        for (var i = 0; i < written.Count; ++i)
        {
            var parameter = parameters[i];
            if (parameter.ValueType is null)
            {
                if (written[i] is ValueTypeArgument value)
                {
                    _Error(value.Span, DiagnosticCodes.InvalidValueArgument, $"« {parameter.Name.Name} » est un paramètre de type : une valeur n'y va pas", "valeur",
                        $"écris un type, par exemple : {name}<int>");
                    return null;
                }
                if (_BindType(written[i]) is not { } type) return null;
                environment.Types[parameter.Name.Name] = type;
            }
            else if (_BindValueArgument(written[i], parameter, ns) is { } bound) environment.Values[parameter.Name.Name] = bound;
            else return null;
        }

        // Ce qui n'est pas écrit se déduit des arguments : d'abord ceux qui ne sont pas constants.
        if (declared is not null && arguments is not null)
        {
            var variadic = declared.Count > 0 && declared[^1].IsParams && declared[^1].Type is AstTypes.ArrayType;
            var order = Enumerable.Range(0, variadic ? arguments.Count : Math.Min(declared.Count, arguments.Count))
                .OrderBy(i => ConstantFolder.Fold(arguments[i]) is null ? 0 : 1).ToArray();
            var conflicts = new List<string>();
            foreach (var i in order)
            {
                // params [T] : un seul argument qui est un tableau donne T par son élément ; sinon chaque argument en trop est un T.
                var pattern = i < declared.Count ? declared[i].Type : null;
                if (variadic && i >= declared.Count - 1)
                {
                    var whole = arguments.Count == declared.Count && arguments[i].Type is ArrayType;
                    pattern = whole ? declared[^1].Type : ((AstTypes.ArrayType)declared[^1].Type).Element;
                }
                if (pattern is not null) _Infer(pattern, arguments[i].Type, arguments[i], environment, conflicts, topLevel: true);
            }
            if (conflicts.Count > 0)
            {
                _Error(span, DiagnosticCodes.TypeArguments, $"les arguments de « {name} » ne s'accordent pas : {conflicts[0]}", "types différents",
                    $"écris l'argument générique pour choisir : {name}<{parameters[0].Name.Name}>(…)");
                return null;
            }
        }

        if (inferMore is not null)
        {
            var conflicts = new List<string>();
            inferMore(environment, conflicts);
            if (conflicts.Count > 0)
            {
                _Error(span, DiagnosticCodes.TypeArguments, $"les champs de « {name} » ne s'accordent pas : {conflicts[0]}", "types différents",
                    $"écris l'argument générique pour choisir : new {name}<{parameters[0].Name.Name}> {{…}}");
                return null;
            }
        }

        foreach (var parameter in parameters)
        {
            var known = parameter.ValueType is null ? environment.Types.TryGetValue(parameter.Name.Name, out var type) && type is not NullType : environment.Values.ContainsKey(parameter.Name.Name);
            if (known) continue;

            _Error(span, DiagnosticCodes.TypeArguments,
                parameter.ValueType is null ? $"le paramètre générique « {parameter.Name.Name} » de « {name} » ne se déduit pas des arguments" : $"la valeur du paramètre « {parameter.Name.Name} » de « {name} » doit être écrite",
                "non déduit",
                parameter.ValueType is null ? $"écris-le : {name}<int>(…) ; null ne donne pas de type" : $"une valeur générique s'écrit entre chevrons : {name}<10>(…)");
            return null;
        }

        return _CheckConstraints(name, span, ns, environment) ? environment : null;
    }

    /// <summary>La valeur d'un paramètre de valeur : écrite entre chevrons, connue à la compilation (E116) et du type du paramètre.</summary>
    private IrExpression? _BindValueArgument(ITypeNode written, TypeParameter parameter, Namespace ns)
    {
        // Un littéral, ou le nom d'une constante, qui se lit comme un nom de type.
        IExpression? source = written switch
        {
            ValueTypeArgument value => value.Value,
            AstTypes.NamedType { TypeArguments: null } named when _scope[named.Name.Name].HasValue || _generic?.Values.ContainsKey(named.Name.Name) == true || _parameters.ContainsKey(named.Name.Name) => new NameExpression(named.Name),
            _ => null,
        };
        if (source is null)
        {
            _Error(written.Span, DiagnosticCodes.InvalidValueArgument, $"« {parameter.Name.Name} » est un paramètre de valeur : un type n'y va pas", "type",
                $"écris une valeur connue à la compilation, par exemple : 10");
            return null;
        }

        var type = _InGeneric(ns, null, () => _BindType(parameter.ValueType!));
        var bound = _BindExpression(source);
        if (type is null || bound is null) return null;
        if (_ConvertTo(bound, type, $"« {parameter.Name.Name} » est un « {type} »") is not { } converted) return null;
        if (ConstantFolder.Fold(converted) is null)
        {
            _Error(source.Span, DiagnosticCodes.InvalidValueArgument, $"la valeur de « {parameter.Name.Name} » n'est pas connue à la compilation", "calculée à l'exécution",
                "un paramètre de valeur est une constante : un littéral, une constante du fichier, ou un calcul sur elles");
            return null;
        }
        return converted;
    }

    /// <summary>Le premier de int, long, float, double qui contient toutes ces constantes ; <see langword="null"/> si aucun.</summary>
    private PrimitiveType? _NumberTypeFor(IReadOnlyList<IrExpression> constants) =>
        new[] { PrimitiveType.Int, new PrimitiveType(PrimitiveKind.Long), new PrimitiveType(PrimitiveKind.Float), PrimitiveType.Double }
            .FirstOrDefault(candidate => constants.All(c => _CanCoerce(c, candidate)));

    /// <summary>
    /// Cherche dans <paramref name="actual"/>, le type d'un argument, les paramètres de type que <paramref name="pattern"/>, le type
    /// écrit du paramètre, désigne. Un paramètre déjà connu garde son type ; un autre type qui n'y entre pas est un conflit.
    /// </summary>
    private void _Infer(ITypeNode pattern, IrType actual, IrExpression? argument, GenericEnvironment environment, List<string> conflicts, bool topLevel = false)
    {
        switch (pattern)
        {
            case AstTypes.NamedType { TypeArguments: null, Name.Name: var name } when environment.IsTypeParameter(name):
                var constant = topLevel && argument is not null and not IrVariableReference && actual is PrimitiveType { IsNumeric: true, Bounds: null } && ConstantFolder.Fold(argument) is not null;
                if (!environment.Types.TryGetValue(name, out var bound))
                {
                    // Un nombre connu à la compilation prend le type le plus courant qui le contient : int, sinon long ; float, sinon double.
                    environment.Types[name] = constant ? _NumberTypeFor([argument!]) ?? actual : actual;
                    if (constant) environment.Constants[name] = [argument!];
                }
                else if (bound == actual || (topLevel && argument is not null && _CanCoerce(argument, bound)))
                {
                    if (constant && environment.Constants.TryGetValue(name, out var same)) same.Add(argument!);
                }
                else if (constant && environment.Constants.TryGetValue(name, out var known) && _NumberTypeFor([.. known, argument!]) is { } wider)
                {
                    // Tous les arguments qui ont donné son type sont des constantes : le type s'élargit pour les contenir tous.
                    known.Add(argument!);
                    environment.Types[name] = wider;
                }
                else conflicts.Add($"« {name} » est déduit « {bound} », puis « {actual} »");
                break;

            case AstTypes.NamedType { TypeArguments: { } arguments } when actual is MoldType { Symbol: var mold } && _moldInstanceArguments.TryGetValue(mold, out var origin):
                for (var i = 0; i < Math.Min(arguments.Count, origin.Arguments.Length); ++i) _Infer(arguments[i], origin.Arguments[i], null, environment, conflicts);
                break;

            case AstTypes.ArrayType array when actual is ArrayType actualArray:
                _Infer(array.Element, actualArray.Element, null, environment, conflicts);
                break;

            case AstTypes.DictionaryType dictionary when actual is DictionaryType actualDictionary:
                _Infer(dictionary.Key, actualDictionary.Key, null, environment, conflicts);
                _Infer(dictionary.Value, actualDictionary.Value, null, environment, conflicts);
                break;

            case AstTypes.NullableType nullable:
                _Infer(nullable.Inner, actual is NullableType actualNullable ? actualNullable.Inner : actual, null, environment, conflicts);
                break;

            case AstTypes.TupleType tuple when actual is TupleType actualTuple:
                for (var i = 0; i < Math.Min(tuple.Elements.Count, actualTuple.Elements.Count); ++i) _Infer(tuple.Elements[i].Type, actualTuple.Elements[i], null, environment, conflicts);
                break;

            case AstTypes.FunctionType function when actual is FunctionType actualFunction:
                for (var i = 0; i < Math.Min(function.Parameters.Count, actualFunction.Parameters.Count); ++i) _Infer(function.Parameters[i], actualFunction.Parameters[i], null, environment, conflicts);
                _Infer(function.Return, actualFunction.Return, null, environment, conflicts);
                break;

            case AstTypes.NamedType { Name.Name: "Promise", TypeArguments: { Count: 1 } promiseArguments } when actual is PromiseType promise:
                // Promise<T> et Promise<T ! E> : la valeur de la promesse, et son erreur quand elle est écrite.
                if (promiseArguments[0] is AstTypes.ResultType written)
                {
                    if (promise.Value != PrimitiveType.Void) _Infer(written.Value, promise.Value, null, environment, conflicts);
                    _Infer(written.Error, promise.Error, null, environment, conflicts);
                }
                else if (promise.Value != PrimitiveType.Void) _Infer(promiseArguments[0], promise.Value, null, environment, conflicts);
                break;

            case AstTypes.ResultType result when actual is ResultType actualResult:
                _Infer(result.Value, actualResult.Value, null, environment, conflicts);
                _Infer(result.Error, actualResult.Error, null, environment, conflicts);
                break;
        }
    }

    /// <summary>Les contraintes des paramètres de type (E114) : <c>number</c>, <c>error</c>, ou au moins les champs d'un type mold.</summary>
    private bool _CheckConstraints(string name, SourceSpan span, Namespace ns, GenericEnvironment environment)
    {
        foreach (var parameter in environment.Parameters.Where(p => p.Constraint is not null))
        {
            var type = environment.Types[parameter.Name.Name];
            string? missing = null;
            switch (parameter.Constraint)
            {
                case AstTypes.NamedType { Name.Name: "number" }:
                    if (type is not PrimitiveType { IsNumeric: true }) missing = "un type numérique (int, long, float, double…)";
                    break;

                case AstTypes.NamedType { Name.Name: "error" }:
                    if (!_IsError(type)) missing = "un type d'erreur (un mold avec string message et string where)";
                    break;

                case AstTypes.AnonymousMoldType anonymous:
                    if (_InGeneric(ns, environment, () => _BindType(anonymous)) is not MoldType required) return false;
                    if (!_HasFields(type, required.Symbol)) missing = $"un type avec au moins {required}";
                    break;
            }
            if (missing is null) continue;

            _Error(span, DiagnosticCodes.ConstraintNotMet, $"« {type} » ne convient pas à « {parameter.Name.Name} » de « {name} »", $"« {type} »",
                $"« {parameter.Name.Name} » doit être {missing}", $"contrainte écrite : {parameter.Name.Name}: {parameter.Constraint!.Span.Text}");
            return false;
        }
        return true;
    }

    /// <summary>Vrai si <paramref name="type"/> est un mold qui a, avec le même type, tous les champs de <paramref name="required"/>.</summary>
    private static bool _HasFields(IrType type, MoldSymbol required) =>
        type is MoldType { Symbol: var mold } && required.Fields.All(f => mold[f.Name] is { } own && own.Type == f.Type);

    #endregion

    #region Gears génériques

    /// <summary>
    /// L'instance d'un gear générique pour cet appel : ses arguments génériques écrits ou déduits de <paramref name="arguments"/>,
    /// créée au premier appel (la signature tout de suite, le corps plus tard) et réutilisée ensuite.
    /// </summary>
    private GearSymbol? _InstantiateGear(GenericGear generic, IReadOnlyList<ITypeNode>? written, IReadOnlyList<IrExpression> arguments, SourceSpan span)
    {
        var declaration = generic.Declaration;
        var name = declaration.Name.Name;
        var environment = _BindGenericArguments(name, span, generic.Namespace, declaration.TypeParameters!, written, declaration.Parameters, arguments);
        if (environment is null) return null;

        var key = _InstanceKey(generic.Key, environment);
        if (_gearInstances.TryGetValue(key, out var existing)) return existing;
        if (!_CheckInstanceLimit(span, name)) return null;

        var displayName = _InstanceName(name, environment);
        var cppName = generic.Key.Replace(".", "_d_") + "_g" + _gearInstances.Count;
        var symbol = _InGeneric(generic.Namespace, environment, () => _BindGearSignature(declaration, generic.Key, displayName, cppName));
        _gearInstances[key] = symbol;
        if (symbol is not null) _pendingInstances.Enqueue(new PendingInstance(generic, symbol, environment));
        return symbol;
    }

    /// <summary>Lie le corps de chaque instance de gear créée, y compris celles que ces corps créent à leur tour.</summary>
    private void _BindPendingInstances(RArray<IrGear> gears)
    {
        var fileNamespace = _ns;
        while (_pendingInstances.TryDequeue(out var pending))
        {
            _EnterNamespace(pending.Generic.Namespace);
            _scope = new Scope(pending.Generic.Namespace.Scope);
            _generic = pending.Environment;
            if (_BindGearBody(pending.Generic.Declaration, pending.Symbol) is { } bound) gears.Push(bound);
            _generic = null;
        }
        foreach (var native in _instantiatedNativeGears) gears.Push(native);
        _instantiatedNativeGears.Clear();
        _EnterNamespace(fileNamespace);
    }

    #endregion

    #region Molds génériques

    /// <summary>Le type <c>Pair&lt;int, string&gt;</c> : l'instance du mold générique pour ces arguments (E041 s'il n'existe pas).</summary>
    private IrType? _BindGenericType(AstTypes.NamedType named)
    {
        if (!_TryFind(_genericMolds, named.Name.Name, out var generic))
        {
            _Error(named.Span, DiagnosticCodes.UnknownName, $"le type « {named.Name.Name} » n'est pas générique ou n'existe pas", "pas de paramètres génériques",
                "seul un mold déclaré avec des paramètres (mold Pair<T, U> { … }) s'écrit avec des arguments");
            return null;
        }

        var environment = _BindGenericArguments(named.Name.Name, named.Span, generic.Namespace, generic.Declaration.TypeParameters!, named.TypeArguments, null, null);
        return environment is null ? null : _InstantiateMold(generic, environment, named.Span);
    }

    /// <summary>
    /// Le type d'un <c>new Pair {…}</c> écrit sans arguments : ceux du type attendu s'il est une instance du même mold, sinon ceux
    /// déduits du type des champs donnés (les paramètres de valeur doivent toujours être écrits).
    /// </summary>
    private IrType? _InferMoldType(GenericMold generic, AstTypes.NamedType named, IReadOnlyList<(string Name, IrExpression Value)> entries)
    {
        var fields = generic.Declaration.Members.OfType<MoldField>().ToArray();
        var environment = _BindGenericArguments(named.Name.Name, named.Span, generic.Namespace, generic.Declaration.TypeParameters!, null, null, null, (env, conflicts) =>
        {
            foreach (var (fieldName, value) in entries)
            {
                if (fields.FirstOrDefault(f => f.Name.Name == fieldName) is { } field)
                    _Infer(field.Type, value.Type, value, env, conflicts, topLevel: true);
            }
        });
        return environment is null ? null : _InstantiateMold(generic, environment, named.Span);
    }

    private IrType? _InstantiateMold(GenericMold generic, GenericEnvironment environment, SourceSpan span)
    {
        var declaration = generic.Declaration;
        var key = _InstanceKey(generic.Key, environment);
        if (_moldInstances.TryGetValue(key, out var existing)) return existing is null ? null : new MoldType(existing);
        if (!_CheckInstanceLimit(span, declaration.Name.Name)) return null;

        var arguments = environment.Parameters.Select(p => environment.Types[p.Name.Name]).ToArray();
        var name = _InstanceName(declaration.Name.Name, environment);
        var symbol = new MoldSymbol(name, declaration.Name.Span, generic.Key + "_g" + _moldInstances.Count) { IsError = declaration.IsError };
        _moldInstances[key] = symbol;
        _moldInstanceArguments[symbol] = (generic.Key, arguments);
        _molds[generic.Key + "<" + key] = symbol;
        _moldDeclarations[symbol] = declaration;
        _allMolds.Push(symbol);

        ++_instantiationDepth;
        _resolvingMolds.Add(symbol);
        symbol.Fields = _InGeneric(generic.Namespace, environment, () =>
            _BindMoldMembers(declaration.Members, name, declaration.IsError ? _ErrorFields(declaration.Name.Span) : null));
        _resolvingMolds.Remove(symbol);
        _resolvedMolds.Add(symbol);
        _InstantiateNativeDescriptor(generic, environment, symbol);
        --_instantiationDepth;

        if (_instantiationDepth == 0)
        {
            _CheckMoldContainment();
            _CheckMoldDefaults();
        }
        return new MoldType(symbol);
    }

    #endregion

    #region is et accept

    /// <summary>
    /// <c>T is X</c> et <c>T accept X</c> : un booléen connu à la compilation. <c>is</c> compare les types, <c>accept</c> demande
    /// au moins les champs de X.
    /// </summary>
    private IrExpression? _BindTypeTest(TypeTestExpression test)
    {
        var subject = _BindType(test.Subject);
        var pattern = _BindType(test.Pattern);
        if (subject is null || pattern is null) return null;

        var result = test.IsStructural
            ? subject == pattern || (pattern is MoldType { Symbol: var required } && _HasFields(subject, required))
            : subject == pattern;
        return new IrBoolConstant(test.Span, result);
    }

    /// <summary>
    /// Ce que vaut une condition faite de tests de type seulement (<c>T is int</c>, <c>not</c>, <c>and</c>, <c>or</c>) :
    /// elle est connue à la compilation, et le <c>if</c> ne lie que la branche prise. <see langword="null"/> pour toute autre condition.
    /// </summary>
    private bool? _StaticCondition(IExpression condition)
    {
        switch (condition)
        {
            case ParenthesizedExpression parenthesized:
                return _StaticCondition(parenthesized.Inner);

            case TypeTestExpression test:
                return _BindTypeTest(test) is IrBoolConstant constant ? constant.Value : null;

            case UnaryExpression { Operator: TokenKind.NotKeyword } not when _IsStaticCondition(not.Operand):
                var inner = _StaticCondition(not.Operand);
                return inner is null ? null : !inner.Value;

            case BinaryExpression { Operator: TokenKind.AndKeyword or TokenKind.OrKeyword } binary when _IsStaticCondition(binary.Left) && _IsStaticCondition(binary.Right):
                var (left, right) = (_StaticCondition(binary.Left), _StaticCondition(binary.Right));
                if (left is null || right is null) return null;
                return binary.Operator == TokenKind.AndKeyword ? left.Value && right.Value : left.Value || right.Value;

            default:
                return null;
        }
    }

    /// <summary>Vrai si la condition n'est faite que de tests de type : on peut la lire sans rien lier.</summary>
    private static bool _IsStaticCondition(IExpression condition) => condition switch
    {
        TypeTestExpression => true,
        ParenthesizedExpression parenthesized => _IsStaticCondition(parenthesized.Inner),
        UnaryExpression { Operator: TokenKind.NotKeyword } not => _IsStaticCondition(not.Operand),
        BinaryExpression { Operator: TokenKind.AndKeyword or TokenKind.OrKeyword } binary => _IsStaticCondition(binary.Left) && _IsStaticCondition(binary.Right),
        _ => false,
    };

    #endregion
}
