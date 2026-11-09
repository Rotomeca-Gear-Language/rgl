using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Notes;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// L'asynchrone coopératif (slice 5.5). Un seul thread exécute du code RGL ; un gear <c>@async</c> est une coroutine, dont l'appel démarre tout de suite et
/// rend une <c>Promise&lt;T ! E&gt;</c> que <c>await</c> attend. Décisions :
/// <list type="bullet">
/// <item><description>le seul type visible est <c>Promise&lt;T ! E&gt;</c> ; un gear <c>@async</c> s'écrit avec <c>T</c>, <c>Promise&lt;T&gt;</c> ou
/// <c>Promise&lt;T ! E&gt;</c>, et <c>T</c> comme <c>Promise&lt;T&gt;</c> valent <c>Promise&lt;T ! AsyncException&gt;</c> ; <c>E</c> est <c>AsyncException</c> ou
/// un type d'erreur qui en a les champs ; son corps renvoie le résultat <c>T ! E</c> (<c>return 5;</c> y est <c>ok(5)</c>) ;</description></item>
/// <item><description><c>@fire</c> : un gear <c>void</c> dont l'appel ne rend rien et lance une coroutine détachée, que le programme attend avant de finir ;</description></item>
/// <item><description><c>await</c> n'est permis que dans le corps d'un gear <c>@async</c> (E128) ; il donne <c>T ! E</c>, ou <c>E?</c> pour une promesse sans valeur ;</description></item>
/// <item><description>une promesse jetée (appel écrit comme instruction) est E129, le résultat ignoré d'un <c>await</c> avertit (W063) ;</description></item>
/// <item><description>un gear async n'est ni <c>pure</c> (E018), ni générique, ni un guard, ni n'a de paramètre <c>mut</c> (« pas encore pris en charge ») ; il
/// ne s'appelle pas comme étape d'un train ni d'un parcours (sauf un emit qui termine le train d'entrée) ; un gear async qui n'attend jamais avertit (W053) ;</description></item>
/// <item><description><c>sleep(durée)</c> est reconnu comme <c>ok</c> et <c>err</c> le sont, sauf si un gear porte ce nom.</description></item>
/// </list>
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Le mold d'erreur <c>AsyncException</c> de la std ; <see langword="null"/> si la std n'est pas là.</summary>
    private MoldSymbol? _asyncException;

    /// <summary>Dans le corps d'un gear <c>@sync</c> (lambdas comprises) : ce qui est asynchrone y devient synchrone.</summary>
    private bool _InSyncBody => _currentGear is { Async: AsyncMode.Sync };

    /// <summary>Le dernier <c>IrBlockOn</c> fabriqué : un <c>await</c> écrit sur un appel déjà bloqué n'a rien à ajouter.</summary>
    private IrExpression? _lastBlocked;

    /// <summary>
    /// Dans un gear <c>@sync</c>, l'appel d'un gear async, un <c>sleep</c>, <c>readAsync</c>, <c>writeAsync</c> et <c>spawnAsync</c> n'ont pas de promesse : ils
    /// attendent, sur place, et donnent le résultat (celui que <c>await</c> donnerait). Une promesse déjà tenue dans une variable ou un paramètre reste une promesse.
    /// </summary>
    private IrExpression? _AutoBlock(IrExpression? bound)
    {
        if (bound is not null) _NoteAsyncUse(bound);
        if (bound is null || !_InSyncBody || bound.Type is not PromiseType promise || bound is not (IrCall or IrSleep or IrAsyncFile or IrAsyncSpawn or IrWhenAny or IrTaskCall)) return bound;
        if (_RejectInAllocatorScope(bound.Span, "l'attente d'un appel asynchrone (le gear @sync qui l'enveloppe)")) return null;
        return _lastBlocked = new IrBlockOn(bound.Span, bound, promise.Awaited);
    }

    /// <summary>Avertit (W064) quand une coroutine appelle un gear <c>@sync</c> : il fait attendre toute la boucle.</summary>
    private void _WarnSyncCall(GearSymbol gear, SourceSpan span)
    {
        if (gear.Async != AsyncMode.Sync || _currentGear is not { IsCoroutine: true }) return;
        _diagnostics.Warning(span, DiagnosticCodes.SyncInCoroutine, $"« {gear.Name} » est @sync : il bloque la boucle d'événements, les autres tâches n'avancent plus pendant qu'il attend", "bloque la boucle",
            ["appelle plutôt un gear @async avec await"]);
    }

    /// <summary>Les gears dont le corps attend, lance ou bloque sur la boucle d'événements : un <c>@task</c> ne doit en atteindre aucun.</summary>
    private readonly HashSet<GearSymbol> _asyncUsers = [];

    /// <summary>
    /// Vrai si une valeur de ce type partage un état entre ses copies, donc entre threads : une promesse, un flux de fichier, ou un type qui en contient.
    /// </summary>
    private static bool _SharesState(IrType type) => _SharesState(type, []);

    private static bool _SharesState(IrType type, HashSet<MoldSymbol> seen) => type switch
    {
        PromiseType or PrimitiveType { Kind: PrimitiveKind.FileStream } => true,
        // Une fonction qui n'est pas pure peut tout faire — créer une promesse, toucher à la boucle — sur le thread du pool : seule une fonction pure passe.
        FunctionType { IsPure: false } => true,
        ArrayType array => _SharesState(array.Element, seen),
        DictionaryType dictionary => _SharesState(dictionary.Value, seen),
        NullableType nullable => _SharesState(nullable.Inner, seen),
        TupleType tuple => tuple.Elements.Any(e => _SharesState(e, seen)),
        UnionType union => union.Members.Any(m => _SharesState(m, seen)),
        ResultType result => _SharesState(result.Value, seen) || _SharesState(result.Error, seen),
        // Une valeur à cycle de vie n'a qu'un propriétaire : elle ne passe pas d'un thread à l'autre par copie, et sa fin ne doit pas s'exécuter sur le pool.
        MoldType mold when LifecycleRules.HasMold(mold.Symbol) => true,
        MoldType mold => seen.Add(mold.Symbol) && mold.Symbol.Fields.Any(f => _SharesState(f.Type, seen)),
        _ => false,
    };

    /// <summary>Pourquoi une valeur de ce type ne passe pas à un <c>@task</c>, pour le message d'erreur.</summary>
    private static string _SharedReason(IrType type) =>
        type is FunctionType ? "une fonction qui n'est pas pure peut toucher à la boucle d'événements depuis un autre thread : écris « pure » devant son type"
        : LifecycleRules.Has(type) ? "une valeur à cycle de vie n'a qu'un propriétaire : elle ne passe pas par copie à un autre thread"
        : "il partage un état entre threads : passe-lui des données (nombres, textes, tableaux, molds, fonctions pures), pas une promesse ni un flux de fichier";

    /// <summary>Note que le gear en cours attend, lance ou bloque sur la boucle d'événements.</summary>
    private void _NoteAsyncUse(IrExpression bound)
    {
        if (_currentGear is not { } current) return;
        if (bound is IrSleep or IrAsyncFile or IrAsyncSpawn or IrWhenAny or IrAwait or IrBlockOn or IrCancel or IrTaskCall
            || bound is IrCall { Gear: { IsCoroutine: true } or { Async: AsyncMode.Sync or AsyncMode.Task } })
            _asyncUsers.Add(current);
    }

    /// <summary>Les appels <c>task f(x)</c> : le gear appelé et l'endroit, pour vérifier qu'il n'atteint rien qui touche à la boucle d'événements.</summary>
    private readonly List<(GearSymbol Callee, SourceSpan Span)> _taskCalls = [];

    /// <summary>
    /// <c>task f(x)</c> : appelle un gear ordinaire (ni async, ni <c>@task</c>, ni guard, ni emit, sans paramètre <c>mut</c>) sur le pool de threads, avec des copies de
    /// ses arguments, et rend une promesse de son résultat. Ses arguments et son résultat ne partagent pas d'état entre threads, et il n'atteint rien qui attende, lance ou
    /// bloque sur la boucle (E132).
    /// </summary>
    private IrExpression? _BindTaskCall(TaskCallExpression task)
    {
        if (_asyncException is null)
        {
            _Unsupported(task.Span, "task sans la bibliothèque standard, qui définit AsyncException");
            return null;
        }
        if (task.Call is not CallExpression)
        {
            _Error(task.Call.Span, DiagnosticCodes.TaskUnsafe, "task lance un appel de gear", "pas un appel", "par exemple : var p: task calcule(12);");
            return null;
        }
        if (_BindExpression(task.Call) is not { } bound) return null;
        if (bound is not IrCall { Gear: var gear } call || gear.IsTrain)
        {
            _Error(task.Call.Span, DiagnosticCodes.TaskUnsafe, "task lance l'appel d'un gear, pas autre chose", "pas l'appel d'un gear", "par exemple : var p: task calcule(12);");
            return null;
        }
        if (gear.Async != AsyncMode.None || gear.Kind != GearKind.Gear)
        {
            if (gear.Kind == GearKind.Gear && gear.Async != AsyncMode.Sync) _Error(task.Call.Span, DiagnosticCodes.TaskUnsafe, $"« {gear.Name} » est déjà asynchrone : appelle-le directement", "déjà async", "task se pose sur un gear ordinaire");
            else if (gear.Kind == GearKind.Gear) _Error(task.Call.Span, DiagnosticCodes.TaskUnsafe, $"« {gear.Name} » est @sync : il bloque sur la boucle, qu'un thread du pool ne doit pas toucher", "@sync", "appelle-le depuis un gear @async");
            else _Unsupported(task.Call.Span, $"task sur un {_KindName(gear)}");
            return null;
        }
        if (gear.Parameters.Any(p => p.IsMutable))
        {
            _Error(task.Call.Span, DiagnosticCodes.TaskUnsafe, $"« {gear.Name} » modifie l'argument d'un paramètre mut : il ne peut pas s'exécuter sur une copie", "paramètre mut", "task travaille sur des copies");
            return null;
        }
        foreach (var argument in call.Arguments.Where(a => a is not IrOmittedArgument && _SharesState(a.Type)))
        {
            _Error(argument.Span, DiagnosticCodes.TaskUnsafe, $"un « {argument.Type} » ne passe pas à un thread du pool", "ne se copie pas pour de bon", _SharedReason(argument.Type));
            return null;
        }
        if (_SharesState(gear.ReturnType) || !_CheckPromiseValue(task.Span, gear.ReturnType == PrimitiveType.Void ? PrimitiveType.Bool : gear.ReturnType)) return null;

        _taskCalls.Add((gear, task.Span));
        return new IrTaskCall(task.Span, call, new PromiseType(gear.ReturnType, new MoldType(_asyncException)));
    }

    /// <summary>
    /// Le premier gear qui attend, lance ou bloque sur la boucle d'événements parmi <paramref name="start"/> et ceux qu'il atteint par ses appels ; <see langword="null"/>
    /// s'il n'y en a pas.
    /// </summary>
    private GearSymbol? _FirstAsyncUse(GearSymbol start)
    {
        var visited = new HashSet<GearSymbol> { start };
        var pending = new Stack<GearSymbol>([start]);
        while (pending.Count > 0)
        {
            var gear = pending.Pop();
            if (_asyncUsers.Contains(gear)) return gear;
            foreach (var callee in _calls.GetValueOrDefault(gear) ?? [])
                if (visited.Add(callee)) pending.Push(callee);
        }
        return null;
    }

    /// <summary>
    /// Un <c>@task</c> s'exécute sur un autre thread que la boucle d'événements : il ne peut atteindre, par ses appels, aucun gear qui attend, lance, bloque ou
    /// lance une tâche — ils touchent à l'état de la boucle, qu'un seul thread manipule (E132).
    /// </summary>
    private void _CheckTaskSafety()
    {
        foreach (var task in _calls.Keys.Where(g => g.Async == AsyncMode.Task).OrderBy(g => g.Declaration.Start.Value).ToArray())
        {
            if (_FirstAsyncUse(task) is not { } gear) continue;
            _Error(task.Declaration, DiagnosticCodes.TaskUnsafe,
                gear == task ? $"« {task.Name} » est @task : son corps ne peut ni attendre, ni lancer de tâche, ni bloquer" : $"« {task.Name} » est @task : il atteint « {gear.Name} », qui attend, lance ou bloque",
                "sur le pool de threads", "un @task tourne hors de la boucle d'événements : sépare le travail pur dans un gear ordinaire, et appelle-le depuis le @task");
        }

        // task f(x) : le gear appelé tourne sur le pool de la même façon.
        foreach (var (callee, span) in _taskCalls)
        {
            if (_FirstAsyncUse(callee) is not { } gear) continue;
            _Error(span, DiagnosticCodes.TaskUnsafe, gear == callee ? $"« {callee.Name} » attend, lance ou bloque : il ne peut pas s'exécuter sur le pool de threads" : $"« {callee.Name} » atteint « {gear.Name} », qui attend, lance ou bloque : il ne peut pas s'exécuter sur le pool de threads",
                "sur le pool de threads", "task lance un gear qui n'a rien d'asynchrone : sépare le travail pur dans un gear ordinaire");
        }
    }

    /// <summary>L'enum <c>TaskState</c> de la std, ce que <c>promesse.state</c> donne ; <see langword="null"/> si la std n'est pas là.</summary>
    private EnumSymbol? _taskState;

    /// <summary>Vrai dès qu'un <c>await</c> ou un <c>sleep</c> est lié dans le gear en cours : il peut se suspendre.</summary>
    private bool _suspends;

    /// <summary>
    /// Le type attendu d'un <c>return</c> : pour un gear async, la valeur de sa promesse (le résultat <c>T ! E</c> qui la remplit s'en déduit par
    /// conversion), sinon le type que le corps renvoie.
    /// </summary>
    private static IrType _ExpectedBody(GearSymbol symbol) =>
        symbol.Async is AsyncMode.Async or AsyncMode.Task && symbol.Promise is { } promise && promise.Value != PrimitiveType.Void ? promise.Value : symbol.BodyType;

    /// <summary>Vrai dans le corps d'un gear async, là où <c>await</c> est permis (pas dans une lambda, pas dans une valeur par défaut).</summary>
    private bool _InAsyncBody => _currentGear is { IsCoroutine: true } && _lambda is null && !_bindingDefault;

    /// <summary>
    /// Ce que les annotations d'un gear en font : ordinaire, <c>@async</c> ou <c>@fire</c>. <see langword="null"/> après une erreur signalée
    /// (un gear <c>pure</c>, deux annotations, un <c>@fire</c> sur un emit) ou une forme pas encore prise en charge.
    /// </summary>
    private AsyncMode? _AsyncModeOf(GearDeclaration gear)
    {
        var notes = gear.Notes?.Annotations.OfType<OtherAnnotation>().Where(n => n.Name.Name is "async" or "fire" or "sync" or "task").ToArray() ?? [];
        if (notes.Length == 0) return AsyncMode.None;

        var first = notes[0];
        var name = first.Name.Name;
        if (notes.Any(n => n.Name.Name != name))
        {
            _Error(notes[1].Span, DiagnosticCodes.InvalidAnnotation, "@async, @fire, @sync et @task s'excluent", "deux manières d'exécuter",
                "@async rend une promesse qu'on attend, @fire lance sans rien rendre, @sync bloque jusqu'au résultat, @task s'exécute sur le pool de threads : choisis-en une");
            return null;
        }

        if (name != "sync" && _asyncException is null)
        {
            _Unsupported(first.Span, $"@{name} sans la bibliothèque standard, qui définit AsyncException");
            return null;
        }
        if (gear.IsPure)
        {
            _Error(first.Span, DiagnosticCodes.AsyncPure, $"« {gear.Name.Name} » est pure : il ne peut pas être @{name}", "async dans un gear pure",
                "attendre, lancer ou bloquer est un effet : retire pure, ou retire @" + name);
            return null;
        }
        if (name == "task" && gear.Keyword == TokenKind.EmitKeyword)
        {
            _Unsupported(first.Span, "@task sur un emit");
            return null;
        }
        if (gear.Keyword == TokenKind.GuardKeyword && name != "sync")
        {
            _Unsupported(first.Span, $"@{name} sur un guard");
            return null;
        }
        if (name == "fire" && gear.Keyword == TokenKind.EmitKeyword)
        {
            _Error(first.Span, DiagnosticCodes.InvalidAnnotation, "@fire ne se pose pas sur un emit : il termine le chemin du train", "sur un emit", "pose @async sur l'emit, ou @fire sur un gear");
            return null;
        }
        if (name != "sync" && gear.Parameters.Any(p => p.IsMutable))
        {
            _Unsupported(first.Span, $"un paramètre mut dans un gear @{name}");
            return null;
        }
        return name switch { "fire" => AsyncMode.Fire, "sync" => AsyncMode.Sync, "task" => AsyncMode.Task, _ => AsyncMode.Async };
    }

    /// <summary>
    /// La signature d'un gear <c>@async</c> ou <c>@fire</c> : la promesse que le corps remplit, ce que l'appelant voit (la promesse, <c>void</c> pour
    /// <c>@fire</c>) et ce que le corps renvoie. <see langword="null"/> après une erreur signalée.
    /// </summary>
    private (PromiseType Promise, IrType Visible, IrType Body)? _BindAsyncSignature(GearDeclaration gear, AsyncMode mode)
    {
        var error = new MoldType(_asyncException!);
        var written = gear.ReturnType;
        if (mode == AsyncMode.Fire)
        {
            if (written is not AstTypes.PrimitiveType { Keyword: TokenKind.VoidKeyword })
            {
                _Error(written.Span, DiagnosticCodes.FireReturnType, $"« {gear.Name.Name} » est @fire : il renvoie void, personne ne lira son résultat", "pas void",
                    "un gear @fire s'écrit « : void » ; pour obtenir un résultat, utilise @async et await");
                return null;
            }
            return (new PromiseType(PrimitiveType.Void, error), PrimitiveType.Void, PrimitiveType.Void);
        }

        PromiseType promise;
        if (written is AstTypes.NamedType { Name.Name: "Promise" } named)
        {
            if (_BindPromiseType(named) is not { } bound) return null;
            promise = bound;
        }
        else
        {
            IrType? value = written is AstTypes.PrimitiveType { Keyword: TokenKind.VoidKeyword } ? PrimitiveType.Void : _BindType(written);
            if (value is null || !_CheckPromiseValue(written.Span, value)) return null;
            promise = new PromiseType(value, error);
        }

        IrType body = promise.Value == PrimitiveType.Void ? PrimitiveType.Void : new ResultType(promise.Value, promise.Error);
        return (promise, promise, body);
    }

    /// <summary><c>Promise&lt;T&gt;</c> et <c>Promise&lt;T ! E&gt;</c> (<c>T</c> peut être <c>void</c>) ; <c>E</c> vaut <c>AsyncException</c> quand il n'est pas écrit.</summary>
    private PromiseType? _BindPromiseType(AstTypes.NamedType named)
    {
        if (_asyncException is null)
        {
            _Unsupported(named.Span, "Promise sans la bibliothèque standard, qui définit AsyncException");
            return null;
        }
        if (named.TypeArguments is not { Count: 1 } arguments)
        {
            _Error(named.Span, DiagnosticCodes.InvalidPromise, "Promise prend un seul argument : le type de la valeur, ou « T ! E »", "arguments",
                "par exemple : Promise<int> ou Promise<int ! AsyncException>");
            return null;
        }

        var (valueNode, errorNode) = arguments[0] is AstTypes.ResultType result ? (result.Value, result.Error) : (arguments[0], null);
        IrType? value = valueNode is AstTypes.PrimitiveType { Keyword: TokenKind.VoidKeyword } ? PrimitiveType.Void : _BindType(valueNode);
        if (value is null || !_CheckPromiseValue(valueNode.Span, value)) return null;

        IrType error = new MoldType(_asyncException);
        if (errorNode is not null)
        {
            if (_BindType(errorNode) is not { } written) return null;
            if (!_IsAsyncError(written))
            {
                _Error(errorNode.Span, DiagnosticCodes.InvalidPromise, $"l'erreur d'une promesse est AsyncException ou un type d'erreur qui en a les champs, pas « {written} »", "pas une erreur async",
                    "écris « error MonErreur { ...AsyncException; … } » pour garder ses champs, ou laisse « Promise<T> », qui vaut Promise<T ! AsyncException>");
                return null;
            }
            error = written;
        }
        return new PromiseType(value, error);
    }

    /// <summary>La valeur d'une promesse est un type de résultat : pas de nullable, comme <c>T ! E</c>.</summary>
    private bool _CheckPromiseValue(SourceSpan span, IrType value)
    {
        if (value is not NullableType) return true;
        _Error(span, DiagnosticCodes.InvalidPromise, "la valeur d'une promesse ne peut pas être nullable : elle est le côté succès d'un résultat", "nullable",
            "comme pour « T ! E », écris un type qui n'est pas nullable");
        return false;
    }

    /// <summary>Vrai si <paramref name="type"/> est <c>AsyncException</c>, ou un type d'erreur qui en a tous les champs (même nom, même type).</summary>
    private bool _IsAsyncError(IrType type) =>
        type is MoldType mold && (mold.Symbol == _asyncException || _asyncException!.Fields.All(f => mold.Symbol[f.Name] is { } other && other.Type == f.Type));

    /// <summary>
    /// <c>await promesse</c> : seulement dans le corps d'un gear <c>@async</c> (E128), sur une promesse (E130) ; il donne le résultat de la
    /// promesse. Le gear peut alors se suspendre.
    /// </summary>
    private IrExpression? _BindAwait(AwaitExpression await)
    {
        if (_RejectInAllocatorScope(await.Span, "await")) return null;
        var operand = _BindExpression(await.Operand);
        if (_InSyncBody)
        {
            // Dans un gear @sync, await bloque : l'appel déjà bloqué n'a rien à attendre de plus, une promesse tenue se bloque ici.
            if (operand is null || ReferenceEquals(operand, _lastBlocked)) return operand;
            if (operand.Type is PromiseType held) return _lastBlocked = new IrBlockOn(await.Span, operand, held.Awaited);
            _Error(await.Operand.Span, DiagnosticCodes.AwaitNotPromise, $"await attend une promesse, pas un « {operand.Type} »", "pas une promesse", "l'appel d'un gear @async rend une promesse");
            return null;
        }
        if (!_InAsyncBody)
        {
            if (_lambda is not null) _Unsupported(await.Span, "await dans une lambda");
            else
            {
                var where = _bindingDefault ? "dans une valeur par défaut" : _currentGear is null ? "hors d'un gear" : $"dans le {_KindName(_currentGear)} « {_currentGear.Name} », qui n'est pas @async";
                _Error(await.Span, DiagnosticCodes.AwaitOutsideAsync, $"await suspend le gear qui l'écrit : il n'est permis que dans un gear @async, pas {where}", "pas dans un gear async",
                    _currentGear is null || _bindingDefault ? "écris l'attente dans un gear @async, que le train appelle comme dernière étape (emit)" : "ajoute @async devant la déclaration du gear");
            }
            return null;
        }
        if (operand is null) return null;
        if (operand.Type is not PromiseType promise)
        {
            _Error(await.Operand.Span, DiagnosticCodes.AwaitNotPromise, $"await attend une promesse, pas un « {operand.Type} »", "pas une promesse",
                "l'appel d'un gear @async rend une promesse : await f(x)");
            return null;
        }
        _suspends = true;
        return new IrAwait(await.Span, operand, promise.Awaited);
    }

    /// <summary>
    /// <c>cancel promesse;</c> : demande l'annulation de la tâche (E130 si ce n'est pas une promesse). Elle s'arrête à son prochain point de suspension, ou tout
    /// de suite si elle attend ; ce qu'elle attend est annulé aussi ; celui qui l'attend reçoit <c>err(Cancelled)</c>. Permis partout, sauf dans un gear <c>pure</c> (un effet).
    /// </summary>
    private IrStatement? _BindCancel(CancelStatement cancel)
    {
        var operand = _BindExpression(cancel.Operand);
        if (operand is null) return null;
        if (operand.Type is not PromiseType)
        {
            _Error(cancel.Operand.Span, DiagnosticCodes.AwaitNotPromise, $"cancel annule une promesse, pas un « {operand.Type} »", "pas une promesse",
                "l'appel d'un gear @async rend une promesse : var p: f(x); … cancel p;");
            return null;
        }
        var statement = new IrCancel(cancel.Span, operand);
        _NoteAsyncUse(statement);
        return new IrExpressionStatement(cancel.Span, statement);
    }

    /// <summary><c>promesse.state</c> : où en est la tâche, en lecture seule. Seul membre d'une promesse.</summary>
    private IrExpression? _BindPromiseState(MemberAccessExpression access, IrExpression target)
    {
        if (access.Member.Name != "state" || _taskState is null)
        {
            _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"une promesse n'a pas de membre « {access.Member.Name} »", "inconnu",
                "une promesse n'a que state : p.state eq TaskState.Completed");
            return null;
        }
        return new IrPromiseState(access.Span, target, new EnumType(_taskState));
    }

    /// <summary>
    /// <c>readAsync(chemin)</c> : une promesse du texte du fichier ; <c>writeAsync(chemin, texte)</c> : une promesse sans valeur. Le travail s'exécute sur le pool de
    /// threads du runtime ; un échec est une <c>AsyncException</c> de genre <c>IoError</c>. Pas dans un gear <c>pure</c> (E066).
    /// </summary>
    private IrExpression? _BindAsyncFile(CallExpression call, bool isRead, bool bytes = false)
    {
        var name = !isRead ? "writeAsync" : bytes ? "readBytesAsync" : "readAsync";
        if (_asyncException is null)
        {
            _Unsupported(call.Span, $"{name} sans la bibliothèque standard, qui définit AsyncException");
            return null;
        }
        var expected = isRead ? 1 : 2;
        if (call.Arguments.Count != expected)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"{name} attend {(isRead ? "un chemin" : "un chemin et un texte ou des octets")}, pas {call.Arguments.Count} arguments", "arguments",
                isRead ? "par exemple : var t: await readAsync('a.txt');" : "par exemple : await writeAsync('a.txt', 'texte');");
            return null;
        }
        if (!_AllowInputOutput(call.Span, name)) return null;

        IrExpression? Text(int index) =>
            _BindExpected(call.Arguments[index], PrimitiveType.String) is { } value ? _ConvertTo(value, PrimitiveType.String, $"{name} attend un texte") : null;
        var path = Text(0);
        // writeAsync écrit un texte ou des octets (bytes), selon ce qu'il reçoit.
        IrExpression? text = null;
        if (!isRead)
        {
            if (_BindExpression(call.Arguments[1]) is { } written)
                text = written.Type == _bytes ? written : _ConvertTo(written, PrimitiveType.String, "writeAsync écrit un texte ou des octets (bytes)");
            if (text is null) return null;
        }
        if (path is null) return null;
        IrType value = !isRead ? PrimitiveType.Void : bytes ? _bytes : PrimitiveType.String;
        return new IrAsyncFile(call.Span, path, text, new PromiseType(value, new MoldType(_asyncException)));
    }

    /// <summary>
    /// <c>spawnAsync(exécutable, arguments…)</c> : lance un processus enfant sur le pool de threads et rend une promesse de son <c>ProcessResult</c> ; chaque élément est
    /// une chaîne, comme pour <c>spawn</c>, dont il garde les règles (pas dans un gear <c>pure</c>, pas de <c>rgl</c>) mais pas les options. Un code de sortie non nul est un
    /// succès ; l'échec du lancement est une <c>AsyncException</c> de genre <c>IoError</c>.
    /// </summary>
    private IrExpression? _BindAsyncSpawn(CallExpression call, bool withOptions = false)
    {
        if (_asyncException is null || _processResult is null)
        {
            _Unsupported(call.Span, "spawnAsync sans la bibliothèque standard, qui définit AsyncException et ProcessResult");
            return null;
        }
        var first = withOptions ? 1 : 0;
        if (call.Arguments.Count <= first)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, withOptions ? "spawnAsyncWith attend les options, puis l'exécutable" : "spawnAsync attend au moins l'exécutable", "exécutable manquant",
                withOptions ? "par exemple : await spawnAsyncWith('timeout=5000,cwd=./build', 'make', '-j4');" : "par exemple : await spawnAsync('make', '-j4');");
            return null;
        }
        if (!_AllowInputOutput(call.Span, "lancer un processus")) return null;

        var options = SpawnOptions.Default;
        if (withOptions)
        {
            if (_BindSpawnOptions(call.Arguments[0]) is not { } parsed) return null;
            options = parsed;
        }

        List<IrExpression> parts = [];
        var failed = false;
        foreach (var argument in call.Arguments.Skip(first))
        {
            var bound = _BindExpected(argument, PrimitiveType.String);
            var text = bound is null ? null : _ConvertTo(bound, PrimitiveType.String, "chaque élément de spawnAsync est une chaîne");
            if (text is null) failed = true;
            else parts.Add(text);
        }
        if (failed) return null;

        if (parts[0] is IrStringConstant { Value: var program } && _IsCompiler(program))
        {
            _Error(call.Arguments[first].Span, DiagnosticCodes.InvalidSpawn, "un programme RGL ne relance pas rgl", "le compilateur", "la décision d'exécuter le compilateur reste à l'humain");
            return null;
        }
        return new IrAsyncSpawn(call.Span, parts[0], parts.Skip(1).ToArray(), options, new PromiseType(new MoldType(_processResult), new MoldType(_asyncException)));
    }

    /// <summary>
    /// <c>whenAny(promesses)</c> : un tableau de promesses de même type donne la promesse du résultat de la première qui finit (<c>await</c> le lit comme celui de n'importe
    /// laquelle) ; les autres continuent, sans être annulées. Un tableau vide donne une erreur de genre <c>Cancelled</c>. <c>whenAll</c>, lui, est un gear de la std.
    /// </summary>
    private IrExpression? _BindWhenAny(CallExpression call)
    {
        if (call.Arguments.Count != 1)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"whenAny attend un tableau de promesses, pas {call.Arguments.Count} arguments", "un tableau", "par exemple : var premiere: await whenAny([a, b]);");
            return null;
        }
        var promises = _BindExpression(call.Arguments[0]);
        if (promises is null) return null;
        if (promises.Type is not ArrayType { Element: PromiseType element })
        {
            _Error(call.Arguments[0].Span, DiagnosticCodes.AwaitNotPromise, $"whenAny attend un tableau de promesses, pas un « {promises.Type} »", "pas un tableau de promesses",
                "par exemple : whenAny([a, b]) avec a et b des promesses du même type");
            return null;
        }
        return new IrWhenAny(call.Span, promises, element);
    }

    /// <summary><c>sleep(durée)</c> : une promesse sans valeur qui se termine une fois la durée écoulée.</summary>
    private IrExpression? _BindSleep(CallExpression call)
    {
        if (_asyncException is null)
        {
            _Unsupported(call.Span, "sleep sans la bibliothèque standard, qui définit AsyncException");
            return null;
        }
        if (call.Arguments.Count != 1)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"sleep attend une durée, pas {call.Arguments.Count} arguments", "une durée",
                "par exemple : await sleep(500ms);");
            return null;
        }

        var duration = _BindExpected(call.Arguments[0], new PrimitiveType(PrimitiveKind.Duration));
        if (duration is null || _ConvertTo(duration, new PrimitiveType(PrimitiveKind.Duration), "sleep attend une durée") is not { } converted) return null;
        return new IrSleep(call.Span, converted, new PromiseType(PrimitiveType.Void, new MoldType(_asyncException)));
    }

    /// <summary>
    /// Une promesse écrite comme instruction n'est attendue par personne (E129) ; le résultat d'un <c>await</c> écrit comme instruction est perdu :
    /// il peut être une erreur (W063), sauf celui d'un <c>sleep</c>.
    /// </summary>
    /// <returns>Faux après une erreur.</returns>
    private bool _CheckDiscarded(SourceSpan span, IrExpression bound)
    {
        if (bound.Type is PromiseType)
        {
            _Error(span, DiagnosticCodes.PromiseDiscarded, "la promesse n'est attendue par personne : elle est jetée", "promesse jetée",
                "écris « await … », ou garde-la : var p: f(x); … await p;");
            return false;
        }
        if (bound is IrAwait { Operand: not IrSleep } or IrBlockOn { Operand: not IrSleep } && _currentGear is { Async: AsyncMode.Fire })
        {
            // Un @fire est détaché : personne n'attend son résultat, donc personne ne lirait l'erreur d'un await ignoré.
            _Error(span, DiagnosticCodes.FireErrorIgnored, "l'erreur de cet await est perdue : un @fire est détaché, personne ne la lira", "erreur ignorée",
                "garde le résultat et traite-le : var r: await f(x); puis match (r) { … } ou ?? ; un @fire doit gérer ses erreurs lui-même");
            return false;
        }
        if (_safe && bound is IrAwait { Operand: not IrSleep } or IrBlockOn { Operand: not IrSleep })
        {
            _Error(span, DiagnosticCodes.IgnoredResult, "le résultat de await est ignoré : il peut être une erreur, et le mode sûr n'en cache aucune", "résultat ignoré",
                "garde-le : var r: await f(x); puis lis-le avec match ou ??");
            return false;
        }
        if (bound is IrAwait { Operand: not IrSleep } or IrBlockOn { Operand: not IrSleep })
            _diagnostics.Warning(span, DiagnosticCodes.AwaitResultIgnored, "le résultat de await est ignoré : il peut être une erreur", "résultat ignoré",
                ["garde-le : var r: await f(x); puis lis-le avec match ou ??"]);
        return true;
    }
}
