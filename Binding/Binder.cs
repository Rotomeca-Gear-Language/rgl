using Rgl.AST.Enums;
using Rgl.AST.Gears;
using Rgl.AST.Ingots;
using Rgl.AST.Molds;
using Rgl.AST.Statements;
using Rgl.AST.Interfaces;
using Rgl.AST.Variables;
using Rgl.AST.Steps;
using Rgl.AST.Train;
using Rgl.AST;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR;
using Rgl.IR.Types;
using Rgl.Text;
using Rotomeca.Core.Collections;
using Rotomeca.Utils.Types;
using AstTypes = Rgl.AST.Types;
using IrTypes = Rgl.IR.Types;

namespace Rgl.Binding;

/// <summary>
/// Analyse sémantique : transforme l'arbre syntaxique en IR typé. Résout les noms (un paramètre
/// devient un symbole), calcule le type de chaque expression, vérifie les règles du langage et
/// signale ce qui ne les respecte pas.
/// </summary>
/// <remarks>
/// <para>
/// Le binder ne reçoit qu'un arbre sans erreur de syntaxe (pas de nœud manquant). Il signale toutes
/// ses erreurs, puis ne renvoie un <see cref="IrProgram"/> que s'il n'en a trouvé aucune : l'IR
/// produit est donc toujours valide, et l'émetteur C++ n'a plus rien à vérifier.
/// </para>
/// <para>
/// Un programme est le train d'entrée, précédé ou suivi d'instructions sur des variables ; il n'y
/// a encore ni gears déclarés, ni types utilisateur. Les instructions sont liées dans l'ordre du
/// source : un nom n'est visible qu'après sa déclaration. Une construction que le compilateur ne
/// sait pas encore traiter est signalée « pas encore prise en charge », sans code : c'est une
/// limite du compilateur, pas une erreur dans le programme.
/// </para>
/// </remarks>
/// <param name="diagnostics">Reçoit les erreurs sémantiques.</param>
/// <param name="overflow">Ce que font les opérations entières qui débordent (clé <c>overflow</c> de <c>rgl.json</c>).</param>
/// <param name="recursionLimit">Profondeur maximale des gears récursifs (clé <c>recursion</c>) ; <see langword="null"/> pour aucune limite.</param>
/// <param name="safe">Vrai pour le mode sûr (clé <c>safe</c> de <c>rgl.json</c>) : les trains doivent traiter toutes leurs erreurs.</param>
/// <param name="boundedLoops">Vrai quand la clé <c>loop</c> de <c>rgl.json</c> vaut <c>"safe"</c> : toute boucle a une limite et <c>goto</c> est refusé (E142).</param>
internal sealed partial class Binder(DiagnosticBag diagnostics, OverflowMode overflow = OverflowMode.Checked, uint? recursionLimit = 200, bool safe = false, bool boundedLoops = false, Project.NativePolicy? nativePolicy = null)
{
    private readonly DiagnosticBag _diagnostics = diagnostics;
    private OverflowMode _overflow = overflow;
    private readonly uint? _recursionLimit = recursionLimit;
    private readonly bool _safe = safe;
    private readonly bool _boundedLoops = boundedLoops;
    private readonly Project.NativePolicy _nativePolicy = nativePolicy ?? Project.NativePolicy.Unrestricted;

    /// <summary>Vrai dès qu'une erreur a été signalée par ce binder.</summary>
    private bool _hasErrors;

    /// <summary>Le paramètre des arguments du train d'entrée ; <see langword="null"/> s'il ne le déclare pas.</summary>
    private ParameterSymbol? _arguments;

    /// <summary>Vrai dès qu'une étape du train appelle un guard : le train ne doit alors pas être préfixé par <c>!</c>.</summary>
    private bool _trainCallsGuard;

    /// <summary>Construit l'IR d'un programme d'un seul fichier : le fichier d'entrée.</summary>
    /// <param name="unit">Arbre du fichier, sans erreur de syntaxe.</param>
    /// <returns>Le programme, ou <see langword="null"/> si une erreur a été signalée.</returns>
    public IrProgram? Bind(CompilationUnit unit) => Bind([new SourceModule(string.Empty, unit, true)]);

