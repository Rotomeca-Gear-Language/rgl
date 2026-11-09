using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Steps;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

using AstRail = Rgl.AST.Train.Rail;

namespace Rgl.Binding;

/// <summary>
/// Les parcours <c>||&gt;</c> et <c>|!|&gt;</c> : appliquer une étape à chaque élément d'un tableau.
/// </summary>
/// <remarks>
/// <para>
/// Plusieurs parcours enchaînés (<c>a ||&gt; f ||&gt; g</c>) forment un seul <see cref="IrEach"/> :
/// chaque élément traverse toutes les étapes avant le suivant. Une étape est un gear (il transforme
/// l'élément), un guard (il garde l'élément s'il passe : sa valeur, ou l'élément lui-même pour un
/// guard <c>void</c>) ou une lambda ; des arguments en plus suivent l'élément, comme dans un pipeline.
/// Une étape qui ne renvoie rien termine le parcours, qui ne renvoie rien non plus.
/// </para>
/// <para>
/// Un élément raté par <c>||&gt;</c> est écarté, après son rail s'il en a un (dans un train) ; le
/// parcours continue. Avec <c>|!|&gt;</c>, le premier échec abandonne tout : dans un train, il part sur
/// le rail de l'étape ou fait échouer le train ; dans un guard, il fait échouer le guard ; ailleurs,
/// il n'aurait nulle part où aller (E062). Un emit ne s'applique pas à chaque élément (E063).
/// </para>
/// </remarks>
internal sealed partial class Binder
{
    /// <summary>Une étape de parcours écrite : dans une expression, ou dans un train avec son rail.</summary>
    private readonly record struct EachPart(SourceSpan Span, bool IsStrict, EachTarget Target, AstRail? Rail);

    /// <summary>Une accumulation écrite : dans une expression, ou dans un train avec son rail.</summary>
    private readonly record struct FoldPart(SourceSpan Span, IExpression Seed, EachTarget Target, AstRail? Rail);

    /// <summary>Un parcours dans une expression, ses parcours enchaînés réunis.</summary>
    private IrExpression? _BindEach(EachExpression each)
    {
        var parts = _EachChain(each, out var value);
        var source = _BindExpression(value);
        return source is null ? null : _BindEachStages(each.Span, source, parts, inTrain: false);
    }

    /// <summary>Une accumulation dans une expression : elle se fait dans la même boucle que les parcours qui la précèdent.</summary>
    private IrExpression? _BindFold(FoldExpression fold)
    {
        var parts = _EachChain(fold.Value, out var value);
        var source = _BindExpression(value);
        return source is null ? null
            : _BindEachStages(fold.Span, source, parts, inTrain: false, new FoldPart(fold.OperatorSpan.Union(fold.Target.Span), fold.Seed, fold.Target, null));
    }

    /// <summary>Les parcours enchaînés qui finissent par <paramref name="value"/>, dans l'ordre, et le tableau parcouru.</summary>
    private static List<EachPart> _EachChain(IExpression value, out IExpression source)
    {
        var parts = new List<EachPart>();
        while (value is EachExpression inner)
        {
            parts.Insert(0, new EachPart(inner.OperatorSpan.Union(inner.Target.Span), inner.IsStrict, inner.Target, null));
            value = inner.Value;
        }
        source = value;
        return parts;
    }

    /// <summary>
    /// Les étapes d'un parcours de <paramref name="source"/>, un tableau (E040 sinon) : chacune reçoit
    /// l'élément tel que l'étape précédente l'a laissé.
    /// </summary>
    /// <param name="span">Le parcours dans le source.</param>
    /// <param name="source">Le tableau parcouru.</param>
    /// <param name="parts">Les étapes écrites, dans l'ordre.</param>
    /// <param name="inTrain">Vrai dans un train : les étapes peuvent y avoir un rail.</param>
    /// <param name="fold">L'accumulation qui suit les étapes, s'il y en a une : elle donne le résultat.</param>
    private IrEach? _BindEachStages(SourceSpan span, IrExpression source, IReadOnlyList<EachPart> parts, bool inTrain, FoldPart? fold = null)
    {
        source = _Unbranded(source);
        if (source.Type is not ArrayType array)
        {
            _Error(source.Span, DiagnosticCodes.TypeMismatch, $"« {(parts.Count == 0 ? "|+>" : "||>")} » parcourt un tableau, pas un « {source.Type} »", $"de type « {source.Type} »",
                "par exemple : [1, 2, 3] ||> double, ou [1, 2, 3] |+> 0 with (acc, n) => acc + n");
            return null;
        }
        _WarnNestedArrays(span, array.Element);

        IrType? element = array.Element;
        var stages = new List<IrEachStage>();
        var canModify = _IsMutableContent(source);
        foreach (var part in parts)
        {
            if (element is null)
            {
                _Error(part.Span, DiagnosticCodes.TypeMismatch, "l'étape précédente ne renvoie rien : il n'y a plus d'élément à parcourir", "rien à parcourir",
                    "une étape qui ne renvoie rien (un gear void) termine le parcours : place-la en dernier");
                return null;
            }

            _mutableElement = canModify && stages.Count == 0;
            var stage = _BindEachStage(part, element, inTrain);
            _mutableElement = false;
            if (stage is null) return null;
            stages.Add(stage);
            element = stage.Output;
        }

        if (fold is not { } accumulation) return new IrEach(span, element is null ? PrimitiveType.Void : new ArrayType(element), source, stages);

        if (element is null)
        {
            _Error(accumulation.Span, DiagnosticCodes.TypeMismatch, "l'étape précédente ne renvoie rien : il n'y a plus d'élément à accumuler", "rien à accumuler",
                "une étape qui ne renvoie rien (un gear void) termine le parcours : l'accumulation vient avant elle, ou à sa place");
            return null;
        }
        return _BindEachFold(accumulation, element, inTrain) is { } bound ? new IrEach(span, bound.Type, source, stages, bound) : null;
    }

