using Rgl.AST;
using Rgl.AST.Notes;
using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Statements;
using Rgl.AST.Train;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;
using Rotomeca.Core.Collections;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    /// <summary>Les gears du fichier, par nom ; un gear dont la signature contient une erreur y vaut <see langword="null"/>.</summary>
    private readonly Dictionary<string, GearSymbol?> _gears = new(StringComparer.Ordinal);

    /// <summary>Les signatures ordinaires qui partagent un nom, dans leur ordre de déclaration.</summary>
    private readonly Dictionary<string, List<GearSymbol>> _gearOverloads = new(StringComparer.Ordinal);

    /// <summary>Le gear dont on lie le corps ; <see langword="null"/> dans le train et au niveau du fichier.</summary>
    private GearSymbol? _currentGear;

    /// <summary>
    /// Vrai pendant la liaison de la première étape d'un parcours qui modifie sur place une variable
    /// <c>mut</c> : l'élément parcouru peut y être passé à un paramètre <c>mut</c>.
    /// </summary>
    private bool _mutableElement;

    /// <summary>Les paramètres visibles : ceux du gear courant, ou celui du train.</summary>
    private Dictionary<string, ParameterSymbol> _parameters = new(StringComparer.Ordinal);

    /// <summary>Les appels de chaque gear, pour trouver les gears récursifs.</summary>
    private readonly Dictionary<GearSymbol, HashSet<GearSymbol>> _calls = [];

    /// <summary>
    /// La signature d'un gear, avant tout corps : nom partagé par des signatures distinctes, types des paramètres et du
    /// retour. Elle est enregistrée même en cas d'erreur, pour ne pas signaler ensuite chaque appel.
    /// </summary>
    private void _DeclareGear(GearDeclaration gear)
    {
        if (gear.TypeParameters is not null)
        {
            _DeclareGenericGear(gear);
            return;
        }

        var key = _DeclKey(gear.Name.Name);
        var first = !_gearOverloads.TryGetValue(key, out var overloads);
        if (first && !_genericGearOverloads.ContainsKey(key)
            && !_ingots.ContainsKey(key) && !_ClaimName(gear.Name, "un gear", "gear")) return;

        overloads ??= [];
        var cppName = overloads.Count == 0 && !_genericGearOverloads.ContainsKey(key) ? null : key.Replace(".", "_d_") + "_o" + overloads.Count;
        if (_BindGearSignature(gear, key, gear.Name.Name, cppName) is not { } symbol) return;
        if (overloads.Any(existing => _SameCallSignature(existing, symbol)))
        {
            _Error(gear.Name.Span, DiagnosticCodes.DuplicateGear, $"le gear « {gear.Name.Name} » a déjà cette signature", "signature déjà déclarée",
                $"signature : {symbol}", "le type de retour, pure, unsafe, trusted, gear et guard ne distinguent pas une surcharge");
            return;
        }
        overloads.Add(symbol);
        _gearOverloads[key] = overloads;
        _gears.TryAdd(key, symbol);
    }

    private static bool _SameCallSignature(GearSymbol left, GearSymbol right) =>
        left.Parameters.Count == right.Parameters.Count
        && left.Parameters.Zip(right.Parameters).All(p => p.First.Type == p.Second.Type
            && p.First.IsParams == p.Second.IsParams);

    /// <summary>
    /// Les types des paramètres et du retour d'un gear. <see langword="null"/> si l'un contient une erreur.
    /// Pour l'instance d'un générique, les paramètres génériques sont déjà remplacés (<see cref="_generic"/>).
    /// </summary>
    /// <param name="gear">La déclaration.</param>
    /// <param name="key">Le nom unique du gear (celui du générique pour une instance).</param>
    /// <param name="name">Le nom du symbole, pour les messages.</param>
    /// <param name="cppName">Le nom C++ d'une instance ; <see langword="null"/> pour le nom du gear.</param>
    private GearSymbol? _BindGearSignature(GearDeclaration gear, string key, string name, string? cppName, bool isTrain = false)
    {
        var previous = _unsafeSignature;
        _unsafeSignature = gear.Keyword == TokenKind.UnsafeKeyword || gear.IsNative;
        try { return _BindGearSignatureCore(gear, key, name, cppName, isTrain); }
        finally { _unsafeSignature = previous; }
    }

    private GearSymbol? _BindGearSignatureCore(GearDeclaration gear, string key, string name, string? cppName, bool isTrain = false)
    {
        RArray<ParameterSymbol> parameters = new();
        var failed = false;
        var earlierDefault = false;
        foreach (var parameter in gear.Parameters)
        {
            if (gear.IsPure && parameter.IsMutable)
            {
                _Error(parameter.Span, DiagnosticCodes.PureWithMutParameter, $"le gear pure « {gear.Name.Name} » ne peut pas avoir de paramètre mut", "paramètre mut", "modifier l'argument de l'appelant est un effet : retire pure ou mut");
                failed = true;
                continue;
            }

            if (_BindType(parameter.Type) is { } type && (!parameter.IsMutable || _CheckMutType(parameter.Span, type, $"le paramètre « {parameter.Name.Name} »")))
            {
                var defaultKind = parameter.IsParams ? _CheckParams(parameter, type, parameter == gear.Parameters[^1]) : _CheckDefault(parameter, type, earlierDefault);
                if (defaultKind is not { } kindChecked) failed = true;
                else
                {
                    var checkedKind = kindChecked;
                    earlierDefault |= checkedKind != ParameterDefault.None;
                    parameters.Push(new ParameterSymbol(parameter.Name.Name, gear.IsNative ? _NativeBoundaryType(type) : type, parameter.Name.Span, parameter.IsMutable)
                    {
                        Default = checkedKind,
                        DefaultSyntax = checkedKind == ParameterDefault.Expression ? parameter.Default : null,
                    });
                }
            }
            else failed = true;
        }

        // @async et @fire : l'appelant voit une promesse (ou void), le corps renvoie le résultat de cette promesse.
        var asyncMode = _AsyncModeOf(gear);
        IrType? returnType;
        IrType? bodyType = null;
        PromiseType? promise = null;
        if (asyncMode is null)
        {
            failed = true;
            returnType = null;
        }
        else if (asyncMode is AsyncMode.None or AsyncMode.Sync)
            returnType = gear.ReturnType is AST.Types.PrimitiveType { Keyword: TokenKind.VoidKeyword } ? PrimitiveType.Void : _BindType(gear.ReturnType);
        else if (_BindAsyncSignature(gear, asyncMode.Value) is { } bound)
            (promise, returnType, bodyType) = bound;
        else
        {
            failed = true;
            returnType = null;
        }

        if (asyncMode == AsyncMode.Task)
        {
            foreach (var parameter in parameters.Where(p => _SharesState(p.Type)))
            {
                _Error(parameter.Declaration, DiagnosticCodes.TaskUnsafe, $"le paramètre « {parameter.Name} » de « {name} » (@task) est un « {parameter.Type} » : il ne passe pas à un thread du pool",
                    "ne se copie pas pour de bon", _SharedReason(parameter.Type));
                failed = true;
            }
        }

        if (gear.IsNative && returnType is not null) returnType = _NativeBoundaryType(returnType);

        var kind = gear.Keyword switch
        {
            TokenKind.GuardKeyword => GearKind.Guard,
            TokenKind.EmitKeyword => GearKind.Emit,
            _ => GearKind.Gear,
        };
        return failed || returnType is null ? null : new GearSymbol(name, kind, [.. parameters], returnType, gear.IsPure, gear.Name.Span, cppName is null ? key : null)
        {
            CppNameOverride = cppName,
            IsTrain = isTrain,
            IsTrusted = _Trusted(gear),
            DeclaredUnsafe = gear.Keyword == TokenKind.UnsafeKeyword,
            IsUnsafe = gear.Keyword == TokenKind.UnsafeKeyword || (gear.IsNative || _Trusted(gear)) && (_RawType(returnType) || parameters.Any(p => _RawType(p.Type))),
            Deprecation = _DeprecationOf(gear),
            NativeSource = gear.IsNative ? _nativeSources?.GetValueOrDefault(gear.Name.Name) : null,
            NativeBody = gear.IsNative ? _NativeBodyFor(gear) : null,
            IgnoresError = _IgnoresErrorOf(gear),
            Async = asyncMode ?? AsyncMode.None,
            Promise = promise,
            BodyType = bodyType ?? returnType,
        };
    }

    /// <summary>
    /// Vrai pendant la liaison d'une valeur par défaut : le gear appelé la calcule dans un enveloppeur, hors de son corps,
    /// donc ni <c>fail</c>, <c>done</c>, <c>as!</c> ni appel de guard n'y sont permis, même dans un guard.
    /// </summary>
    private bool _bindingDefault;

    /// <summary>Vrai dans le corps d'un guard, là où il peut échouer.</summary>
    private bool _InGuardBody => _currentGear is { Kind: GearKind.Guard } && _lambda is null && !_bindingDefault;

    /// <summary>
    /// Les valeurs par défaut écrites de <paramref name="symbol"/>, liées dans le gear : chacune lit les paramètres qui la
    /// précèdent (comme en JS), converties au type de son paramètre.
    /// </summary>
    private IReadOnlyList<IrParameterDefault> _BindDefaults(GearSymbol symbol)
    {
        var result = new List<IrParameterDefault>();
        if (symbol.Parameters.All(p => p.Default != ParameterDefault.Expression)) return result;

        var all = _parameters;
        _parameters = new Dictionary<string, ParameterSymbol>(StringComparer.Ordinal);
        _bindingDefault = true;
        foreach (var parameter in symbol.Parameters)
        {
            if (parameter.Default == ParameterDefault.Expression
                && _BindExpected(parameter.DefaultSyntax!, parameter.Type) is { } value
                && _ConvertTo(value, parameter.Type, $"le paramètre « {parameter.Name} » est un « {parameter.Type} »") is { } converted)
                result.Add(new IrParameterDefault(parameter, converted));
            _parameters[parameter.Name] = parameter;
        }
        _bindingDefault = false;
        _parameters = all;
        return result;
    }

    /// <summary>Les noms des valeurs par défaut que l'appelant remplit lui-même, avec ce qu'ils valent.</summary>
    private static readonly Dictionary<string, ParameterDefault> SpecialDefaults = new(StringComparer.Ordinal)
    {
        ["__CALLER__"] = ParameterDefault.Caller,
        ["__WHERE__"] = ParameterDefault.Where,
        ["__FILE__"] = ParameterDefault.File,
        ["__LINE__"] = ParameterDefault.Line,
    };

    /// <summary>
    /// Ce que vaut <paramref name="parameter"/> quand l'appel ne le donne pas, après avoir vérifié qu'il peut avoir une valeur
    /// par défaut : pas avant un paramètre obligatoire, ni <c>mut</c> ni <c>this</c>, et une valeur spéciale du bon type
    /// (<c>__LINE__</c> est un entier, les autres un <c>string</c>). <see langword="null"/> après une erreur signalée.
    /// </summary>
    private ParameterDefault? _CheckDefault(Parameter parameter, IrType type, bool earlierDefault)
    {
        var name = parameter.Name.Name;
        if (parameter.Default is null)
        {
            if (!earlierDefault) return ParameterDefault.None;
            _Error(parameter.Name.Span, DiagnosticCodes.InvalidDefault, $"« {name} » est obligatoire, mais il suit un paramètre par défaut", "obligatoire après un défaut",
                "les paramètres par défaut viennent après ceux qui n'en ont pas : déplace-le avant, ou donne-lui une valeur");
            return null;
        }

        if (parameter.IsMutable || parameter.IsThis)
        {
            _Error(parameter.Default.Span, DiagnosticCodes.InvalidDefault, $"le paramètre « {name} » ne peut pas avoir de valeur par défaut", "valeur par défaut",
                parameter.IsThis ? "this est la valeur sur laquelle le gear s'appelle : elle est toujours donnée" : "un paramètre mut désigne la variable de l'appelant : elle est toujours donnée");
            return null;
        }

        if (parameter.Default is not NameExpression { Name.Name: var special } || !SpecialDefaults.TryGetValue(special, out var kind)) return ParameterDefault.Expression;

        var accepted = kind == ParameterDefault.Line ? type is PrimitiveType { IsInteger: true } : type == PrimitiveType.String;
        if (accepted) return kind;
        _Error(parameter.Default.Span, DiagnosticCodes.InvalidDefault, $"{special} est {(kind == ParameterDefault.Line ? "un entier" : "un string")} : « {name} » est un « {type} »", $"{(kind == ParameterDefault.Line ? "entier" : "string")} attendu",
            kind == ParameterDefault.Line ? $"déclare le paramètre uint {name}: {special}" : $"déclare le paramètre string {name}: {special}");
        return null;
    }

    /// <summary>
    /// <c>params [T] reste</c> : le dernier paramètre d'un gear, un tableau <c>[T]</c> sans <c>mut</c>, <c>this</c> ni valeur par défaut (E134). Il vaut un tableau vide quand
    /// l'appel ne lui donne rien. <see langword="null"/> après une erreur signalée.
    /// </summary>
    private ParameterDefault? _CheckParams(Parameter parameter, IrType type, bool isLast)
    {
        var name = parameter.Name.Name;
        string? problem = null;
        if (!isLast) problem = $"« {name} » n'est pas le dernier paramètre : params reçoit tous les arguments qui restent";
        else if (type is not ArrayType { Kind: ArrayKind.Dynamic }) problem = $"« {name} » est un « {type} » : params s'écrit devant un tableau [T]";
        else if (parameter.IsMutable || parameter.IsThis) problem = $"« {name} » ne peut être ni mut ni this";
        else if (parameter.Default is not null) problem = $"« {name} » n'a pas de valeur par défaut : sans argument, il est un tableau vide";
        if (problem is null) return ParameterDefault.Params;

        _Error(parameter.Span, DiagnosticCodes.InvalidParams, problem, "params mal placé", "par exemple : gear somme(int base, params [int] valeurs): int { … }, appelé somme(1, 2, 3) ou somme(1, [2, 3])");
        return null;
    }

    /// <summary>Signale un <c>params</c> là où il n'est pas permis (train, opérateur).</summary>
    private void _RejectParams(Parameter parameter, string owner) =>
        _Error(parameter.Span, DiagnosticCodes.InvalidParams, $"{owner} n'a pas de paramètre params", "params", "seuls les paramètres d'un gear, d'un guard ou d'un emit peuvent le faire");

    /// <summary>Le train en cours de liaison, pour <c>__CALLER__</c> ; <see langword="null"/> hors d'un train.</summary>
    private string? _trainName;

    /// <summary>
    /// L'argument d'un paramètre que l'appel <paramref name="span"/> ne donne pas : pour <c>__CALLER__</c>, <c>__WHERE__</c>,
    /// <c>__FILE__</c> et <c>__LINE__</c>, la valeur de l'appelant, connue ici ; pour une expression, une place que le gear appelé remplit.
    /// </summary>
    private IrExpression _OmittedArgument(ParameterSymbol parameter, SourceSpan span)
    {
        var position = span.StartPosition;
        var file = Path.GetFileName(span.Source.Path);
        return parameter.Default switch
        {
            ParameterDefault.Caller => new IrStringConstant(span, PrimitiveType.String, _lambda is not null ? "lambda" : _currentGear?.Name ?? _trainName ?? "fichier"),
            ParameterDefault.Where => new IrStringConstant(span, PrimitiveType.String, $"{file}:{position.Line}:{position.Column}"),
            ParameterDefault.File => new IrStringConstant(span, PrimitiveType.String, file),
            ParameterDefault.Line => new IrIntegerConstant(span, PrimitiveType.Int, position.Line),
            ParameterDefault.Params => new IrArrayValue(span, (ArrayType)parameter.Type, []),
            _ => new IrOmittedArgument(span, parameter.Type),
        };
    }

    /// <summary>Signale la valeur par défaut d'un paramètre qui ne peut pas en avoir (train, opérateur).</summary>
    private void _RejectDefault(Parameter parameter, string owner) =>
        _Error(parameter.Default!.Span, DiagnosticCodes.InvalidDefault, $"{owner} n'a pas de paramètre par défaut", "valeur par défaut",
            "seuls les paramètres d'un gear, d'un guard ou d'un emit peuvent en avoir une");

    /// <summary>
    /// Le corps d'un gear, d'un guard ou d'un emit, dans une portée dont le parent est celle des
    /// constantes du fichier. S'il renvoie une valeur, il ne doit jamais atteindre la fin de son
    /// corps (E056) : un guard finit par <c>done</c>, <c>fail</c> ou <c>return</c>.
    /// </summary>
    private IrGear? _BindGear(GearDeclaration gear)
    {
        if (!_gearOverloads.GetValueOrDefault(_DeclKey(gear.Name.Name))?.Any(s => s.Declaration == gear.Name.Span) ?? true) return null;
        var symbol = _gearOverloads[_DeclKey(gear.Name.Name)].Single(s => s.Declaration == gear.Name.Span);
        return _BindGearBody(gear, symbol);
    }

    /// <summary>Le corps de <paramref name="gear"/>, dont <paramref name="symbol"/> est la signature (celle d'une instance, pour un générique).</summary>
    private IrGear? _BindGearBody(GearDeclaration gear, GearSymbol symbol)
    {
        // @checked et @unchecked règlent le débordement des entiers dans ce corps seulement.
        var saved = _overflow;
        var mode = _OverflowOf(gear);
        if (mode is { } given) _overflow = given;
        try
        {
            var bound = _BindGearBodyCore(gear, symbol);
            if (bound is not null && mode is not null)
            {
                bound = bound with { Overflow = mode };
                _boundGears[symbol] = bound;
            }
            return bound;
        }
        finally { _overflow = saved; }
    }

    /// <summary>Le mode de débordement que <c>@checked</c> ou <c>@unchecked</c> impose au gear ; <see langword="null"/> sans l'une ni l'autre.</summary>
    private static OverflowMode? _OverflowOf(GearDeclaration gear)
    {
        var names = gear.Notes?.Annotations.OfType<OtherAnnotation>().Select(a => a.Name.Name).Where(n => n is "checked" or "unchecked").ToArray() ?? [];
        return names.Length == 0 ? null : names[0] == "checked" ? OverflowMode.Checked : OverflowMode.Unchecked;
    }

    private IrGear? _BindGearBodyCore(GearDeclaration gear, GearSymbol symbol)
    {
        if (gear.IsNative) return _BindNativeGear(gear, symbol);

        _currentGear = symbol;
        _parameters = symbol.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _calls[symbol] = [];
        var errorsBefore = _ErrorCount;
        _suspends = false;
        var defaults = _BindDefaults(symbol);
        var befores = _BindBefores(gear, symbol);

        IrBlock body;
        if (gear.Body is { } block) body = _BindStatements(block.Span, block.Statements);
        else
        {
            // Corps expression : sa valeur est renvoyée ; dans un gear void, elle est évaluée pour son effet.
            var value = _BindExpected(gear.ExpressionBody!, _ExpectedBody(symbol));
            IrStatement? statement = null;
            if (value is not null)
            {
                if (symbol.BodyType == PrimitiveType.Void) statement = new IrExpressionStatement(value.Span, value);
                else if (_ConvertTo(value, symbol.BodyType, $"« {symbol.Name} » renvoie un « {_ExpectedBody(symbol)} »") is { } converted)
                    statement = new IrReturn(value.Span, converted);
            }
            body = new IrBlock(gear.ExpressionBody!.Span, statement is null ? [] : [statement]);
        }

        if (befores.Count > 0) body = new IrBlock(body.Span, [.. befores, .. body.Statements]);
        if (_ErrorCount == errorsBefore) _CheckPointerLeaks(body);
        var taps = _BindTaps(gear, symbol);

        if (symbol.IsCoroutine && !_suspends && _ErrorCount == errorsBefore)
            _diagnostics.Warning(gear.Name.Span, DiagnosticCodes.AsyncNeverSuspends, $"« {symbol.Name} » est @{(symbol.Async == AsyncMode.Fire ? "fire" : "async")} mais n'attend jamais", "ne se suspend jamais",
                ["un gear qui n'écrit aucun await ni sleep n'a pas besoin d'être une coroutine : retire l'annotation"]);

        // Après une erreur dans le corps, une instruction a pu disparaître : E056 serait une erreur en cascade.
        var implicitDone = false;
        if (symbol.BodyType != PrimitiveType.Void && _ErrorCount == errorsBefore && ControlFlow.CanCompleteNormally(body))
        {
            // Un guard qui renvoie le type de son premier paramètre finit par un done implicite.
            if (symbol.Kind == GearKind.Guard && symbol.Parameters is [var first, ..] && first.Type == symbol.ReturnType)
            {
                implicitDone = true;
                _diagnostics.Warning(gear.Name.Span, DiagnosticCodes.ImplicitDone, $"le guard « {symbol.Name} » peut finir sans done, fail ni return : il laisse alors passer « {first.Name} »", "done implicite",
                    ["écris « done; » à la fin du guard pour le dire"]);
            }
            else if (symbol.Kind == GearKind.Guard)
                _Error(gear.Name.Span, DiagnosticCodes.MissingReturn, $"le guard « {symbol.Name} » peut finir sans done, fail ni return", "un chemin ne finit pas",
                    "chaque chemin du guard se termine par « done; » (la valeur reçue passe), « fail …; » ou « return valeur; »",
                    "vérifie les branches sans else et les cas de switch");
            else
                _Error(gear.Name.Span, DiagnosticCodes.MissingReturn, $"« {symbol.Name} » peut finir sans renvoyer de « {_ExpectedBody(symbol)} »", "un chemin ne renvoie rien",
                    $"chaque chemin du {_KindName(symbol)} se termine par « return valeur; » : vérifie les branches sans else et les cas de switch",
                    $"un {_KindName(symbol)} qui ne renvoie rien se déclare « : void »");
        }

        // Un tap qui échoue (il appelle un guard) fait échouer le guard : ses erreurs sont celles du gear.
        if (symbol.Kind == GearKind.Guard) _CollectErrors(symbol, taps.Count == 0 ? body : new IrBlock(body.Span, [.. body.Statements, .. taps.Select(t => t.Statement)]));
        _currentGear = null;
        _parameters = new(StringComparer.Ordinal);
        var bound = new IrGear(gear.Span, symbol, body, implicitDone, defaults, taps.Count == 0 ? null : taps);
        _boundGears[symbol] = bound;
        return bound;
    }

    /// <summary>Les gears dont le corps est lié, pour le mode sûr (un handler lit-il l'erreur qu'il reçoit).</summary>
    private readonly Dictionary<GearSymbol, IrGear> _boundGears = [];

    /// <summary>Pour chaque guard, les erreurs qu'il envoie lui-même, et les guards qu'il appelle.</summary>
    private readonly Dictionary<GearSymbol, (HashSet<IrType> Errors, HashSet<GearSymbol> Callees)> _guardErrors = [];

    /// <summary>
    /// Les erreurs qu'un guard peut envoyer de lui-même : celles de ses <c>fail</c>, une CastError pour
    /// un <c>as!</c>, un cast ou une borne contrôlés, une Exception pour une division entière par une
    /// valeur inconnue ; et les guards qu'il appelle, dont il transmet les erreurs.
    /// </summary>
    private void _CollectErrors(GearSymbol guard, IrBlock body)
    {
        var errors = IrWalk.Statements(body).OfType<IrFailStatement>().Select(f => f.Error.Type).ToHashSet();
        // Lire ou écrire un fichier peut échouer : IoError.
        if (IrWalk.Statements(body).OfType<IrWriteFile>().Any()) errors.Add(new MoldType(_ioError));
        if (IrWalk.Statements(body).OfType<IrWriteJson>().Any() || IrWalk.Statements(body).OfType<IrWriteXml>().Any())
        {
            errors.Add(new MoldType(_ioError));
            errors.Add(new MoldType(_parseError));
        }
        var callees = new HashSet<GearSymbol>();
        // Un guard appliqué par « ||> » écarte l'élément qui échoue : son échec ne remonte pas.
        var dropped = IrWalk.AllExpressions(body).OfType<IrEach>().SelectMany(e => e.Stages.Where(s => !s.IsStrict).Select(s => s.Apply))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var expression in IrWalk.AllExpressions(body))
        {
            switch (expression)
            {
                case IrCall { Gear.Kind: GearKind.Guard, AsResult: false } call when !dropped.Contains(call):
                    callees.Add(call.Gear);
                    break;

                case IrReadFile or IrOpenFile or IrFileOp { Operation: not FileOperation.Close }:
                    errors.Add(new MoldType(_ioError));
                    break;

                case IrReadJson or IrReadXml:
                    errors.Add(new MoldType(_ioError));
                    errors.Add(new MoldType(_parseError));
                    break;

                case IrBytesToString { IsForced: true }:
                    errors.Add(new MoldType(_castError));
                    break;

                case IrIndex { Checked: true } or IrAdd:
                case IrPush push when _Unbranded(push.Target).Type is ArrayType { Kind: ArrayKind.Bounded }:
                    errors.Add(new MoldType(_exception));
                    break;

                case IrParseNumber or IrNumericCast { Checked: true } or IrBoundCheck or IrArrayConvert { Mode: ArrayConversion.Checked }:
                case IrIncrement { Variable.Type: var type } when (type is IngotType ingot ? ingot.Underlying : type) is PrimitiveType { Bounds: not null }:
                    errors.Add(new MoldType(_castError));
                    break;

                case IrBinary { Operator: IrBinaryOperator.Divide or IrBinaryOperator.Remainder, Type: PrimitiveType { IsInteger: true } } division
                    when ConstantFolder.Fold(division.Right) is null && ConstantFolder.Fold(division) is null:
                    errors.Add(new MoldType(_exception));
                    break;
            }
        }
        _guardErrors[guard] = (errors, callees);
    }

    /// <summary>
    /// Les erreurs de chaque guard, une fois tous les corps liés : les siennes et, de proche en
    /// proche, celles des guards qu'il appelle. <see cref="GearSymbol.Errors"/> les réunit en un type.
    /// </summary>
    private void _ResolveGuardErrors()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var (_, own) in _guardErrors)
            {
                foreach (var callee in own.Callees)
                {
                    if (!_guardErrors.TryGetValue(callee, out var other)) continue;
                    foreach (var error in other.Errors) changed |= own.Errors.Add(error);
                }
            }
        } while (changed);

        foreach (var (guard, own) in _guardErrors) guard.Errors = own.Errors.Count == 0 ? null : UnionType.Of(own.Errors);
    }

    /// <summary>« gear », « guard » ou « emit », pour les messages.</summary>
    private static string _KindName(GearSymbol gear) => gear.Kind.ToString().ToLowerInvariant();

    /// <summary>
    /// <c>fail</c> : seulement dans un guard (E060). Il arrête le guard et envoie une erreur — une
    /// Exception pour un texte ou un <c>fail;</c> seul —, dont <c>where</c> reçoit la position du <c>fail</c>.
    /// </summary>
    private IrStatement? _BindFail(FailStatement fail)
    {
        var error = _BindFailValue(fail.Span, fail.Message);
        if (!_RequireGuard(fail.Span, "fail") || error is null) return null;
        return new IrFailStatement(fail.Span, error);
    }

    /// <summary>
    /// <c>panic</c> : permis partout. Un texte ou une erreur ; <c>panic;</c> seul a un message par défaut.
    /// </summary>
    private IrStatement? _BindPanic(PanicStatement panic)
    {
        if (panic.Message is null) return new IrPanic(panic.Span, new IrStringConstant(panic.Span, PrimitiveType.String, "arrêt fatal"));

        var value = _BindExpression(panic.Message);
        if (value is null) return null;
        if (_IsError(value.Type)) return new IrPanic(panic.Span, value);
        if (_Coerce(value, PrimitiveType.String) is { } message) return new IrPanic(panic.Span, message);

        _Error(value.Span, DiagnosticCodes.NotAnError, $"un panic prend un texte ou une erreur, pas un « {value.Type} »", $"de type « {value.Type} »",
            "une erreur est un mold qui a au moins string message et string where : déclare-la avec error Nom { … }",
            "par exemple : panic \"état impossible\"; ou panic new NotFound {path: chemin};");
        return null;
    }

    /// <summary>
    /// <c>done</c> : seulement dans un guard (E060), qui renvoie alors son premier paramètre ; son
    /// type de retour doit donc être celui de ce paramètre (E065).
    /// </summary>
    private IrStatement? _BindDone(DoneStatement done)
    {
        // Dans la lambda d'un |+> : done valeur; arrête l'accumulation avec cette valeur.
        if (_lambda is { FoldType: { } accumulator })
        {
            if (done.Value is null)
            {
                _Error(done.Span, DiagnosticCodes.FailOutsideGuard, "done dans la lambda d'un |+> porte la valeur qui arrête l'accumulation", "valeur manquante", "par exemple : done acc;");
                return null;
            }
            var stop = _BindExpected(done.Value, accumulator);
            return stop is not null && _ConvertTo(stop, accumulator, $"l'accumulateur est un « {accumulator} »") is { } converted ? new IrReturn(done.Span, converted, StopsFold: true) : null;
        }

        if (done.Value is not null)
        {
            _Error(done.Span, DiagnosticCodes.FailOutsideGuard, "done valeur; n'est permis que dans la lambda d'un |+>", "pas dans une accumulation",
                "dans un guard, done laisse passer la valeur reçue : « done; »; ailleurs, termine par « return valeur; »");
            return null;
        }

        if (!_RequireGuard(done.Span, "done")) return null;

        var guard = _currentGear!;
        if (guard.Parameters is not [var first, ..] || first.Type != guard.ReturnType)
        {
            var received = guard.Parameters.Count == 0 ? "il n'a aucun paramètre" : $"son premier paramètre « {guard.Parameters[0].Name} » est un « {guard.Parameters[0].Type} »";
            _Error(done.Span, DiagnosticCodes.DoneTypeMismatch, $"done renvoie la valeur reçue par « {guard.Name} », mais {received} et il renvoie un « {guard.ReturnType} »", "type différent",
                "done laisse passer le premier paramètre : le guard renvoie alors le même type que lui",
                "sinon, termine par « return valeur; »");
            return null;
        }

        if (LifecycleRules.Has(first.Type))
        {
            _Error(done.Span, DiagnosticCodes.LifecycleCopy, $"« {first.Type} » a un cycle de vie : done rendrait une copie de « {first.Name} », qui appartient à l'appelant", "copie interdite",
                "un guard ne rend pas la valeur à cycle de vie qu'il reçoit : ne le fais pas passer par done");
            return null;
        }
        return new IrReturn(done.Span, new IrParameterReference(done.Span, first), IsDone: true);
    }

    /// <summary>
    /// Vrai dans le corps d'un guard ; ailleurs, signale que <paramref name="what"/> (<c>fail</c>,
    /// <c>done</c>, <c>as!</c>) n'y est pas permis (E060).
    /// </summary>
    private bool _RequireGuard(SourceSpan span, string what)
    {
        if (_InGuardBody) return true;

        var where = _lambda is not null ? "dans une lambda" : _currentGear is null ? "hors d'un gear" : $"dans le {_KindName(_currentGear)} « {_currentGear.Name} »";
        _Error(span, DiagnosticCodes.FailOutsideGuard, $"« {what} » n'est permis que dans un guard, pas {where}", "pas dans un guard",
            what == "done"
                ? "done termine un guard en laissant passer la valeur reçue ; un gear termine par « return valeur; »"
                : "seul un guard peut échouer : déclare-le « guard nom(…): T { … } » et appelle-le comme étape du train");
        return false;
    }

    /// <summary>Nombre d'erreurs signalées jusqu'ici.</summary>
    private int _ErrorCount => _diagnostics.Items.Count(d => d.Level == DiagnosticLevel.Error);

    /// <summary>
    /// <c>return</c> : seulement dans un gear (E059) ; sa valeur a le type de retour (E003), et
    /// manque seulement dans un gear <c>void</c> (E056), qui n'en renvoie aucune (E040).
    /// </summary>
    private IrStatement? _BindReturn(ReturnStatement returnStatement)
    {
        if (_lambda is { } lambda) return _BindLambdaReturn(returnStatement, lambda);
        if (_currentGear is not { } gear)
        {
            _Error(returnStatement.Span, DiagnosticCodes.ReturnOutsideGear, "return hors d'un gear", "pas de gear à quitter",
                "dans le train, « |> $ valeur » fixe le code de sortie");
            return null;
        }

        if (returnStatement.Value is null)
        {
            if (gear.BodyType == PrimitiveType.Void) return new IrReturn(returnStatement.Span, null);
            _Error(returnStatement.Span, DiagnosticCodes.MissingReturn, $"return sans valeur dans « {gear.Name} », qui renvoie un « {_ExpectedBody(gear)} »", "valeur manquante",
                $"par exemple : return 0;");
            return null;
        }

        var value = _BindStatementValue(returnStatement.Value, _ExpectedBody(gear));
        if (value is null) return null;

        if (gear.BodyType == PrimitiveType.Void)
        {
            _Error(value.Span, DiagnosticCodes.TypeMismatch, $"« {gear.Name} » est void : il ne renvoie pas de valeur", "valeur en trop",
                "écris « return; », ou déclare un type de retour au gear");
            return null;
        }

        var converted = _NoCopyOnReturn(_ConvertTo(value, gear.BodyType, $"« {gear.Name} » renvoie un « {_ExpectedBody(gear)} »"), returnStatement.Value.Span);
        return converted is null ? null : new IrReturn(returnStatement.Span, converted);
    }

    /// <summary>Un appel <c>g(a, b)</c> : le gear doit exister (E035).</summary>
    private IrExpression? _BindCall(CallExpression call)
    {
        // ArenaPool.set(valeur) construit la valeur avec le pool pour allocateur.
        if (call.Callee is MemberAccessExpression { Target: NameExpression { Name.Name: "ArenaPool" }, Member.Name: "set", IsSafe: false }) return _BindPoolValue(call);

        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "free" } free && call.Arguments.Count == 0 && _SyntacticType(free.Target) is PointerType)
        {
            var pointer = _BindExpression(free.Target);
            return pointer is null ? null : _BindPointerFree(call.Span, pointer);
        }

        if (call.Callee is NameExpression { Name.Name: "pack" } && !_IsValueName("pack") && !_TryFind(_gears, "pack", out _))
            return _BindPack(call);

        if (call.Callee is MemberAccessExpression { IsSafe: false } descriptorMember)
        {
            var described = _TryDescriptorCall(call, descriptorMember, out var handledDescriptor);
            if (handledDescriptor) return described;
        }
        // f.readLine(), f.write(texte) : une méthode d'un flux de fichier (FileStream).
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "readLine" or "readBytes" or "write" or "close" } streamMember && _IsFileStream(streamMember.Target))
            return _BindFileOperation(call, streamMember);

        // id.get() : l'entrée d'un dataset que désigne l'identifiant ; id.name() : son nom ; Moves.find(nom) : l'identifiant d'un nom.
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "get" or "name" } getter && call.Arguments.Count == 0 && _SyntacticType(getter.Target) is IngotType { Symbol.Dataset: { } getterDataset })
            return getter.Member.Name == "get" ? _BindDatasetGet(call, getter, getterDataset) : _BindDatasetName(call, getter, getterDataset);
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "find" } finder && call.Arguments.Count == 1 && _DatasetNamed(finder.Target) is { } findDataset)
            return _BindDatasetFind(call, finder, findDataset);

        // Une table mutable : Moves.table() en donne une copie ([Move], où l'on ajoute des entrées), id.get(table) lit l'entrée (Move?), Moves.at(position) donne l'identifiant d'une position.
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "get" } tableGetter && call.Arguments.Count == 1 && _SyntacticType(tableGetter.Target) is IngotType { Symbol.Dataset: { } tableDataset })
            return _BindDatasetGetFrom(call, tableGetter, tableDataset);
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "table" } tableMember && call.Arguments.Count == 0 && _DatasetNamed(tableMember.Target) is { } tableOwner)
            return _BindDatasetTable(call, tableOwner);
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "at" } atMember && call.Arguments.Count == 1 && _DatasetNamed(atMember.Target) is { } atDataset)
            return _BindDatasetAt(call, atDataset);

        // r.test(s), /a+/g.replace(s, "b") : une méthode d'une regex.
        if (call.Callee is MemberAccessExpression { IsSafe: false } regexMember && _regexMethods.Contains(regexMember.Member.Name) && _SyntacticType(regexMember.Target) == PrimitiveType.Regex)
            return _BindRegexOperation(call, regexMember);

        // d.keys(), d.values(), d.without(k) : les méthodes d'un dictionnaire qu'une extension n'écrase pas.
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "keys" or "values" or "without" or "put" } dictionaryMember
            && _SyntacticType(dictionaryMember.Target) is null or DictionaryType)
        {
            return _BindMethodCall(call, dictionaryMember);
        }

        // Type.f(x), v.f(x) : un gear d'extension.
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: not ("push" or "add" or "has") } extended)
        {
            var extension = _TryBindExtensionCall(call, extended, out var handled);
            if (handled) return extension;
        }

        // nums.push(4), ages.has("a") : une méthode d'une collection.
        // Hors des méthodes des collections, pos.f(x) appelle la lambda d'un champ.
        if (call.Callee is MemberAccessExpression { IsSafe: false, Member.Name: "push" or "add" or "has" } member)
        {
            // Un type qui n'est pas une collection (un mold, un type décrit par un descriptor) peut avoir une extension de ce nom.
            if (_SyntacticType(member.Target) is { } targetType && targetType is not (ArrayType or DictionaryType or IngotType))
            {
                var extension = _TryBindExtensionCall(call, member, out var handled);
                if (handled) return extension;
            }
            return _BindMethodCall(call, member);
        }

        // f(4), ops[0](3), fabrique(2)(5) : l'appel d'une valeur, une lambda.
        // Commandes.ajouter(x) : un nom d'un fichier importé, lu comme un seul nom.
        var qualified = _QualifiedName(call.Callee) is not null ? new Identifier(call.Callee.Span) : null;
        if (qualified is null && (call.Callee is not NameExpression || _IsValueName(((NameExpression)call.Callee).Name.Name)))
            return _BindInvoke(call.Span, _BindExpression(call.Callee), null, call.Arguments);
        var name = qualified ?? ((NameExpression)call.Callee).Name;

        // ok(v) et err(e) construisent un résultat, sauf si un gear porte ce nom.
        if (name.Name is "ok" or "err" && !_TryFind(_gears, name.Name, out _)) return _BindResultConstructor(call, name.Name == "ok");

        // parseJson<T>(texte) et toJson(valeur) : le JSON d'un texte, sans fichier, sauf si un gear porte ce nom.
        if (name.Name is "parseJson" or "toJson" && qualified is null && !_TryFind(_gears, name.Name, out _)) return _BindJsonText(call, name.Name == "parseJson");

        // sleep(500ms) rend une promesse sans valeur, sauf si un gear porte ce nom.
        if (name.Name == "sleep" && qualified is null && !_TryFind(_gears, name.Name, out _)) return _BindSleep(call);

        // readAsync(chemin) et writeAsync(chemin, texte) rendent une promesse, sauf si un gear porte ce nom.
        if (name.Name is "readAsync" or "writeAsync" or "readBytesAsync" && qualified is null && !_TryFind(_gears, name.Name, out _)) return _BindAsyncFile(call, name.Name != "writeAsync", name.Name == "readBytesAsync");

        // whenAny(promesses) rend la promesse de la première qui finit, sauf si un gear porte ce nom.
        if (name.Name == "whenAny" && qualified is null && !_TryFind(_gears, name.Name, out _)) return _BindWhenAny(call);

        // spawnAsync(exécutable, arguments…) rend une promesse, sauf si un gear porte ce nom.
        if (name.Name is "spawnAsync" or "spawnAsyncWith" && qualified is null && !_TryFind(_gears, name.Name, out _)) return _BindAsyncSpawn(call, name.Name == "spawnAsyncWith");

        // UserId(42) : une valeur de la base, marquée.
        if (_TryFind(_ingots, name.Name, out var ingot)) return _BindIngotConstruction(call, ingot);

        var arguments = call.Arguments.Select((argument, i) => _BindExpected(argument, _ParameterTypeOf(name.Name, i))).ToArray();
        return _BindGearCall(call.Span, name, null, arguments, typeArguments: call.TypeArguments);
    }

    /// <summary>Le pipeline <c>v |&gt; g(a)</c> : l'appel <c>g(v, a)</c>.</summary>
    private IrExpression? _BindPipe(PipeExpression pipe)
    {
        var value = _BindExpression(pipe.Value);
        // v |> f : f est une lambda, qui reçoit v en premier argument.
        if (_IsValueName(pipe.Gear.Name))
            return value is null ? null : _BindInvoke(pipe.Span, _BindName(new NameExpression(pipe.Gear)), value, pipe.Arguments);

        var arguments = pipe.Arguments.Select((argument, i) => _BindExpected(argument, _ParameterTypeOf(pipe.Gear.Name, i + 1))).ToArray();
        return value is null ? null : _BindGearCall(pipe.Span, pipe.Gear, value, arguments);
    }

    /// <summary>Le pipeline <c>v |&gt; (x) =&gt; expression</c> : la lambda reçoit exactement la valeur de gauche.</summary>
    private IrExpression? _BindLambdaPipe(LambdaPipeExpression pipe)
    {
        var value = _BindExpression(pipe.Value);
        return value is null ? null : _BindPipelineLambda(pipe.Span, pipe.Lambda, value);
    }

    /// <summary>Lie une lambda qui joue le rôle d'un gear de pipeline : un paramètre, ni rail, ni terminaison.</summary>
    private IrExpression? _BindPipelineLambda(SourceSpan span, LambdaExpression lambda, IrExpression value)
    {
        if (lambda.Parameters.Count != 1)
        {
            _Error(lambda.Span, DiagnosticCodes.ArgumentCount,
                $"une lambda de pipeline reçoit la valeur courante : elle prend un paramètre, pas {lambda.Parameters.Count}",
                $"{lambda.Parameters.Count} paramètre(s)", "par exemple : valeur |> (x) => x + 1");
            return null;
        }
        var function = _BindLambda(lambda, null, value.Type);
        return _BindInvoke(span, function, value, []);
    }

    /// <summary>
    /// Le type du paramètre <paramref name="index"/> du gear <paramref name="gear"/>, attendu de
    /// l'argument écrit à cette place (une lambda y trouve ses paramètres) ; <see langword="null"/> s'il n'existe pas.
    /// </summary>
    private IrType? _ParameterTypeOf(string gear, int index)
    {
        if (_TryFind(_genericGears, gear, out _)
            || !_TryFind(_gearOverloads, gear, out var overloads) || overloads.Count != 1) return null;
        var parameters = overloads[0].Parameters;
        return index < parameters.Count - (parameters.Count > 0 && parameters[^1].IsParams ? 1 : 0) ? parameters[index].Type
            : parameters.Count > 0 && parameters[^1].IsParams && parameters[^1].Type is ArrayType variadic ? variadic.Element
            : index < parameters.Count ? parameters[index].Type : null;
    }

    /// <summary>Choisit la meilleure signature ordinaire sans utiliser le type de retour.</summary>
    private GearSymbol? _ResolveGearOverload(Identifier name, IrExpression? first, IReadOnlyList<IrExpression?> written, out bool ambiguous)
    {
        ambiguous = false;
        if (!_TryFind(_gearOverloads, name.Name, out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0];
        if (written.Any(a => a is null)) return candidates[0];
        IrExpression[] arguments = first is null ? [.. written!] : [first, .. written!];

        (GearSymbol Gear, (int Conversions, int Defaults, int Params) Rank)[] viable = [.. candidates.Select(gear => (Gear: gear, Rank: _CallRank(gear, arguments)))
            .Where(c => c.Rank is not null).Select(c => (c.Gear, c.Rank!.Value))];

        if (viable.Length == 0) return null;
        var bestRank = viable.Select(v => v.Rank).Aggregate((a, b) => _CompareRank(a, b) <= 0 ? a : b);
        var best = viable.Where(v => _CompareRank(v.Rank, bestRank) == 0).ToArray();
        if (best.Length == 1) return best[0].Gear;

        _ReportAmbiguousGear(name, best.Select(v => v.Gear));
        ambiguous = true;
        return null;
    }

    private static int _CompareRank((int Conversions, int Defaults, int Params) a, (int Conversions, int Defaults, int Params) b)
    {
        var conversion = a.Conversions.CompareTo(b.Conversions);
        if (conversion != 0) return conversion;
        var defaults = a.Defaults.CompareTo(b.Defaults);
        return defaults != 0 ? defaults : a.Params.CompareTo(b.Params);
    }

    private (int Conversions, int Defaults, int Params)? _CallRank(GearSymbol gear, IReadOnlyList<IrExpression> arguments)
    {
            var variadic = gear.Parameters.Count > 0 && gear.Parameters[^1].IsParams;
            var fixedCount = variadic ? gear.Parameters.Count - 1 : gear.Parameters.Count;
            var required = gear.Parameters.Count(p => !p.HasDefault);
            if (arguments.Count < required || !variadic && arguments.Count > gear.Parameters.Count) return null;

            var conversions = 0;
            for (var i = 0; i < arguments.Count; ++i)
        {
                IrType expected;
                if (i < fixedCount) expected = gear.Parameters[i].Type;
                else if (variadic && gear.Parameters[^1].Type is ArrayType array)
                {
                    // Un tableau unique peut être donné directement au paramètre params.
                    expected = arguments.Count == gear.Parameters.Count && i == fixedCount && _CanCoerce(arguments[i], array) ? array : array.Element;
                }
                else return null;

                if (arguments[i].Type == expected) continue;
                if (!_CanCoerce(arguments[i], expected)) return null;
                ++conversions;
        }
        return (conversions, Math.Max(0, gear.Parameters.Count - arguments.Count), variadic ? 1 : 0);
    }

    private void _ReportAmbiguousGear(Identifier name, IEnumerable<GearSymbol> candidates) =>
        _Error(name.Span, DiagnosticCodes.AmbiguousGear, $"l'appel de « {name.Name} » est ambigu", "plusieurs surcharges conviennent",
            $"candidates : {string.Join(" ; ", candidates.Select(g => g.ToString()))}",
            "convertis explicitement un argument pour choisir une signature");

    private static bool _ExactArguments(GearSymbol gear, IReadOnlyList<IrExpression> arguments)
    {
        if (gear.Parameters.Count != arguments.Count || gear.Parameters.Any(p => p.IsParams)) return false;
        return arguments.Zip(gear.Parameters).All(p => p.First.Type == p.Second.Type);
    }

    /// <summary>
    /// Les arguments d'un appel dont le gear finit par <c>params [T]</c> : ceux qui suivent les paramètres fixes deviennent les éléments d'un tableau, converti chacun
    /// en <c>T</c> (E003), sauf quand il n'y en a qu'un seul et qu'il s'accepte lui-même comme tableau <c>[T]</c> : il est alors le tableau. <see langword="null"/> après une erreur.
    /// </summary>
    private IrExpression[]? _PackParams(GearSymbol gear, IrExpression[] all)
    {
        var fixedCount = gear.Parameters.Count - 1;
        var parameter = gear.Parameters[^1];
        var array = (ArrayType)parameter.Type;
        var extra = all[fixedCount..];
        if (extra.Length == 1 && _CanCoerce(extra[0], array)) return all;

        List<IrArrayPart> parts = [];
        foreach (var value in extra)
        {
            if (_ConvertTo(value, array.Element, $"« {parameter.Name} » de « {gear.Name} » reçoit des « {array.Element} »") is not { } converted) return null;
            parts.Add(new IrArrayPart(converted, false));
        }
        return [.. all[..fixedCount], new IrArrayValue(extra[0].Span.Union(extra[^1].Span), array, parts)];
    }

    /// <summary>
    /// L'appel d'un gear : il existe (E035), reçoit autant d'arguments que de paramètres (E058),
    /// chacun du type de son paramètre (E003), et un gear <c>pure</c> n'appelle que des gears
    /// <c>pure</c> (E057). Un guard ou un emit ne s'appelle que comme étape d'un train (E062).
    /// L'appel est noté pour trouver les gears récursifs.
    /// </summary>
    /// <param name="span">L'appel dans le source.</param>
    /// <param name="name">Le nom du gear.</param>
    /// <param name="first">La valeur qui entre par le pipeline, premier argument ; <see langword="null"/> sans pipeline.</param>
    /// <param name="arguments">Les arguments écrits, déjà liés (<see langword="null"/> après une erreur).</param>
    /// <param name="asStep">Vrai pour une étape de train ou de rail, où un guard et un emit peuvent s'appeler.</param>
    private IrExpression? _BindGearCall(SourceSpan span, Identifier name, IrExpression? first, IReadOnlyList<IrExpression?> arguments, bool asStep = false, IReadOnlyList<AST.Interfaces.ITypeNode>? typeArguments = null, GearSymbol? resolved = null)
    {
        List<GearSymbol>? ordinaryOverloads = null;
        var hasOrdinaryOverloads = resolved is null && _TryFind(_gearOverloads, name.Name, out ordinaryOverloads);
        var gear = resolved;
        var ambiguous = false;
        if (gear is null) gear = _ResolveGearOverload(name, first, arguments, out ambiguous);
        if (ambiguous) return null;
        var found = gear is not null;
        var allGiven = first is null ? arguments.Where(a => a is not null).Cast<IrExpression>().ToArray()
            : arguments.All(a => a is not null) ? new[] { first }.Concat(arguments.Cast<IrExpression>()).ToArray() : [];
        if (resolved is null && arguments.All(a => a is not null) && _TryFind(_genericGearOverloads, name.Name, out var generics)
            && (gear is null || !_ExactArguments(gear, allGiven)))
        {
            // Une instance générique exacte précède une surcharge ordinaire qui demanderait une conversion ;
            // à rang égal, la signature non générique reste la plus spécifique.
            var instantiated = new List<(GearSymbol Gear, (int Conversions, int Defaults, int Params) Rank)>();
            foreach (var generic in generics)
            {
                var checkpoint = _diagnostics.Count;
                var hadErrors = _hasErrors;
                var candidate = _InstantiateGear(generic, typeArguments, allGiven, span);
                if (candidate is not null && _CallRank(candidate, allGiven) is { } rank) instantiated.Add((candidate, rank));
                else
                {
                    _diagnostics.Rollback(checkpoint);
                    _hasErrors = hadErrors;
                }
            }

            if (instantiated.Count > 0)
            {
                var bestRank = instantiated.Select(c => c.Rank).Aggregate((a, b) => _CompareRank(a, b) <= 0 ? a : b);
                var best = instantiated.Where(c => _CompareRank(c.Rank, bestRank) == 0).ToArray();
                var ordinaryRank = gear is null ? null : _CallRank(gear, allGiven);
                if (ordinaryRank is null || _CompareRank(bestRank, ordinaryRank.Value) < 0)
                {
                    if (best.Length > 1)
                    {
                        _ReportAmbiguousGear(name, best.Select(c => c.Gear));
                        return null;
                    }
                    gear = best[0].Gear;
                    found = true;
                }
            }
            else if (gear is null)
            {
                // Rejouer le premier candidat afin de conserver son diagnostic de déduction ou de contrainte.
                _InstantiateGear(generics[0], typeArguments, allGiven, span);
                return null;
            }
        }
        else if (gear is null && hasOrdinaryOverloads)
        {
            // Aucune surcharge ne convient : conserver les diagnostics détaillés E058/E003 existants.
            gear = ordinaryOverloads![0];
            found = true;
        }
        else if (gear is null) found = _TryFind(_gears, name.Name, out gear); // trains et gears synthétiques
        if (!found)
        {
            if (_IsPendingTrain(name.Name))
                _Error(name.Span, DiagnosticCodes.TrainCallOrder, $"le train « {name.Name} » est écrit plus bas : un train n'appelle que les trains écrits au-dessus de lui", "écrit plus bas",
                    "le graphe des trains est sans cycle : écris le train appelé au-dessus de celui qui l'appelle",
                    "les déclarations communes du fichier n'appellent aucun train du fichier");
            else
            {
                var visible = _VisibleNames(_gears).ToArray();
                _Error(name.Span, DiagnosticCodes.UnknownGear, $"gear « {name.Name} » introuvable", "inconnu",
                    visible.Length == 0 ? "aucun gear n'est déclaré dans ce fichier" : $"gears déclarés : {string.Join(", ", visible)}");
            }
            return null;
        }
        if (gear is null || arguments.Any(a => a is null)) return null;
        if (!asStep && (_RawType(gear.ReturnType) || gear.Parameters.Any(p => _RawType(p.Type))) && !_RequireUnsafe(span)) return null;
        _WarnDeprecated(gear, name.Span);
        _WarnSyncCall(gear, name.Span);
        if (gear.Async is AsyncMode.Async or AsyncMode.Fire or AsyncMode.Sync && _RejectInAllocatorScope(name.Span, $"l'appel du gear asynchrone « {gear.Name} »")) return null;

        // Un guard appelé par un guard lui transmet son échec.
        if (!asStep && !gear.IsTrain && gear.Kind != GearKind.Gear && !(gear.Kind == GearKind.Guard && _InGuardBody))
        {
            _Error(name.Span, DiagnosticCodes.GuardOrEmitInExpression, $"« {gear.Name} » est un {_KindName(gear)} : il ne s'appelle que comme étape d'un train", _KindName(gear),
                gear.Kind == GearKind.Guard
                    ? $"un guard peut échouer : seul un train, ou un autre guard qui échoue avec lui, peut l'appeler, par exemple : … |> {gear.Name} |> …"
                    : $"un emit termine un train : appelle-le en dernière étape, par exemple : … |> {gear.Name};");
            return null;
        }

        IrExpression[] all = first is null ? [.. arguments!] : [first, .. arguments!];
        var variadic = gear.Parameters.Count > 0 && gear.Parameters[^1].IsParams;
        var required = gear.Parameters.Count(p => !p.HasDefault);
        // params : les arguments qui dépassent les paramètres fixes deviennent le tableau du dernier.
        if (variadic && all.Length >= gear.Parameters.Count)
        {
            if (_PackParams(gear, all) is not { } packed) return null;
            all = packed;
        }
        if (all.Length < required || all.Length > gear.Parameters.Count)
        {
            var expectedCount = variadic ? $"au moins {required}" : required == gear.Parameters.Count ? $"{required}" : $"de {required} à {gear.Parameters.Count}";
            _Error(span, DiagnosticCodes.ArgumentCount, $"« {gear.Name} » attend {expectedCount} argument(s), pas {all.Length}", $"{all.Length} argument(s)",
                $"signature : {gear}" + (first is null ? "" : " (la valeur du pipeline est le premier argument)"));
            return null;
        }
        // Les paramètres que l'appel ne donne pas : la valeur de l'appelant, ou la place d'un défaut que le gear calcule.
        if (all.Length < gear.Parameters.Count) all = [.. all, .. gear.Parameters.Skip(all.Length).Select(p => _OmittedArgument(p, span))];

        // Un argument mut est la variable elle-même : elle a déjà le type exact du paramètre.
        if (!_CheckMutArguments(gear, all, _mutableElement)) return null;
        var converted = new IrExpression[all.Length];
        for (var i = 0; i < all.Length; ++i)
        {
            var parameter = gear.Parameters[i];
            if (parameter.IsMutable || all[i] is IrOmittedArgument) converted[i] = all[i];
            else if (_ConvertTo(all[i], parameter.Type, $"le paramètre « {parameter.Name} » de « {gear.Name} » est un « {parameter.Type} »") is { } argument)
            {
                if ((gear.IsCoroutine || gear.Async == AsyncMode.Task) && _NoCopy(argument, argument.Span, "un appel asynchrone par valeur") is null) return null;
                converted[i] = argument;
            }
            else return null;
        }

        if (!gear.IsPure && _lambda is null && _currentGear is { IsPure: true } caller)
        {
            _Error(name.Span, DiagnosticCodes.ImpureCall, $"« {caller.Name} » est pure, mais « {gear.Name} » ne l'est pas", "pas pure",
                $"un gear pure n'appelle que des gears pure : déclare « gear pure {gear.Name} », ou retire pure de « {caller.Name} »");
            return null;
        }
        if (!gear.IsPure && !_AllowEffect(name.Span, DiagnosticCodes.ImpureCall, $"appeler « {gear.Name} », qui n'est pas pure")) return null;

        if (_currentGear is { } current) _calls[current].Add(gear);

        // Hors d'un train, un train qui peut échouer renvoie un résultat T ! E.
        var asResult = !asStep && gear.IsTrain && gear.Errors is not null;
        if (asResult && gear.ReturnType == PrimitiveType.Void)
        {
            _Unsupported(span, $"l'appel du train « {gear.Name} », qui ne renvoie rien mais peut échouer, dans une expression");
            return null;
        }
        return new IrCall(span, gear, converted, asResult);
    }

    /// <summary>
    /// Les gears récursifs, directement ou par d'autres : ceux qui font partie d'un cycle d'appels
    /// (composantes fortement connexes de Tarjan, ou appel à soi-même). Chacun est marqué, et
    /// signalé (W010) même sans limite.
    /// </summary>
    private void _MarkRecursiveGears()
    {
        var index = new Dictionary<GearSymbol, int>();
        var low = new Dictionary<GearSymbol, int>();
        var stack = new Stack<GearSymbol>();
        var onStack = new HashSet<GearSymbol>();
        var counter = 0;

        void Visit(GearSymbol gear)
        {
            index[gear] = low[gear] = counter++;
            stack.Push(gear);
            onStack.Add(gear);

            foreach (var callee in _calls.GetValueOrDefault(gear) ?? [])
            {
                if (!index.ContainsKey(callee))
                {
                    Visit(callee);
                    low[gear] = Math.Min(low[gear], low[callee]);
                }
                else if (onStack.Contains(callee)) low[gear] = Math.Min(low[gear], index[callee]);
            }

            if (low[gear] != index[gear]) return;

            var component = new List<GearSymbol>();
            GearSymbol member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (member != gear);

            var recursive = component.Count > 1 || _calls.GetValueOrDefault(gear)?.Contains(gear) == true;
            if (!recursive) return;

            foreach (var symbol in component.OrderBy(c => c.Declaration.Start.Value))
            {
                symbol.IsRecursive = true;
                var through = component.Count == 1 ? "il s'appelle lui-même" : $"par {string.Join(" → ", component.Select(c => c.Name))}";
                var limit = _recursionLimit is { } max ? $"au plus {max} appels imbriqués (clé « recursion » de rgl.json)" : "sans limite (« recursion »: \"infinite\")";
                _diagnostics.Warning(symbol.Declaration, DiagnosticCodes.RecursiveGear, $"« {symbol.Name} » est récursif : {through}", "récursif",
                    [$"sa profondeur est limitée : {limit}"]);
            }
        }

        foreach (var gear in _calls.Keys.OrderBy(g => g.Declaration.Start.Value))
        {
            if (!index.ContainsKey(gear)) Visit(gear);
        }
    }
}