    /// <summary>Construit l'IR d'un programme : ses fichiers, chacun après ceux qu'il importe, le fichier d'entrée en dernier.</summary>
    /// <param name="modules">Les fichiers, sans erreur de syntaxe, dans l'ordre où les lier.</param>
    /// <returns>Le programme, ou <see langword="null"/> si une erreur a été signalée.</returns>
    public IrProgram? Bind(IReadOnlyList<SourceModule> modules)
    {
        _wrappedCppSources.UnionWith(modules.SelectMany(m => (m.NativeSources?.Values ?? Enumerable.Empty<string>())
            .Concat(m.Native?.Headers ?? []).Concat(m.NativeBodies?.Values.SelectMany(b => b.Headers) ?? Enumerable.Empty<string>())).Select(Path.GetFullPath));
        var unit = modules[^1].Unit;
        if (unit.Train is null)
        {
            _Error(new SourceSpan(unit.Span.Source, unit.Span.Start, RUint.Zero), DiagnosticCodes.MissingEntryTrain,
                "le fichier d'entrée ne contient aucun train", null,
                "le fichier .rgl d'un projet contient le train d'entrée, par exemple : train |> done;");
            return null;
        }

        // Les types d'erreur du compilateur sont ceux de Errors.forge : tout programme le lie, en premier, avec ou sans le reste de la std.
        var errors = modules.FirstOrDefault(m => m.Name == "std.Errors") ?? Project.StdLibrary.ErrorsModule(_diagnostics);
        if (errors is not null) modules = [errors, .. modules.Where(m => m != errors)];
        else _DeclareStandardErrors();

        _CheckNamespaces(modules);
        RArray<IrStatement> statements = new();
        RArray<IrGear> gears = new();
        IrEntryTrain? entry = null;
        foreach (var module in modules) entry = _BindModule(module, statements, gears) ?? entry;
        _BindPendingInstances(gears);
        if (entry is not null) statements.Push(entry);

        _MarkRecursiveGears();
        _CheckTaskSafety();
        _PropagateUnsafe();
        if (entry is not null) entry.IsUnsafe = IrWalk.PathRoots(entry.Main).Concat(entry.StartValue is { } start ? [start] : []).SelectMany(IrWalk.Expressions).OfType<IrCall>().Any(c => c.Gear.IsUnsafe);
        if (_safe && !_hasErrors && entry is not null) _ReportCertificate();
        if (_hasErrors || entry is null) return null;

        var kept = _WithoutUnusedExtensions([.. statements], [.. gears]);
        var precasts = statements.OfType<IrVariableDeclaration>().Where(d => _precastVariables.Contains(d.Variable) && !Project.StdLibrary.Contains(d.Variable.Declaration.Source.Path)).ToList();
        return new IrProgram([.. _WithoutUnusedPrecasts([.. statements], kept)], _overflow, [.. kept], _recursionLimit, [.. _allMolds], [.. _enums.Values], _exception, _castError, _loopError, _ioError, _parseError,
            _defaults.Where(d => d.Value is not null).ToDictionary(d => d.Key, d => d.Value!), _xmlNode, _asyncException,
            [.. modules.Select(m => m.Native).OfType<Native.CLibrary>()]) { Precasts = precasts };
    }

    #region Train d'entrée

    private IrEntryTrain? _BindEntryTrain(TrainDeclaration train)
    {
        // Un train lié avant lui ne laisse rien derrière lui.
        _trainName = "train";
        _trainCallsGuard = false;
        _trainCheckpoints.Clear();
        _passedCheckpoints = [];
        _arguments = _BindSignature(train);
        _parameters = _arguments is null ? new(StringComparer.Ordinal) : new(StringComparer.Ordinal) { [_arguments.Name] = _arguments };
        var errorsBefore = _ErrorCount;

        var startValue = train.StartValue is null ? null : _BindExpression(train.StartValue);
        _DeclareCheckpoints(train.Pipeline.Steps);
        var main = _BindPath(train.Pipeline.Span, train.Pipeline.Steps, "le train", startValue?.Type);

        // Après une erreur dans une étape, un guard a pu ne pas être reconnu : le « ! » n'est pas vérifié.
        if (_ErrorCount == errorsBefore) _CheckBang(train);
        if (main is not null && _ErrorCount == errorsBefore) _CheckTrainMode(main);

        return main is null ? null : new IrEntryTrain(train.Span, _arguments, startValue, main);
    }

