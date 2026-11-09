using Rgl.AST;
using Rgl.AST.Collections;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;
using Rotomeca.Core.Collections;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Les molds : leurs déclarations, les types anonymes, les valeurs <c>{…}</c>, la lecture d'un champ
/// et le duck typing. Le duck typing suit TypeScript : une valeur qui a plus de champs que le mold
/// attendu passe (seuls ses champs sont gardés) ; il peut lui en manquer s'ils ont une valeur par
/// défaut ; une valeur écrite <c>{…}</c> ne peut pas nommer un champ inconnu du mold attendu.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Les molds déclarés, par nom ; un mold dont le nom est en double y vaut le premier.</summary>
    private readonly Dictionary<string, MoldSymbol> _molds = new(StringComparer.Ordinal);

    /// <summary>La déclaration de chaque mold nommé.</summary>
    private readonly Dictionary<MoldSymbol, MoldDeclaration> _moldDeclarations = [];

    /// <summary>Les molds dont les champs sont en cours de résolution (pour trouver les cycles de spreads) ou résolus.</summary>
    private readonly HashSet<MoldSymbol> _resolvingMolds = [];
    private readonly HashSet<MoldSymbol> _resolvedMolds = [];

    /// <summary>Les molds anonymes, par forme (noms et types des champs) : une même forme est un même type.</summary>
    private readonly Dictionary<string, MoldSymbol> _anonymousMolds = new(StringComparer.Ordinal);

    /// <summary>Tous les molds du programme : déclarés, puis anonymes dans l'ordre où ils apparaissent.</summary>
    private readonly RArray<MoldSymbol> _allMolds = new();

    /// <summary>La valeur par défaut écrite de chaque champ qui en a une, et sa valeur liée une fois calculée.</summary>
    private readonly Dictionary<MoldFieldSymbol, IExpression> _defaultSources = [];

    /// <summary>Les noms visibles là où chaque valeur par défaut est écrite : c'est là qu'elle est liée.</summary>
    private readonly Dictionary<MoldFieldSymbol, Namespace> _defaultNamespaces = [];

    /// <summary>Les molds dont la contenance est déjà vérifiée.</summary>
    private readonly HashSet<MoldSymbol> _containmentChecked = [];
    private readonly Dictionary<MoldFieldSymbol, IrExpression?> _defaults = [];

    /// <summary>La portée du fichier : celle de ses constantes, où les valeurs par défaut sont liées.</summary>
    private Scope? _fileScope;

    #region Déclarations

    /// <summary>
    /// Les types d'erreur standard : <c>Exception</c>, <c>CastError</c> d'un cast ou d'une borne qui
    /// échoue, et <c>LoopError</c> d'une boucle de guard qui atteint sa limite <c>&lt;!N&gt;</c>.
    /// </summary>
    private MoldSymbol _exception = null!;
    private MoldSymbol _castError = null!;
    private MoldSymbol _loopError = null!;
    private MoldSymbol _ioError = null!;
    private MoldSymbol _parseError = null!;

    /// <summary>Le mold <c>XmlNode</c> de la std, que lit <c>xml</c> ; <see langword="null"/> si la std n'est pas là.</summary>
    private MoldSymbol? _xmlNode;

    /// <summary>Les molds <c>ProcessResult</c> et <c>SpawnError</c> de la std, que <c>spawn</c> renvoie ; <see langword="null"/> si la std n'est pas là.</summary>
    private MoldSymbol? _processResult;
    private MoldSymbol? _spawnError;

    /// <summary>Vrai pour la prochaine expression liée seulement : elle est la valeur d'une déclaration, d'une affectation ou d'un return, ou une instruction, où <c>spawn</c> est permis.</summary>
    private bool _spawnAllowed;

    /// <summary>
    /// Réserve un nom de type déclaré. Molds, errors, enums et ingots partagent un seul espace de
    /// noms, où les erreurs standard ont déjà le leur : un nom déjà pris est E075.
    /// </summary>
    /// <param name="name">Le nom déclaré.</param>
    /// <param name="what">Ce qui le déclare, pour le message : « un mold », « un enum »…</param>
    /// <returns>Vrai si le nom était libre.</returns>
    private bool _ClaimTypeName(Identifier name, string what)
    {
        var text = name.Name;
        var key = _DeclKey(text);
        if (_IsStandardError(text) && !_ns.Module.Name.StartsWith("std.", StringComparison.Ordinal))
            _diagnostics.Warning(name.Span, DiagnosticCodes.StdNameMasked, $"« {text} » masque le type d'erreur standard du même nom", "masque la std",
                [$"le compilateur continue d'envoyer l'erreur standard (std.{text}) ; la tienne n'est qu'un type de plus"]);
        if (!_molds.ContainsKey(key) && !_genericMolds.ContainsKey(key) && !_enums.ContainsKey(key) && !_ingots.ContainsKey(key)) return _ClaimName(name, what, "type");

        _Error(name.Span, DiagnosticCodes.DuplicateMold,
            _IsStandardError(text) ? $"« {text} » est un type d'erreur standard : {what} ne peut pas prendre ce nom" : $"le type « {text} » est déjà déclaré",
            "nom déjà pris", "choisis un autre nom");
        return false;
    }

    /// <summary>Indique si <paramref name="name"/> est un type d'erreur standard.</summary>
    private static bool _IsStandardError(string name) => name is "Exception" or "CastError" or "LoopError";

    /// <summary>
    /// Les types d'erreur du compilateur, une fois <c>Errors.forge</c> lié : ses molds <c>Exception</c>, <c>CastError</c> et <c>LoopError</c>, que tout code
    /// généré utilise par identité, jamais par leur nom.
    /// </summary>
    private void _TakeStandardErrors()
    {
        _exception = _molds["std.Errors.Exception"];
        _castError = _molds["std.Errors.CastError"];
        _loopError = _molds["std.Errors.LoopError"];
        _ioError = _molds["std.Errors.IoError"];
        _parseError = _molds["std.Errors.ParseError"];
        _asyncException = _molds["std.Errors.AsyncException"];
        _nativeError = _molds.GetValueOrDefault("std.Errors.NativeError");
        _taskState = _enums["std.Errors.TaskState"];
    }

    /// <summary>Sans la std : des types d'erreur déclarés par le compilateur, sous le nom unique std.Nom.</summary>
    private void _DeclareStandardErrors()
    {
        MoldSymbol Standard(string name)
        {
            // Leur nom unique est std.Nom : un fichier peut déclarer son propre Nom (avec un avertissement) sans les toucher.
            var symbol = new MoldSymbol(name, default, "std." + name) { IsError = true, Fields = _ErrorFields(default) };
            _molds["std." + name] = symbol;
            _resolvedMolds.Add(symbol);
            _allMolds.Push(symbol);
            return symbol;
        }

        _exception = Standard("Exception");
        _castError = Standard("CastError");
        _loopError = Standard("LoopError");
        _ioError = Standard("IoError");
        _parseError = Standard("ParseError");
        _parseError.Fields = [.. _parseError.Fields, new MoldFieldSymbol("code", PrimitiveType.UInt, false, default), new MoldFieldSymbol("input", PrimitiveType.String, false, default)];
        // Les champs en plus de message et where : le code (5, une entrée-sortie) et le fichier concerné.
        _ioError.Fields = [.. _ioError.Fields, new MoldFieldSymbol("code", PrimitiveType.UInt, false, default), new MoldFieldSymbol("path", PrimitiveType.String, false, default)];
    }

    /// <summary>
    /// Les champs que reçoit d'office un type d'erreur : <c>message</c> (« échec » par défaut) et
    /// <c>where</c>, l'endroit du <c>fail</c>, qu'il remplit.
    /// </summary>
    private MoldFieldSymbol[] _ErrorFields(SourceSpan declaration)
    {
        var message = new MoldFieldSymbol("message", PrimitiveType.String, true, declaration);
        var where = new MoldFieldSymbol("where", PrimitiveType.String, true, declaration);
        _defaults[message] = new IrStringConstant(declaration, PrimitiveType.String, "échec");
        _defaults[where] = new IrStringConstant(declaration, PrimitiveType.String, "");
        return [message, where];
    }

    /// <summary>
    /// Indique si une valeur de ce type est une erreur, qu'un <c>fail</c> peut envoyer : un mold qui a
    /// au moins les champs d'<c>Exception</c>, <c>string message</c> et <c>string where</c>.
    /// </summary>
    private static bool _IsError(IrType type) =>
        type is MoldType { Symbol: var mold } && mold["message"]?.Type == PrimitiveType.String && mold["where"]?.Type == PrimitiveType.String;

    /// <summary>Vrai pour une erreur, ou une union dont tous les membres en sont.</summary>
    private static bool _IsErrorOrUnion(IrType type) => type is UnionType union ? union.Members.All(_IsError) : _IsError(type);

    /// <summary>
    /// Les noms de tous les molds d'abord (E075 pour un nom en double ou réservé), puis leurs champs :
    /// un mold peut en utiliser un autre déclaré plus bas.
    /// </summary>
    private void _DeclareMolds(IEnumerable<MoldDeclaration> declarations)
    {
        var all = declarations.ToArray();
        foreach (var mold in all)
        {
            if (mold.TypeParameters is not null)
            {
                _DeclareGenericMold(mold);
                continue;
            }

            var name = mold.Name.Name;
            if (!_ClaimTypeName(mold.Name, mold.IsError ? "une error" : "un mold")) continue;

            var key = _DeclKey(name);
            var symbol = new MoldSymbol(name, mold.Name.Span, key) { IsError = mold.IsError, IsSoa = mold.IsSoa, DeclaresDestructor = mold.Destructor is not null };
            _molds[key] = symbol;
            _moldDeclarations[symbol] = mold;
            _allMolds.Push(symbol);
        }

        foreach (var symbol in _moldDeclarations.Keys) _ResolveMold(symbol);
    }

    /// <summary>
    /// Les champs d'un mold déclaré, spreads dépliés : un spread résout d'abord le mold qu'il
    /// embarque (un cycle de spreads est E073), un nom en double est E071.
    /// </summary>
    private void _ResolveMold(MoldSymbol symbol)
    {
        if (_resolvedMolds.Contains(symbol)) return;
        var declaration = _moldDeclarations[symbol];
        if (!_resolvingMolds.Add(symbol))
        {
            _Error(declaration.Name.Span, DiagnosticCodes.RecursiveMold, $"le mold « {symbol.Name} » s'embarque lui-même par ses spreads", "cycle de spreads",
                "un spread recopie les champs d'un autre mold : il ne peut pas revenir au mold de départ");
            return;
        }

        symbol.Fields = _BindMoldMembers(declaration.Members, declaration.Name.Name, symbol.IsError ? _ErrorFields(declaration.Name.Span) : null);
        if (symbol.IsSoa) _CheckSoaMold(symbol, declaration);
        _resolvingMolds.Remove(symbol);
        _resolvedMolds.Add(symbol);
    }

    /// <summary>E153 : un <c>soa mold</c> n'a que des colonnes, c'est-à-dire des tableaux dynamiques <c>[T]</c>, et au moins une.</summary>
    private void _CheckSoaMold(MoldSymbol symbol, MoldDeclaration declaration)
    {
        if (symbol.Fields.Count == 0)
            _Error(declaration.Name.Span, DiagnosticCodes.SoaMold, $"le soa mold « {symbol.Name} » n'a aucune colonne", "sans colonne", "chaque champ d'un soa mold est une colonne : soa mold Particles { [float] vx; [float] vy; }");
        foreach (var field in symbol.Fields)
            if (field.Type is not ArrayType { Kind: ArrayKind.Dynamic })
                _Error(field.Declaration, DiagnosticCodes.SoaMold, $"le champ « {field.Name} » du soa mold « {symbol.Name} » est de type « {field.Type} » : une colonne est un tableau [T]", "pas une colonne",
                    "dans un soa mold, chaque champ est un tableau dynamique : [float] vx;");
    }

    /// <summary>Les champs écrits d'un mold ou d'un type anonyme ; les valeurs par défaut sont notées pour plus tard.</summary>
    /// <param name="members">Les membres écrits.</param>
    /// <param name="owner">Le nom du mold, pour les messages ; <see langword="null"/> pour un type anonyme.</param>
    /// <param name="implicitFields">Les champs reçus d'office, avant ceux écrits (<c>message</c> et <c>where</c> d'une error).</param>
    private MoldFieldSymbol[] _BindMoldMembers(IReadOnlyList<IMoldMember> members, string? owner, IEnumerable<MoldFieldSymbol>? implicitFields = null)
    {
        var fields = new List<MoldFieldSymbol>(implicitFields ?? []);
        var where = owner is null ? "dans ce type" : $"dans « {owner} »";

        void Add(MoldFieldSymbol field, SourceSpan span)
        {
            if (fields.Any(f => f.Name == field.Name))
            {
                _Error(span, DiagnosticCodes.DuplicateField, $"le champ « {field.Name} » est déjà déclaré {where}", "en double",
                    "chaque champ d'un mold a un nom unique, spreads compris");
                return;
            }
            fields.Add(field);
        }

        foreach (var member in members)
        {
            switch (member)
            {
                case MoldSpread spread:
                    if (_BindType(spread.Source) is not { } source) break;
                    if (source is not MoldType { Symbol: var embedded })
                    {
                        _Error(spread.Source.Span, DiagnosticCodes.TypeMismatch, $"« ...{source} » : un spread embarque les champs d'un mold", $"de type « {source} »");
                        break;
                    }
                    if (!embedded.IsAnonymous) _ResolveMold(embedded);
                    foreach (var field in embedded.Fields)
                    {
                        // Un error qui en étale un autre a déjà message et where : il ne reçoit que les champs écrits.
                        if (implicitFields is not null && embedded.IsError && field.Name is "message" or "where") continue;
                        var copy = field with { Declaration = spread.Span };
                        if (_defaultSources.TryGetValue(field, out var source2))
                        {
                            _defaultSources[copy] = source2;
                            _defaultNamespaces[copy] = _defaultNamespaces[field];
                            _defaultGenerics[copy] = _defaultGenerics.GetValueOrDefault(field);
                        }
                        else if (_defaults.GetValueOrDefault(field) is { } bound) _defaults[copy] = bound;   // défaut d'office (message, where)
                        Add(copy, spread.Span);
                    }
                    break;

                case MoldField field:
                    if (_BindType(field.Type) is not { } type) break;
                    // Un champ nullable sans valeur par défaut vaut null : on peut l'omettre, comme en TypeScript.
                    var optional = field.Default is null && type is NullableType;
                    var symbol = new MoldFieldSymbol(field.Name.Name, type, field.Default is not null || optional, field.Name.Span);
                    if (field.Default is not null)
                    {
                        _defaultSources[symbol] = field.Default;
                        _defaultNamespaces[symbol] = _ns;
                        _defaultGenerics[symbol] = _generic;
                    }
                    else if (optional) _defaults[symbol] = new IrConversion(field.Name.Span, type, new IrNullConstant(field.Name.Span));
                    Add(symbol, field.Name.Span);
                    break;
            }
        }

        return [.. fields];
    }

    /// <summary>
    /// Les molds rangés dans la valeur même d'un champ de ce type : un nullable, une union, un ingot,
    /// un tuple, un <c>[T;N]</c> ou un <c>[T;N..M]</c> gardent leurs valeurs en place ; un <c>[T]</c>,
    /// un <c>[T+]</c> ou un dictionnaire les gardent sur le tas.
    /// </summary>
    private static IEnumerable<MoldSymbol> _InlineMolds(IrType type) => type switch
    {
        MoldType mold => [mold.Symbol],
        NullableType nullable => _InlineMolds(nullable.Inner),
        IngotType ingot => _InlineMolds(ingot.Base),
        UnionType union => union.Members.SelectMany(_InlineMolds),
        ResultType result => [.. _InlineMolds(result.Value), .. _InlineMolds(result.Error)],
        TupleType tuple => tuple.Elements.SelectMany(_InlineMolds),
        ArrayType { Kind: ArrayKind.Fixed or ArrayKind.Bounded } array => _InlineMolds(array.Element),
        _ => [],
    };

    /// <summary>
    /// Un mold ne peut pas se contenir lui-même par ses champs, directement, par d'autres molds, par
    /// des ingots ou dans un tableau de taille fixe ou bornée (E073) : sa taille serait infinie. Un
    /// tableau <c>[T]</c> ou <c>[T+]</c> de lui-même reste permis.
    /// </summary>
    private void _CheckMoldContainment()
    {
        var reported = new HashSet<MoldSymbol>();
        foreach (var start in _moldDeclarations.Keys.ToArray())
        {
            if (reported.Contains(start) || !_containmentChecked.Add(start)) continue;

            var visiting = new Stack<(MoldSymbol Mold, string Path, MoldSymbol[] Through)>();
            var seen = new HashSet<MoldSymbol>();
            visiting.Push((start, start.Name!, [start]));
            while (visiting.Count > 0)
            {
                var (mold, path, through) = visiting.Pop();
                foreach (var field in mold.Fields)
                {
                    foreach (var inner in _InlineMolds(field.Type))
                    {
                    var next = $"{path}.{field.Name}";
                    if (inner == start)
                    {
                        _Error(_moldDeclarations[start].Name.Span, DiagnosticCodes.RecursiveMold, $"le mold « {start.Name} » se contient lui-même ({next})", "taille infinie",
                            "un mold ne peut pas contenir un champ de son propre type, même nullable : utilise un tableau ([T])");
                        reported.UnionWith(through);   // un même cycle n'est signalé qu'une fois
                        visiting.Clear();
                        break;
                    }
                    if (seen.Add(inner)) visiting.Push((inner, next, [.. through, inner]));
                    }
                }
            }
        }
    }

    /// <summary>
    /// La valeur par défaut d'un champ, liée la première fois qu'on en a besoin dans la portée du
    /// fichier (ses constantes déclarées jusque-là sont visibles) : du type du champ (E003) et connue à
    /// la compilation (E072). <see langword="null"/> après une erreur.
    /// </summary>
    private IrExpression? _DefaultOf(MoldFieldSymbol field)
    {
        if (_defaults.TryGetValue(field, out var known)) return known;
        _defaults[field] = null;   // un défaut qui se lirait lui-même s'arrête ici

        var source = _defaultSources[field];
        var (scope, gear, parameters, lambda, ns, generic) = (_scope, _currentGear, _parameters, _lambda, _ns, _generic);
        _ns = _defaultNamespaces[field];
        _generic = _defaultGenerics.GetValueOrDefault(field);
        (_scope, _currentGear, _parameters, _lambda) = (_ns.Scope, null, new(StringComparer.Ordinal), null);

        var value = _BindExpression(source);
        var converted = value is null ? null : _ConvertTo(value, field.Type, $"le champ « {field.Name} » est un « {field.Type} »");
        if (converted is not null && ConstantFolder.Fold(converted) is null)
        {
            _Error(source.Span, DiagnosticCodes.NonConstantDefault, $"la valeur par défaut de « {field.Name} » n'est pas connue à la compilation", "calculée à l'exécution",
                "une valeur par défaut est une constante : un littéral, une constante du fichier, ou un calcul sur elles");
            converted = null;
        }

        (_scope, _currentGear, _parameters, _lambda, _ns, _generic) = (scope, gear, parameters, lambda, ns, generic);
        _defaults[field] = converted;
        return converted;
    }

    /// <summary>Vérifie toutes les valeurs par défaut, même celles qu'aucune valeur n'a utilisées.</summary>
    private void _CheckMoldDefaults()
    {
        foreach (var mold in _moldDeclarations.Keys)
        {
            foreach (var field in mold.Fields)
            {
                if (field.HasDefault && _defaultSources.ContainsKey(field)) _DefaultOf(field);
            }
        }
    }

    /// <summary>Un type mold anonyme <c>{int x, int y}</c> : le même symbole pour la même forme.</summary>
    private IrType? _BindAnonymousMoldType(AstTypes.AnonymousMoldType anonymous)
    {
        var errors = _ErrorCount;
        var fields = _BindMoldMembers(anonymous.Members, null);
        return _ErrorCount == errors ? new MoldType(_AnonymousMold(fields.Select(f => (f.Name, f.Type)), anonymous.Span)) : null;
    }

    /// <summary>Le mold anonyme de ces champs, créé à la première rencontre de sa forme ; ses champs sont rangés par nom.</summary>
    private MoldSymbol _AnonymousMold(IEnumerable<(string Name, IrType Type)> fields, SourceSpan span)
    {
        var ordered = fields.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        var key = string.Join(";", ordered.Select(f => $"{f.Name}:{f.Type}"));
        if (_anonymousMolds.TryGetValue(key, out var existing)) return existing;

        var symbol = new MoldSymbol(null, span) { Fields = [.. ordered.Select(f => new MoldFieldSymbol(f.Name, f.Type, false, span))] };
        _anonymousMolds[key] = symbol;
        _allMolds.Push(symbol);
        return symbol;
    }

    #endregion

    #region Valeurs et champs

    /// <summary>
    /// Une valeur <c>{…}</c> : ses champs et ses spreads dans l'ordre (un nom écrit deux fois est E071,
    /// un champ écrit après un spread le remplace, et inversement). Sans <c>new</c>, c'est un mold
    /// anonyme, qui prendra le type attendu là où il va ; avec <c>new T</c>, il prend le type T. Chaque
    /// valeur est liée en sachant le type de son champ dans le mold visé (une lambda y trouve ses paramètres).
    /// </summary>
    private IrExpression? _BindMoldExpression(MoldExpression mold, IrType? expected)
    {
        IrType? target = null;
        GenericMold? toInfer = null;
        if (mold.Type is AstTypes.NamedType { TypeArguments: null } bare && _generic?.Types.ContainsKey(bare.Name.Name) != true
            && _TryFind(_genericMolds, bare.Name.Name, out var genericMold))
        {
            // new Pair {…} sans arguments : ceux du type attendu s'il est une instance du même mold, sinon ceux des champs.
            if (expected is MoldType { Symbol: var expectedMold } && _moldInstanceArguments.TryGetValue(expectedMold, out var origin) && origin.Generic == genericMold.Key)
                target = expected;
            else toInfer = genericMold;
        }
        else if (mold.Type is not null)
        {
            // new T {…} : la valeur prend le type T.
            target = _BindType(mold.Type);
            if (target is null) return null;
            if (target is not MoldType)
            {
                _Error(mold.Type.Span, DiagnosticCodes.TypeMismatch, $"new crée un mold, et « {target} » n'en est pas un", $"de type « {target} »",
                    "par exemple : new Vec2 {x: 1.0, y: 2.0}");
                return null;
            }
        }
        var shape = _ExpectedOf<MoldType>(target ?? expected)?.Symbol;

        var failed = false;
        var entries = new List<(string Name, IrExpression Value)>();
        var spreads = new List<IrExpression>();
        var explicitNames = new HashSet<string>(StringComparer.Ordinal);
        var written = new List<(IrExpression Value, bool IsSpread)>();

        // D'abord les valeurs, dans l'ordre d'écriture.
        foreach (var entry in mold.Entries)
        {
            switch (entry)
            {
                case MoldFieldValue field:
                    var value = _NoCopy(_BindExpected(field.Value, shape?[field.Name.Name]?.Type), field.Value.Span, $"le champ « {field.Name.Name} »");
                    if (!explicitNames.Add(field.Name.Name))
                    {
                        _Error(field.Name.Span, DiagnosticCodes.DuplicateField, $"le champ « {field.Name.Name} » est déjà donné dans cette valeur", "en double");
                        failed = true;
                    }
                    if (value is null) failed = true;
                    else if (value.Type is NullType)
                    {
                        _Error(value.Span, DiagnosticCodes.CannotInferType, $"type du champ « {field.Name.Name} » impossible à déduire de null", "de type inconnu",
                            "les nullables arrivent avec la slice 2.2");
                        failed = true;
                    }
                    else written.Add((value, false));
                    break;

                case MoldSpreadValue spread:
                    var source = _BindExpression(spread.Value);
                    if (source is not null && LifecycleRules.Has(source.Type))
                    {
                        _Error(spread.Value.Span, DiagnosticCodes.LifecycleCopy, $"« {source.Type} » a un cycle de vie : « ... » en copierait les champs", "copie interdite",
                            "écris les champs un à un, ou garde la valeur telle quelle");
                        source = null;
                    }
                    if (source is null) failed = true;
                    else if (source.Type is not MoldType)
                    {
                        _Error(source.Span, DiagnosticCodes.TypeMismatch, $"« ... » étale les champs d'un mold, pas un « {source.Type} »", $"de type « {source.Type} »");
                        failed = true;
                    }
                    else written.Add((source, true));
                    break;
            }
        }
        if (failed) return null;

        // Avec un effet quelque part, chaque valeur non constante est évaluée d'abord, dans l'ordre ;
        // sans effet, seule une valeur étalée qui n'est pas un simple nom l'est (pour ne la calculer qu'une fois).
        var anyEffects = written.Any(w => IrEffects.HasEffects(w.Value));
        var parts = new RArray<IrExpression>();
        IrExpression Hold(IrExpression value, bool isSpread)
        {
            var keep = anyEffects ? ConstantFolder.Fold(value) is null : isSpread && value is not (IrVariableReference or IrParameterReference);
            if (!keep) return value;
            parts.Push(value);
            return new IrPart(value.Span, value);
        }

        var index = 0;
        foreach (var entry in mold.Entries)
        {
            var (value, isSpread) = written[index++];
            var held = Hold(value, isSpread);
            if (isSpread)
            {
                foreach (var field in ((MoldType)value.Type).Symbol.Fields)
                    _SetEntry(entries, field.Name, new IrMemberAccess(value.Span, held, field));
            }
            else _SetEntry(entries, ((MoldFieldValue)entry).Name.Name, held);
        }

        var type = new MoldType(_AnonymousMold(entries.Select(e => (e.Name, e.Value.Type)), mold.Span));
        var fields = type.Symbol.Fields.Select(f => entries.First(e => e.Name == f.Name).Value).ToArray();
        var literal = new IrMoldValue(mold.Span, type, [.. parts], fields, explicitNames);
        if (toInfer is not null)
        {
            target = _InferMoldType(toInfer, (AstTypes.NamedType)mold.Type!, entries);
            if (target is null) return null;
        }

        return target is null ? literal : _ConvertTo(literal, target, $"new crée un « {target} »");
    }

    /// <summary>Donne sa valeur à un champ : un champ déjà donné (par un spread ou plus tôt) est remplacé.</summary>
    private static void _SetEntry(List<(string Name, IrExpression Value)> entries, string name, IrExpression value)
    {
        var at = entries.FindIndex(e => e.Name == name);
        if (at >= 0) entries[at] = (name, value);
        else entries.Add((name, value));
    }

    private static int _FieldIndex(MoldSymbol mold, MoldFieldSymbol field)
    {
        for (var i = 0; i < mold.Fields.Count; ++i)
        {
            if (mold.Fields[i] == field) return i;
        }
        throw new InvalidOperationException($"Champ « {field.Name} » absent de « {mold} ».");
    }

    /// <summary>
    /// <c>valeur.champ</c> : un champ du mold (E074 s'il n'existe pas), ou <c>message</c> et
    /// <c>where</c> d'une Exception.
    /// </summary>
    private IrExpression? _BindMemberAccess(MemberAccessExpression access)
    {
        if (!access.IsSafe && access.Member.Name == "free" && _SyntacticType(access.Target) is PointerType)
        {
            var pointer = _BindExpression(access.Target);
            return pointer is null ? null : _BindPointerFree(access.Span, pointer);
        }

        if (_NumericLimit(access) is { } limit) return limit;
        if (_ReportUnknownLimit(access)) return null;

        var ambiguous = false;
        var onField = _pathOverrides.Count > 0 && _pathOverrides.ContainsKey(access.Target);
        if (!access.IsSafe && !onField && _ExtensionConstant(access, out ambiguous) is { } extended) return extended;
        if (ambiguous) return null;
        if (access.Namespace is not null)
        {
            _Error(access.Member.Span, DiagnosticCodes.InvalidExtension, $"« {access.Member.Name} » n'est pas une constante ni un appel d'extension de namespace « {access.Namespace} »", "namespace inutile",
                "un namespace ne s'écrit que devant une constante d'extension (Type.<Ns>nom) ou un appel d'extension (v.<Ns>f())");
            return null;
        }
        if (!access.IsSafe && !onField && _DatasetNamed(access.Target) is { } dataset)
        {
            if (_DatasetEntry(access, dataset) is { } entry) return entry;
            if (access.Member.Name != "length") return _UnknownDatasetEntry(access, dataset);
        }
        if (!access.IsSafe && !onField && _EnumNamed(access.Target) is { } enumeration) return _BindEnumAccess(access, enumeration);
        if (!access.IsSafe && !onField && _QualifiedName(access) is { } qualified) return _BindQualifiedValue(access, qualified);

        var target = _BindExpression(access.Target);
        if (target is null) return null;
        // Les champs d'un ingot de mold se lisent comme ceux de sa base.
        if (!access.IsSafe) target = _Unbranded(target);
        return access.IsSafe ? _BindSafeAccess(access, target) : _FieldAccess(access, target, target.Type);
    }

    /// <summary>
    /// <c>Fichier.nom</c> : une constante ou un gear d'un fichier importé, lu comme une valeur (E041 si le fichier ne le déclare pas).
    /// </summary>
    private IrExpression? _BindQualifiedValue(MemberAccessExpression access, string qualified)
    {
        var module = _ns.Module.Imported[((NameExpression)access.Target).Name.Name];
        if (_MemberConstant(module, access.Member.Name) is { } constant)
            return constant.Symbol is { } variable ? new IrVariableReference(access.Span, variable) : null;
        if (_TryFind(_gears, qualified, out var gear)) return _BindGearReference(new Identifier(access.Span), gear);

        _Error(access.Member.Span, DiagnosticCodes.UnknownName, $"« {module.Name} » ne déclare pas de constante ni de gear « {access.Member.Name} »", "inconnu",
            "un nom qualifié désigne une constante ou un gear du fichier importé ; un type s'écrit Fichier.Type");
        return null;
    }

    /// <summary><c>valeur.champ</c> sur une valeur de type <paramref name="type"/>.</summary>
    private IrExpression? _FieldAccess(MemberAccessExpression access, IrExpression target, IrType type)
    {
        if (type is UnionType union) return _UnionFieldAccess(access, target, union);
        if (type is PromiseType) return _BindPromiseState(access, target);
        var collection = _CollectionMember(access, target, out var handled);
        if (handled) return collection;
        return _FieldOf(access, type) is { } field ? new IrMemberAccess(access.Span, target, field) : null;
    }

    /// <summary>
    /// <c>valeur.champ</c> sur une union : permis si tous ses membres sont des molds qui ont ce champ
    /// (E074 sinon) ; il vaut le champ du membre présent, du type commun ou de l'union des types.
    /// </summary>
    private IrExpression? _UnionFieldAccess(MemberAccessExpression access, IrExpression target, UnionType union)
    {
        var name = access.Member.Name;
        var fields = new List<MoldFieldSymbol>();
        foreach (var member in union.Members)
        {
            if (member is MoldType { Symbol: var mold } && mold[name] is { } field)
            {
                fields.Add(field);
                continue;
            }

            _Error(access.Member.Span, DiagnosticCodes.UnknownField,
                member is MoldType ? $"« {member} » n'a pas de champ « {name} »" : $"« {member} » n'est pas un mold : il n'a pas de champ « {name} »",
                "absent d'un membre",
                $"un champ se lit directement sur une union seulement si chacun de ses membres est un mold qui l'a ; sinon, lis-la avec match",
                $"membres de l'union : {union}");
            return null;
        }

        var types = fields.Select(f => f.Type).Distinct().ToArray();
        if (types.Length > 1 && types.Any(t => t is NullableType))
        {
            _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"« {name} » n'a pas le même type dans tous les membres de « {union} », et l'un est nullable", "types différents",
                "une union ne contient pas de nullable : lis l'union avec match");
            return null;
        }

        return new IrUnionField(access.Span, UnionType.Of(types), target, fields);
    }

    /// <summary>Le champ lu par <paramref name="access"/> dans une valeur de type <paramref name="type"/> (E074 s'il n'existe pas).</summary>
    private MoldFieldSymbol? _FieldOf(MemberAccessExpression access, IrType type)
    {
        var name = access.Member.Name;
        var mold = type switch
        {
            MoldType moldType => moldType.Symbol,
            _ => null,
        };

        if (mold is null)
        {
            _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"« .{name} » : un « {type} » n'a pas de champs", type is NullableType ? "peut être null" : "pas un mold",
                type is NullableType
                    ? $"la valeur peut être null : écris « ?.{name} » (null si elle l'est), ou vérifie-la avant avec if (… not eq null)"
                    : "« . » lit le champ d'un mold, par exemple : pos.x");
            return null;
        }

        if (mold[name] is not { } field)
        {
            _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"« {mold} » n'a pas de champ « {name} »", "champ inconnu",
                mold.Fields.Count == 0 ? "ce mold n'a aucun champ" : $"champs : {string.Join(", ", mold.Fields.Select(f => f.Name))}");
            return null;
        }

        return field;
    }

    #endregion
}