    /// <summary>
    /// L'accumulation : la valeur de départ, puis l'étape appliquée à l'accumulateur et à chaque élément,
    /// qui renvoie le nouvel accumulateur. Son type est celui du premier paramètre d'un gear ou d'un guard
    /// nommé, sinon celui de la valeur de départ (E001 s'il est inconnu). Un guard en étape renvoie
    /// exactement ce type (E003) : son <c>done</c> arrête l'accumulation, son échec la fait échouer tout
    /// entière, ce qui n'a de sens que dans un train ou un guard (E062).
    /// </summary>
    private IrEachFold? _BindEachFold(FoldPart part, IrType element, bool inTrain)
    {
        var target = part.Target;
        var seed = _BindExpression(part.Seed);
        if (seed is null) return null;

        var named = target.Lambda is null && !_IsValueName(target.Name!.Name) ? target.Name : null;
        var type = named is not null && _ParameterTypeOf(named.Name, 0) is { } declared ? declared : seed.Type;
        if (type is NullType or PendingType || type == PrimitiveType.Void)
        {
            _Error(seed.Span, DiagnosticCodes.CannotInferType, $"une valeur de départ « {seed.Type} » ne donne pas le type de l'accumulateur", "type inconnu",
                "écris une valeur de départ typée, par exemple : |+> 0 with …");
            return null;
        }
        seed = _ConvertTo(seed, type, "la valeur de départ est le premier accumulateur");
        if (seed is null) return null;

        var accumulator = new IrAccumulatorValue(target.Span, type);
        var current = new IrElementValue(target.Span, element);
        IrExpression? apply;
        GearSymbol? guard = null;

        if (target.Lambda is { } lambda)
        {
            // (acc, x) => …, ou (acc, x, i) => … avec le rang de l'élément.
            IrType[] parameters = lambda.Parameters.Count == 3 ? [type, element, PrimitiveType.Int] : [type, element];
            if (_BindLambda(lambda, new FunctionType(parameters, type, false), inPlace: true, foldType: type) is not { Type: FunctionType function } bound) return null;
            if (!function.IsPure && !_AllowEffect(target.Span, DiagnosticCodes.ImpureCall, "appliquer une lambda qui n'est pas pure")) return null;
            apply = new IrInvoke(target.Span, bound, parameters.Length == 3 ? [accumulator, current, new IrIndexValue(target.Span)] : [accumulator, current]);
        }
        else if (named is null)
            apply = _BindFoldInvoke(target, accumulator, current);
        else
        {
            var arguments = target.Arguments.Select((argument, i) => _BindExpected(argument, _ParameterTypeOf(named.Name, i + 2)));
            if (_BindGearCall(target.Span, named, accumulator, [current, .. arguments], asStep: true) is not IrCall call) return null;

            switch (call.Gear.Kind)
            {
                case GearKind.Emit:
                    _Error(named.Span, DiagnosticCodes.EmitNotLast, $"« {call.Gear.Name} » est un emit : il termine un train et ne s'applique pas à chaque élément", "emit",
                        "fais-en un gear qui renvoie le nouvel accumulateur");
                    return null;

                case GearKind.Guard when !inTrain && !_InGuardBody:
                    _Error(named.Span, DiagnosticCodes.GuardOrEmitInExpression, $"« |+> » avec le guard « {call.Gear.Name} » : son échec n'aurait nulle part où aller ici", "guard",
                        "dans un gear ou un emit, accumule avec un gear ou une lambda",
                        "un guard s'accumule dans un train (l'échec part sur le rail) ou dans un guard (qui échoue avec lui)");
                    return null;

                case GearKind.Guard:
                    guard = call.Gear;
                    if (inTrain) _trainCallsGuard = true;
                    if (call.Type != type)
                    {
                        _Error(named.Span, DiagnosticCodes.AssignmentTypeMismatch, $"le guard « {call.Gear.Name} » renvoie un « {call.Type} », l'accumulateur est un « {type} »", "type différent",
                            "un guard qui accumule renvoie le nouvel accumulateur, ou fait done pour garder celui qu'il a reçu");
                        return null;
                    }
                    break;
            }
            apply = call;
        }
        if (apply is null) return null;
        if (guard is null && (apply = _ConvertTo(apply, type, "le résultat de l'étape devient le nouvel accumulateur")) is null) return null;

        // Le rail ne sert qu'après un guard qui peut échouer.
        IrPath? rail = null;
        if (part.Rail is { } written)
        {
            if (guard is { Errors: { } errors })
            {
                rail = _BindPath(written.Span, written.Steps, "le rail", errors);
                if (rail is null) return null;
            }
            else
                _diagnostics.Warning(written.Span, DiagnosticCodes.UnreachableRail, "cette accumulation ne peut pas échouer : ce rail ne sera jamais pris", "jamais pris",
                    [guard is null ? "seul un guard peut échouer : un gear ou une lambda qui rencontre un problème arrête le programme"
                        : "ce guard ne contient ni fail, ni rien qui puisse échouer (as!, cast contrôlé, division par une valeur inconnue, guard appelé)"]);
        }

        return new IrEachFold(part.Span, seed, apply, guard, rail, type);
    }