    /// <summary>
    /// La signature du train d'entrée est <c>([string] args): uint</c> (E015). Le train peut aussi
    /// ne déclarer aucun paramètre, s'il n'utilise pas les arguments : <c>train |&gt; done;</c>.
    /// </summary>
    private ParameterSymbol? _BindSignature(TrainDeclaration train)
    {
        if (train.Parameters is not { Count: > 0 } parameters) return null;
        foreach (var parameter in parameters.Where(p => p.IsMutable))
            _Error(parameter.Span, DiagnosticCodes.InvalidMut, "un paramètre du train n'est pas mut", "mut",
                "les arguments du programme ne viennent d'aucune variable à modifier : retire mut, puis copie-les si besoin, par exemple : mut var liste: args;");

        foreach (var parameter in parameters.Where(p => p.IsParams)) _RejectParams(parameter, "le train");
        foreach (var parameter in parameters.Where(p => p.Default is not null)) _RejectDefault(parameter, "le train");

        if (parameters is [{ Type: AstTypes.ArrayType { Kind: AstTypes.ArrayTypeKind.Dynamic, Element: AstTypes.PrimitiveType { Keyword: Syntax.TokenKind.StringKeyword } } } argument])
            return new ParameterSymbol(argument.Name.Name, new IrTypes.ArrayType(IrTypes.PrimitiveType.String), argument.Span);

        var span = parameters[0].Span.Union(parameters[^1].Span);
        _Error(span, DiagnosticCodes.EntryTrainSignature, "le train d'entrée doit avoir la signature ([string] args): uint",
            $"signature reçue : ({string.Join(", ", parameters.Select(p => p.Span.Text))})",
            "la bonne formulation est : train([string] args) …",
            "un train qui n'utilise pas les arguments peut omettre ses paramètres : train |> done;");
        return null;
    }

    /// <summary>
    /// Un train sans guard doit être préfixé par <c>!</c>, sauf <c>train |&gt; done;</c> (E037) ; un
    /// train qui appelle un guard ne l'est pas (E064).
    /// </summary>
    private void _CheckBang(TrainDeclaration train)
    {
        if (_trainCallsGuard)
        {
            if (!train.IsUnguarded) return;
            var bang = new SourceSpan(train.Span.Source, train.Span.Start, RUint.One);
            _Error(bang, DiagnosticCodes.BangWithGuard, "ce train appelle un guard : il ne doit pas être préfixé par « ! »", "en trop",
                "le « ! » signale un train sans guard : retire-le");
            return;
        }

        if (train.IsUnguarded) return;

        var isMinimal = train.Parameters is null or { Count: 0 } && train.StartValue is null
            && train.Pipeline.Steps is [DoneStep];
        if (isMinimal) return;

        var keyword = new SourceSpan(train.Span.Source, train.Span.Start, RUint.CreateChecked(Math.Min(5, train.Span.Text.Length)));
        _Error(keyword, DiagnosticCodes.MissingUnguardedBang, "un train sans guard doit être préfixé par « ! »", "ajoute « ! » devant « train »",
            "ce train n'appelle aucun guard : la bonne formulation est !train …",
            "seul « train |> done; » est dispensé du « ! »");
    }

