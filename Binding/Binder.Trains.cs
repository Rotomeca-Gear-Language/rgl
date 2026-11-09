using Rgl.AST.Train;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;
using Rotomeca.Core.Collections;
using Rotomeca.Utils.Types;

namespace Rgl.Binding;

/// <summary>
/// Les trains appelables, ceux d'un fichier <c>forge: train</c> : un train s'appelle comme un gear, et son type de retour
/// est toujours déduit. C'est le type commun de toutes ses fins — la valeur d'un emit, de <c>done</c> (celle qui est arrivée à
/// cette étape), de <c>$ v</c>, la valeur finale d'un rail — ; s'il peut échouer, c'est <c>T ! E</c> où E est l'union des
/// erreurs que ni rail ni guard ne traite : un guard sans rail, un <c>fail</c>. Un train se lie donc comme un gear dont le
/// corps est son chemin, et se comporte comme un guard quand il peut échouer.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Vrai pendant la liaison d'un train appelable : ses fins n'ont pas à être des codes de sortie.</summary>
    private bool _callable;

    /// <summary>Vrai si <paramref name="name"/> désigne un train du fichier qui n'est pas encore lié (écrit plus bas).</summary>
    private bool _IsPendingTrain(string name) => _ns.Prefixes.Any(prefix => _pendingTrains.Contains(prefix + name));

    /// <summary>
    /// Lie un train appelable : ses paramètres typés, son chemin, puis son type de retour et ses erreurs, déduits. Il devient un
    /// gear — un guard s'il peut échouer — visible des trains écrits plus bas et des fichiers qui l'importent.
    /// </summary>
    /// <param name="train">Le train écrit.</param>
    /// <param name="name">Son nom dans les messages.</param>
    /// <param name="key">Son nom unique dans le programme.</param>
    /// <param name="declaration">Où il est déclaré : son nom, ou lui-même.</param>
    /// <returns>Le gear, ou <see langword="null"/> après une erreur signalée.</returns>
    private IrGear? _BindCallableTrain(TrainDeclaration train, string name, string key, SourceSpan declaration)
    {
        _pendingTrains.Remove(key);
        if (train.NativeReturnType is { } nativeReturn)
        {
            var nativeName = declaration.Text == name ? new AST.Identifier(declaration)
                : new AST.Identifier(new SourceSpan(new SourceText(train.Span.Source.Path, name), 0, (uint)name.Length));
            var native = new AST.Gears.GearDeclaration(train.Span, Syntax.TokenKind.GuardKeyword, false,
                nativeName, train.Parameters ?? [], nativeReturn, null, null);
            var nativeSymbol = _BindGearSignature(native, key, name, null, isTrain: true);
            _gears[key] = nativeSymbol;
            return nativeSymbol is null ? null : _BindNativeGear(native, nativeSymbol);
        }
        _trainName = name;
        _trainCallsGuard = false;
        _trainCheckpoints.Clear();
        _passedCheckpoints = [];
        _arguments = null;

        RArray<ParameterSymbol> parameters = new();
        var failed = false;
        foreach (var parameter in train.Parameters ?? [])
        {
            if (parameter.IsParams)
            {
                _RejectParams(parameter, "un train");
                failed = true;
            }
            if (parameter.Default is not null)
            {
                _RejectDefault(parameter, "un train");
                failed = true;
            }
            if (parameter.IsMutable)
            {
                _Error(parameter.Span, DiagnosticCodes.InvalidMut, "un paramètre du train n'est pas mut", "mut",
                    "un train reçoit ses paramètres par valeur : retire mut, puis copie-les si besoin, par exemple : mut var liste: args;");
                failed = true;
            }
            if (_BindType(parameter.Type) is { } type) parameters.Push(new ParameterSymbol(parameter.Name.Name, type, parameter.Name.Span));
            else failed = true;
        }
        if (failed)
        {
            _gears[key] = null;
            return null;
        }

        var (scope, parameterTable) = (_scope, _parameters);
        _scope = new Scope(_ns.Scope);
        _parameters = parameters.AsReadOnly().ToDictionary(p => p.Name, StringComparer.Ordinal);
        _callable = true;
        var errorsBefore = _ErrorCount;

        var startValue = train.StartValue is null ? null : _BindExpression(train.StartValue);
        _DeclareCheckpoints(train.Pipeline.Steps);
        var main = _BindPath(train.Pipeline.Span, train.Pipeline.Steps, "le train", startValue?.Type);

        // Après une erreur dans une étape, un guard a pu ne pas être reconnu : le « ! » n'est pas vérifié.
        if (_ErrorCount == errorsBefore) _CheckBang(train);
        if (main is not null && _ErrorCount == errorsBefore) _CheckTrainMode(main);

        _callable = false;
        (_scope, _parameters) = (scope, parameterTable);
        if (main is null)
        {
            _gears[key] = null;
            return null;
        }

        if (_DeduceResult(main, name, declaration) is not { } result)
        {
            _gears[key] = null;
            return null;
        }

        var ends = new TrainEnds();
        _CollectTrainEnds(main, perElement: false, ends);
        var errors = ends.Errors.Count == 0 ? null : UnionType.Of(ends.Errors);
        if (train.IsUnguarded && errors is not null && _trainCallsGuard == false)
        {
            var bang = new SourceSpan(train.Span.Source, train.Span.Start, RUint.One);
            _Error(bang, DiagnosticCodes.BangWithGuard, "ce train peut échouer : il ne doit pas être préfixé par « ! »", "en trop",
                "le « ! » signale un train qui ne peut pas échouer : retire-le, ou retire le fail");
            _gears[key] = null;
            return null;
        }

        var symbol = new GearSymbol(name, errors is null ? GearKind.Gear : GearKind.Guard, [.. parameters.AsReadOnly()], result, _IsPureTrain(startValue, main), declaration, key)
        {
            IsTrain = true,
            Errors = errors,
        };
        _gears[key] = symbol;
        var bound = new IrGear(train.Span, symbol, new IrBlock(train.Span, [new IrTrainBody(train.Span, startValue, main)]));
        _calls[symbol] = IrWalk.AllExpressions(bound.Body).OfType<IrCall>().Select(c => c.Gear).ToHashSet();
        _boundGears[symbol] = bound;
        return bound;
    }

    /// <summary>Ce que les fins d'un chemin disent du train : leurs types, leurs valeurs <c>$</c> et les erreurs envoyées.</summary>
    private sealed class TrainEnds
    {
        /// <summary>Le type de la valeur de chaque fin qui renvoie une valeur (<c>done</c>, emit), hors <c>$</c>.</summary>
        public List<IrType> Types { get; } = [];

        /// <summary>Les fins <c>$ valeur</c> : leur valeur est ramenée au type commun des fins.</summary>
        public List<IrExit> Exits { get; } = [];

        /// <summary>Les erreurs que le train envoie : celles que ni rail ni guard ne traite, et celles de ses <c>fail</c>.</summary>
        public HashSet<IrType> Errors { get; } = [];
    }

    /// <summary>
    /// Parcourt un chemin et ses rails : le type de chaque fin qui renvoie une valeur, les fins <c>$</c> et les erreurs envoyées.
    /// Un guard sans rail envoie les siennes ; un <c>fail</c> envoie la sienne, même dans un rail.
    /// </summary>
    /// <param name="path">Le chemin.</param>
    /// <param name="perElement">Vrai pour le rail d'un élément raté par <c>||&gt;</c> : <c>done</c> et un emit <c>void</c> y passent à l'élément suivant, ils ne terminent pas le train.</param>
    /// <param name="ends">Reçoit ce qui est trouvé.</param>
    private void _CollectTrainEnds(IrPath path, bool perElement, TrainEnds ends)
    {
        foreach (var step in path.Steps)
        {
            if (step.Value is IrEach each)
            {
                foreach (var stage in each.Stages)
                {
                    if (stage.Rail is { } stageRail) _CollectTrainEnds(stageRail, !stage.IsStrict, ends);
                    else if (stage.IsStrict && stage.Guard?.Errors is { } stageErrors) ends.Errors.Add(stageErrors);
                }
                if (each.Fold is { } fold)
                {
                    if (fold.Rail is { } foldRail) _CollectTrainEnds(foldRail, perElement: false, ends);
                    else if (fold.Guard?.Errors is { } foldErrors) ends.Errors.Add(foldErrors);
                }
                continue;
            }

            if (step.Rail is { } rail) _CollectTrainEnds(rail, perElement: false, ends);
            else if (step.Errors is { } errors) ends.Errors.Add(errors);
        }

        switch (path.End)
        {
            case IrDone done when !perElement:
                ends.Types.Add(done.Value ?? PrimitiveType.Void);
                break;

            case IrExit exit:
                ends.Exits.Add(exit);
                break;

            case IrEmit emit when !perElement || emit.Call.Type != PrimitiveType.Void:
                ends.Types.Add(emit.Call.Type);
                break;

            case IrFail fail:
                ends.Errors.Add(fail.Error?.Type ?? new MoldType(_exception));
                break;

            case IrResume resume:
                if (resume.Limit is { FailsWhenReached: true }) ends.Errors.Add(new MoldType(_loopError));
                if (resume.Fallback is { } fallback) _CollectTrainEnds(fallback, perElement, ends);
                break;
        }
    }

    /// <summary>
    /// Le type de retour du train : le type commun de ses fins. Les fins qui renvoient une valeur (<c>done</c>, emit) doivent
    /// avoir le même type (E111) ; les valeurs de <c>$ v</c> sont ramenées à ce type, comme une valeur donnée à une variable
    /// de ce type, ou, sans autre fin, au type de la première. Sans valeur, le train ne renvoie rien.
    /// </summary>
    /// <returns>Le type, ou <see langword="null"/> après une erreur signalée.</returns>
    private IrType? _DeduceResult(IrPath main, string name, SourceSpan declaration)
    {
        var ends = new TrainEnds();
        _CollectTrainEnds(main, perElement: false, ends);

        var types = ends.Types.Distinct().ToArray();
        if (types.Length > 1)
        {
            _Error(declaration, DiagnosticCodes.TrainResultType, $"les fins du train « {name} » n'ont pas de type commun : {string.Join(", ", types.Select(t => $"« {t} »"))}", "types différents",
                "le type de retour d'un train est le type de toutes ses fins : valeur d'un emit, de done (la valeur arrivée à cette étape), de $ v, d'un rail",
                "fais renvoyer le même type à chaque fin, par exemple avec un cast ou un gear de conversion");
            return null;
        }

        var result = types.Length == 1 ? types[0] : ends.Exits.Count > 0 ? ends.Exits[0].Code.Type : PrimitiveType.Void;
        var failed = false;
        foreach (var exit in ends.Exits)
        {
            if (exit.Code.Type == result) continue;
            if (_ConvertTo(exit.Code, result, $"le train « {name} » renvoie un « {result} »") is { } converted) exit.Code = converted;
            else failed = true;
        }
        return failed ? null : result;
    }

    /// <summary>
    /// Vrai si le train n'a aucun effet : il n'appelle que des gears <c>pure</c> (et des trains sans effet), ne lit ni n'écrit
    /// sur la console, n'appelle aucune lambda qui n'est pas <c>pure</c>. Un gear <c>pure</c> n'appelle qu'un tel train.
    /// </summary>
    private static bool _IsPureTrain(IrExpression? startValue, IrPath main)
    {
        var roots = startValue is null ? IrWalk.PathRoots(main) : IrWalk.PathRoots(main).Prepend(startValue);
        return roots.SelectMany(IrWalk.Expressions).All(_IsPureExpression);
    }

    /// <summary>Vrai si évaluer <paramref name="expression"/>, sans ses sous-expressions, n'a pas d'effet sur le monde extérieur.</summary>
    private static bool _IsPureExpression(IrExpression expression) => expression switch
    {
        IrCall { Gear.IsPure: false } or IrRead or IrInvoke { Function.IsPure: false } => false,
        IrLambda lambda => IrWalk.Statements(lambda.Body).All(s => s is not IrWrite) && IrWalk.AllExpressions(lambda.Body).All(_IsPureExpression),
        _ => true,
    };
}