    /// <summary>
    /// Une variable lambda en étape d'accumulation : elle prend l'accumulateur et l'élément, chacun de
    /// son type (E003), et ne reçoit rien d'autre (E058).
    /// </summary>
    private IrExpression? _BindFoldInvoke(EachTarget target, IrAccumulatorValue accumulator, IrElementValue current)
    {
        var callee = _BindName(new NameExpression(target.Name!));
        if (callee is null) return null;
        if (callee.Type is not FunctionType { Parameters.Count: 2 } function || target.Arguments.Count > 0)
        {
            _Error(target.Span, DiagnosticCodes.ArgumentCount, $"« {target.Name!.Name} » n'est pas une lambda à deux paramètres (l'accumulateur, puis l'élément)", "pas une étape d'accumulation",
                "par exemple : (int, int) => int ajouter: (acc, n) => acc + n; puis notes |+> 0 with ajouter");
            return null;
        }
        if (!function.IsPure && !_AllowEffect(target.Span, DiagnosticCodes.ImpureCall, "appliquer une lambda qui n'est pas pure")) return null;

        var first = _ConvertTo(accumulator, function.Parameters[0], "l'accumulateur est le premier paramètre");
        var second = _ConvertTo(current, function.Parameters[1], "l'élément est le second paramètre");
        return first is null || second is null ? null : new IrInvoke(target.Span, callee, [first, second]);
    }