    /// <summary>
    /// L'erreur envoyée par un <c>fail</c>, étape de train ou instruction de guard : rien ou un texte
    /// donnent une Exception (« échec » par défaut) ; sinon, la valeur doit être une erreur (E091).
    /// </summary>
    /// <param name="span">Le <c>fail</c> dans le source.</param>
    /// <param name="written">La valeur écrite, ou <see langword="null"/>.</param>
    /// <returns>L'erreur, ou <see langword="null"/> après une erreur signalée.</returns>
    private IrExpression? _BindFailValue(SourceSpan span, IExpression? written)
    {
        if (written is null) return _ExceptionValue(span, new IrStringConstant(span, PrimitiveType.String, "échec"));

        var value = _BindExpression(written);
        if (value is null) return null;
        if (_IsError(value.Type)) return value;
        if (_Coerce(value, PrimitiveType.String) is { } message) return _ExceptionValue(span, message);

        _Error(value.Span, DiagnosticCodes.NotAnError, $"un fail envoie un texte ou une erreur, pas un « {value.Type} »", $"de type « {value.Type} »",
            "une erreur est un mold qui a au moins string message et string where : déclare-la avec error Nom { … }",
            "par exemple : fail \"négatif\"; ou fail new NotFound {path: chemin};");
        return null;
    }

    /// <summary>Une Exception de ce message ; son <c>where</c> est rempli par le <c>fail</c>.</summary>
    private IrMoldValue _ExceptionValue(SourceSpan span, IrExpression message) =>
        new(span, new MoldType(_exception), [], [message, new IrStringConstant(span, PrimitiveType.String, "")], null);

    #endregion

    #region Chemins et étapes

    /// <summary>
    /// Un chemin (chemin principal ou rail) : ses étapes doivent se terminer par <c>done</c>,
    /// <c>fail</c>, <c>$ valeur</c> ou un emit (E038), et rien ne doit suivre la terminaison (E039,
    /// E063 après un emit). Chaque étape reçoit la valeur de la précédente en premier argument ;
    /// après un gear <c>void</c>, aucune.
    /// </summary>
    /// <param name="span">Le chemin dans le source.</param>
    /// <param name="steps">Ses étapes, dans l'ordre.</param>
    /// <param name="what">« le train » ou « le rail », pour les messages.</param>
    /// <param name="input">Le type de la valeur qui entre dans la première étape ; <see langword="null"/> sans valeur.</param>
    private IrPath? _BindPath(SourceSpan span, IReadOnlyList<IStep> steps, string what, IrType? input)
    {
        // Les points passés par ce chemin ne sont visibles que de lui et de ses rails.
        var passedBefore = _passedCheckpoints;
        _passedCheckpoints = new(passedBefore);
        var path = _BindSteps(span, steps, what, input);
        _passedCheckpoints = passedBefore;
        return path;
    }