    /// <summary>
    /// Une étape appliquée à l'élément : une lambda écrite sur place, une variable lambda, ou un gear ou
    /// un guard par son nom, avec ses arguments en plus. Son rail, dans un train, reçoit les erreurs du guard.
    /// </summary>
    private IrEachStage? _BindEachStage(EachPart part, IrType element, bool inTrain)
    {
        var target = part.Target;
        var current = new IrElementValue(target.Span, element);
        IrExpression? apply;
        GearSymbol? guard = null;

        if (target.Lambda is { } lambda)
        {
            if (_BindLambda(lambda, null, element, inPlace: true) is not { Type: FunctionType function } bound) return null;
            if (!function.IsPure && !_AllowEffect(target.Span, DiagnosticCodes.ImpureCall, "appliquer une lambda qui n'est pas pure")) return null;
            apply = new IrInvoke(target.Span, bound, function.Parameters.Count == 2 ? [current, new IrIndexValue(target.Span)] : [current]);
        }
        else if (_IsValueName(target.Name!.Name))
            apply = _BindInvoke(target.Span, _BindName(new NameExpression(target.Name)), current, target.Arguments);
        else
        {
            var name = target.Name;
            var arguments = target.Arguments.Select((argument, i) => _BindExpected(argument, _ParameterTypeOf(name.Name, i + 1))).ToArray();
            if (_BindGearCall(target.Span, name, current, arguments, asStep: true) is not IrCall call) return null;

            switch (call.Gear.Kind)
            {
                case GearKind.Emit:
                    _Error(name.Span, DiagnosticCodes.EmitNotLast, $"« {call.Gear.Name} » est un emit : il termine un train et ne s'applique pas à chaque élément", "emit",
                        "fais-en un gear qui ne renvoie rien (void), par exemple : nums ||> afficher, puis termine le train par done ou un emit");
                    return null;

                case GearKind.Guard when part.IsStrict && !inTrain && !_InGuardBody:
                    _Error(name.Span, DiagnosticCodes.GuardOrEmitInExpression, $"« |!|> » avec le guard « {call.Gear.Name} » : son échec n'aurait nulle part où aller ici", "guard",
                        "dans un gear ou un emit, écris « ||> » : les éléments ratés sont écartés",
                        "« |!|> » s'écrit dans un train (l'échec part sur le rail) ou dans un guard (qui échoue avec lui)");
                    return null;

                case GearKind.Guard:
                    guard = call.Gear;
                    if (inTrain) _trainCallsGuard = true;
                    break;
            }
            apply = call;
        }
        if (apply is null) return null;

        // Le rail ne sert qu'après un guard qui peut échouer ; ses erreurs ne sont connues que dans le train.
        IrPath? rail = null;
        if (part.Rail is { } written)
        {
            if (guard is { Errors: { } errors })
            {
                rail = _BindPath(written.Span, written.Steps, "le rail", errors);
                if (rail is null) return null;
            }
            else
                _diagnostics.Warning(written.Span, DiagnosticCodes.UnreachableRail, "cette étape ne peut pas échouer : ce rail ne sera jamais pris", "jamais pris",
                    [guard is null ? "seul un guard peut échouer : un gear ou une lambda qui rencontre un problème arrête le programme"
                        : "ce guard ne contient ni fail, ni rien qui puisse échouer (as!, cast contrôlé, division par une valeur inconnue, guard appelé)"]);
        }

        // Un guard void garde l'élément tel quel ; une étape void termine le parcours, même celle qui le modifie.
        var modifies = apply switch
        {
            IrInvoke { Target: IrLambda modifier } => modifier.Parameters[0].IsMutable,
            IrCall call => call.Gear.Parameters[0].IsMutable,
            _ => false,
        };
        var output = apply.Type != PrimitiveType.Void ? apply.Type : guard is not null ? element : null;
        return new IrEachStage(part.Span, apply, part.IsStrict, guard, rail, output, modifies);
    }

    /// <summary>
    /// Indique si <paramref name="source"/> se modifie en place : une variable ou un paramètre <c>mut</c>,
    /// ou un tableau dans son contenu. Une lambda ordinaire travaille alors sur sa copie persistante
    /// de la variable ; une lambda de parcours appelée sur place travaille sur l'original.
    /// </summary>
    private bool _IsMutableContent(IrExpression source)
    {
        return IrMutation.Root(source) switch
        {
            IrVariableReference { Variable: { IsMutable: true } } => true,
            IrParameterReference { Parameter: { IsMutable: true } parameter } => _lambda is null || _lambda.Parameters.Contains(parameter),
            _ => false,
        };
    }

    /// <summary>
    /// W041 : les éléments parcourus sont des molds rangés en colonnes, et un de leurs champs (sous-molds
    /// compris) est un tableau, qui reste une colonne de tableaux au lieu d'être aplati.
    /// </summary>
    private void _WarnNestedArrays(SourceSpan span, IrType element)
    {
        var seen = new HashSet<MoldSymbol>();

        IEnumerable<string> Arrays(IrType type, string prefix)
        {
            if ((type is IngotType ingot ? ingot.Base : type) is not MoldType { Symbol: var mold } || !seen.Add(mold)) yield break;
            foreach (var field in mold.Fields)
            {
                var path = prefix + field.Name;
                var fieldType = field.Type is IngotType fieldIngot ? fieldIngot.Base : field.Type;
                if (fieldType is ArrayType) yield return path;
                else foreach (var inner in Arrays(fieldType, path + ".")) yield return inner;
            }
        }

        var arrays = Arrays(element, "").ToArray();
        if (arrays.Length == 0) return;
        _diagnostics.Warning(span, DiagnosticCodes.NestedArrayNotFlattened,
            $"les éléments parcourus sont rangés en colonnes, mais {string.Join(", ", arrays.Select(a => $"« {a} »"))} {(arrays.Length == 1 ? "est un tableau, qui n'est pas aplati" : "sont des tableaux, qui ne sont pas aplatis")}",
            "colonne non aplatie",
            ["chaque élément garde son propre tableau : pour des colonnes plates, range ces valeurs dans un tableau à part"]);
    }
}