    /// <summary>Les étapes d'un chemin et sa terminaison (voir <see cref="_BindPath"/>).</summary>
    private IrPath? _BindSteps(SourceSpan span, IReadOnlyList<IStep> steps, string what, IrType? input)
    {
        IrTerminator? end = null;
        var hadError = false;
        var current = input;
        RArray<IrStep> calls = new();

        for (var index = 0; index < steps.Count; ++index)
        {
            // Après une étape en erreur, la valeur qui entre dans la suivante est inconnue : la vérifier
            // ne donnerait que des erreurs en cascade.
            if (hadError) break;

            var step = steps[index];
            if (end is IrEmit emit)
            {
                _Error(step.Span, DiagnosticCodes.EmitNotLast, $"étape après l'emit « {emit.Call.Gear.Name} »", "jamais exécutée",
                    "un emit est toujours la dernière étape d'un train ou d'un rail : supprime ce qui le suit, ou fais-en un gear");
                return null;
            }
            if (end is not null)
            {
                _Error(step.Span, DiagnosticCodes.StepAfterTerminator, $"étape après la fin de {what}", "jamais exécutée",
                    "« done », « fail », « $ valeur » et une reprise « ^N » ou « ^N<!M> » terminent le chemin : supprime ce qui les suit");
                return null;
            }

            switch (step)
            {
                case DoneStep done:
                    end = new IrDone(done.Span, current);
                    break;

                case FailStep fail:
                    // Un « fail » seul après une valeur qui est une erreur (celle qu'un rail reçoit, ou qu'un gear renvoie) envoie cette erreur.
                    var error = fail.Value is null && current is not null && _IsErrorOrUnion(current)
                        ? new IrPipelineValue(fail.Span, current)
                        : _BindFailValue(fail.Span, fail.Value);
                    if (error is null) hadError = true;
                    else end = new IrFail(fail.Span, error);
                    break;

                case ExitCodeStep exit:
                    // Un train appelé renvoie la valeur de $ ; le train d'entrée, un code de sortie.
                    var code = _callable ? _BindExpression(exit.Code) : _BindExitCode(exit.Code);
                    if (code is null) hadError = true;
                    else end = new IrExit(exit.Span, code);
                    break;

                case CallStep call:
                    var arguments = call.Arguments.Select(_BindExpression).ToArray();
                    var first = current is null ? null : new IrPipelineValue(call.Span, current);
                    if (_BindGearCall(call.Span, call.Gear, first, arguments, asStep: true) is not IrCall result)
                    {
                        hadError = true;
                        break;
                    }

                    // Un point de reprise ou une boucle range la valeur d'entrée avant l'appel.
                    IrStepEntry? entry = null;
                    if (call.Checkpoint is not null || call.Repeat is not null)
                    {
                        if (_BindStepEntry(call, result) is not { } stored)
                        {
                            hadError = true;
                            break;
                        }
                        (result, entry) = stored;
                    }

                    // Un rail ne sert qu'après un guard qui peut échouer.
                    var bound = new IrStep(result, null, entry);
                    if (call.Rail is { } written)
                    {
                        if (result.Gear.Kind == GearKind.Guard && bound.Errors is { } errors)
                        {
                            bound = bound with { Rail = _BindPath(written.Span, written.Steps, "le rail", errors) };
                            if (bound.Rail is null) hadError = true;
                        }
                        else
                            _diagnostics.Warning(written.Span, DiagnosticCodes.UnreachableRail, $"« {result.Gear.Name} » ne peut pas échouer : ce rail ne sera jamais pris", "jamais pris",
                                [result.Gear.Kind == GearKind.Guard
                                    ? "ce guard ne contient ni fail, ni rien qui puisse échouer (as!, cast contrôlé, division par une valeur inconnue, guard appelé)"
                                    : $"seul un guard peut échouer : un {_KindName(result.Gear)} qui rencontre un problème arrête le programme"]);
                    }

                    // Un gear async ne s'appelle pas comme étape : il faut un await, donc un gear async ; seul l'emit async termine le train d'entrée.
                    if ((result.Gear.IsCoroutine && (result.Gear.Kind != GearKind.Emit || _callable)) || result.Gear.Async == AsyncMode.Task)
                    {
                        _Unsupported(call.Span, result.Gear.Kind == GearKind.Emit ? "un emit async qui termine un train appelable" : $"« {result.Gear.Name} », un gear async, comme étape de train ; appelle-le avec await depuis un gear @async");
                        hadError = true;
                        break;
                    }

                    // Le code de sortie d'un emit async est la valeur de sa promesse.
                    var endType = result.Gear.Async == AsyncMode.Async ? result.Gear.Promise!.Value : result.Type;
                    switch (result.Gear.Kind)
                    {
                        case GearKind.Emit when !_callable && endType != PrimitiveType.Void && endType is not PrimitiveType { Kind: PrimitiveKind.UInt } && endType is not IngotType { Underlying: PrimitiveType { Kind: PrimitiveKind.UInt } }:
                            _Error(call.Span, DiagnosticCodes.EmitReturnType, $"l'emit « {result.Gear.Name} » renvoie un « {endType} » : il ne peut pas terminer le train d'entrée", $"de type « {endType} »",
                                "le train d'entrée renvoie le code de sortie : son emit est void (code 0) ou renvoie un uint (borné ou non, ou un ingot de uint comme programValue)");
                            hadError = true;
                            break;

                        case GearKind.Emit:
                            end = new IrEmit(call.Span, result);
                            break;

                        default:
                            if (result.Gear.Kind == GearKind.Guard) _trainCallsGuard = true;
                            calls.Push(bound);
                            current = result.Type == PrimitiveType.Void ? null : result.Type;
                            break;
                    }
                    break;

                case LambdaStep lambda:
                    if (current is null)
                    {
                        _Error(lambda.Span, DiagnosticCodes.TypeMismatch,
                            "une lambda de pipeline reçoit la valeur de l'étape précédente, mais il n'y en a pas",
                            "aucune valeur", "ajoute une valeur de départ ou une étape qui renvoie une valeur");
                        hadError = true;
                        break;
                    }
                    var lambdaInput = new IrPipelineValue(lambda.Span, current);
                    if (_BindPipelineLambda(lambda.Span, lambda.Lambda, lambdaInput) is not { } invocation)
                    {
                        hadError = true;
                        break;
                    }
                    calls.Push(new IrStep(invocation, null));
                    current = invocation.Type == PrimitiveType.Void ? null : invocation.Type;
                    break;

                case ResumeStep resume:
                    // Une reprise <M> continue par la suite du chemin : elle la lie elle-même.
                    var bounded = resume.Limit is { FailsWhenReached: false };
                    end = _BindResume(resume, current, bounded ? [.. steps.Skip(index + 1)] : [], span, what);
                    if (end is null) hadError = true;
                    if (bounded) index = steps.Count;
                    break;

                case EachStep or FoldStep:
                    // Les parcours qui se suivent, et l'accumulation qui les termine, forment une seule boucle.
                    var parts = new List<EachPart>();
                    while (index < steps.Count && steps[index] is EachStep each)
                    {
                        parts.Add(new EachPart(each.Span, each.IsStrict, each.Target, each.Rail));
                        ++index;
                    }
                    FoldPart? accumulation = null;
                    if (index < steps.Count && steps[index] is FoldStep fold)
                    {
                        accumulation = new FoldPart(fold.Span, fold.Seed, fold.Target, fold.Rail);
                        ++index;
                    }
                    --index;

                    var firstSpan = parts.Count > 0 ? parts[0].Span : accumulation!.Value.Span;
                    var eachSpan = firstSpan.Union(accumulation?.Span ?? parts[^1].Span);
                    if (current is null)
                    {
                        _Error(firstSpan, DiagnosticCodes.TypeMismatch, $"« {(parts.Count > 0 ? "||>" : "|+>")} » parcourt la valeur de l'étape précédente, et il n'y en a pas", "rien à parcourir",
                            "un parcours suit une étape qui renvoie un tableau, ou la valeur de départ du train : train [1, 2, 3] ||> double |> …");
                        hadError = true;
                        break;
                    }
                    if (_BindEachStages(eachSpan, new IrPipelineValue(eachSpan, current), parts, inTrain: true, accumulation) is not { } traversal)
                    {
                        hadError = true;
                        break;
                    }
                    calls.Push(new IrStep(traversal, null));
                    current = traversal.Type == PrimitiveType.Void ? null : traversal.Type;
                    break;

                default:
                    throw new InvalidOperationException($"Étape inattendue dans un arbre sans erreur de syntaxe : {step.GetType().Name}.");
            }
        }

        if (end is null && !hadError)
        {
            var after = new SourceSpan(span.Source, span.End, RUint.Zero);
            _Error(after, DiagnosticCodes.MissingTerminator, $"{what} ne se termine ni par done, ni par fail, ni par $ valeur, ni par un emit", "terminaison attendue ici",
                "ajoute « |> done » pour terminer avec succès, « |> fail » pour échouer, ou « |> $ code » pour choisir le code de sortie");
        }

        return end is null || hadError ? null : new IrPath(span, [.. calls], end);
    }

    #endregion

    #region Diagnostics

    private void _Error(SourceSpan span, string code, string message, string? label, params string[] help)
    {
        _hasErrors = true;
        _diagnostics.Error(span, code, message, label, help.Length == 0 ? null : help);
    }

    /// <summary>Construction que le compilateur ne sait pas encore traiter : une limite de l'outil, pas une erreur du programme, d'où l'absence de code.</summary>
    private void _Unsupported(SourceSpan span, string what)
    {
        _hasErrors = true;
        _diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, $"{what} : pas encore pris en charge par le compilateur", Span: span, Label: "pas encore pris en charge"));
    }

    #endregion
}
