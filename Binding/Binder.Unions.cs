using Rgl.AST.Statements;
using Rgl.AST.Expressions;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Les unions <c>T | U</c>, les résultats <c>T ! E</c> (comme le <c>Result</c> de Rust) et <c>match</c>.
/// Une valeur entre dans une union si elle a le type d'un membre, ou si un seul membre l'accepte ;
/// elle n'en sort que par <c>match</c>. Un résultat se construit par <c>ok(v)</c> et <c>err(e)</c>,
/// ou tout seul quand T et E diffèrent.
/// </summary>
internal sealed partial class Binder
{
    #region Types

    /// <summary><c>T | U</c> : ni <c>null</c>, ni nullable, ni <c>void</c> parmi les membres (E078).</summary>
    private IrType? _BindUnionType(AstTypes.UnionType union)
    {
        var members = new List<IrType>();
        var failed = false;
        foreach (var member in union.Members)
        {
            if (member is AstTypes.PrimitiveType { Keyword: Syntax.TokenKind.VoidKeyword } || member is AstTypes.NullableType)
            {
                _Error(member.Span, DiagnosticCodes.InvalidUnion, member is AstTypes.NullableType
                        ? "une union ne contient pas de nullable" : "void ne peut pas être membre d'une union", "membre invalide",
                    "pour une union qui peut être null, écris (int | string)?");
                failed = true;
                continue;
            }
            if (_BindType(member) is { } type) members.Add(type);
            else failed = true;
        }
        return failed ? null : UnionType.Of(members);
    }

    /// <summary><c>T ! E</c> : ni <c>void</c> ni nullable d'un côté ou de l'autre (E078).</summary>
    private IrType? _BindResultType(AstTypes.ResultType result)
    {
        foreach (var side in new[] { result.Value, result.Error })
        {
            if (side is AstTypes.PrimitiveType { Keyword: Syntax.TokenKind.VoidKeyword })
            {
                _Error(side.Span, DiagnosticCodes.InvalidUnion, "un résultat porte une valeur de chaque côté : pas void", "void");
                return null;
            }
        }
        var value = _BindType(result.Value);
        var error = _BindType(result.Error);
        return value is null || error is null ? null : new ResultType(value, error);
    }

    /// <summary>Indique si un type contient un côté de résultat encore inconnu (<c>ok(v)</c> sans type attendu).</summary>
    private static bool _IsPending(IrType type) => type switch
    {
        ResultType result => result.IsPending,
        UnionType union => union.Members.Any(_IsPending),
        NullableType nullable => _IsPending(nullable.Inner),
        _ => type is PendingType,
    };

    #endregion

    #region Conversions

    #endregion

    #region ok, err et match

    /// <summary><c>ok(v)</c> ou <c>err(e)</c> (sans gear de ce nom) : un résultat, dont l'autre côté viendra du type attendu.</summary>
    private IrExpression? _BindResultConstructor(CallExpression call, bool isOk)
    {
        var name = isOk ? "ok" : "err";
        if (call.Arguments.Count != 1)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"« {name} » attend une valeur, pas {call.Arguments.Count}", $"{call.Arguments.Count} argument(s)",
                $"par exemple : {name}(42)");
            return null;
        }

        var value = _BindExpression(call.Arguments[0]);
        if (value is null) return null;
        var type = isOk ? new ResultType(value.Type, PendingType.Instance) : new ResultType(PendingType.Instance, value.Type);
        return new IrResultValue(call.Span, type, isOk, value);
    }

    /// <summary>
    /// <c>match</c> d'une union ou d'un résultat (une autre valeur se lit par ses valeurs, <see cref="_BindValueMatch"/>) : chaque bras correspond à un
    /// membre, à <c>ok</c> ou à <c>err</c> (E080), une seule fois (E081) ; sans <c>default</c>, tous
    /// les cas sont couverts (E082). Les bras valeur prennent un type commun ; un bras bloc fait du
    /// match une instruction.
    /// </summary>
    /// <param name="expected">Le type attendu par le contexte : chaque bras valeur y est converti, au lieu de chercher leur type commun.</param>
    private IrExpression? _BindMatch(MatchExpression match, IrType? expected = null)
    {
        var subject = _BindExpression(match.Subject);
        if (subject is null) return null;

        if (_IsPending(subject.Type))
        {
            _Error(subject.Span, DiagnosticCodes.CannotInferType, $"le type de « {subject.Span.Text} » n'est pas entièrement connu", "type incomplet",
                "range d'abord la valeur dans une variable déclarée, par exemple : int ! string r: ok(1);");
            return null;
        }
        // Une autre valeur se lit comme avec un switch : par ses valeurs (case) ou par une condition (case eval).
        if (subject.Type is not (UnionType or ResultType)) return _BindValueMatch(match, subject, expected);

        var arms = new List<IrMatchArm>();
        var covered = new HashSet<int>();     // membres de l'union, ou 0 = ok et 1 = err
        var hasDefault = false;
        var failed = false;
        MatchArm? namedDefault = null;

        foreach (var arm in match.Arms)
        {
            IrMatchKind kind;
            var index = -1;
            IrType? caught;

            switch (arm.Kind)
            {
                case MatchPatternKind.Default:
                    if (hasDefault) failed |= _DuplicateArm(arm, "default");
                    hasDefault = true;
                    // « default x » reçoit ce que les autres bras ne prennent pas : il est lié après eux.
                    if (arm.Name is not null)
                    {
                        namedDefault ??= arm;
                        continue;
                    }
                    kind = IrMatchKind.Default;
                    caught = null;
                    break;

                case MatchPatternKind.Ok or MatchPatternKind.Err when subject.Type is ResultType result:
                    kind = arm.Kind == MatchPatternKind.Ok ? IrMatchKind.Ok : IrMatchKind.Err;
                    index = kind == IrMatchKind.Ok ? 0 : 1;
                    caught = kind == IrMatchKind.Ok ? result.Value : result.Error;
                    if (!covered.Add(index)) failed |= _DuplicateArm(arm, kind == IrMatchKind.Ok ? "ok" : "err");
                    break;

                case MatchPatternKind.Ok or MatchPatternKind.Err:
                    _Error(arm.Span, DiagnosticCodes.MatchArmMismatch, "ok et err lisent un résultat (T ! E), pas une union", "pas un résultat",
                        $"les bras d'une union nomment ses types : {subject.Type}");
                    failed = true;
                    continue;

                case MatchPatternKind.Type when subject.Type is UnionType union:
                    var type = _BindType(arm.Type!);
                    if (type is null) { failed = true; continue; }
                    index = union.IndexOf(type);
                    if (index < 0)
                    {
                        _Error(arm.Type!.Span, DiagnosticCodes.MatchArmMismatch, $"« {type} » n'est pas un type de « {union} »", "absent de l'union",
                            $"types de l'union : {string.Join(", ", union.Members)}");
                        failed = true;
                        continue;
                    }
                    if (!covered.Add(index)) failed |= _DuplicateArm(arm, type.ToString());
                    kind = IrMatchKind.Member;
                    caught = type;
                    break;

                case MatchPatternKind.Case or MatchPatternKind.Eval:
                    _Error(arm.Span, DiagnosticCodes.MatchArmMismatch, $"« case » compare des valeurs : un « {subject.Type} » se lit par ses types", "pas un bras de valeur",
                        subject.Type is UnionType ? $"les bras d'une union nomment ses types : {subject.Type}" : "un résultat se lit avec ok et err");
                    failed = true;
                    continue;

                default:
                    _Error(arm.Type!.Span, DiagnosticCodes.MatchArmMismatch, "un résultat se lit avec ok et err", "type",
                        "par exemple : ok v |> … ; err e |> …");
                    failed = true;
                    continue;
            }

            // Le bras, dans sa propre portée : le nom reçoit la valeur attrapée, et ne se modifie pas.
            _scope = new Scope(_scope);
            VariableSymbol? binding = null;
            if (arm.Name is { } name && caught is not null)
            {
                binding = new VariableSymbol(name.Name, caught, true, name.Span, null);
                _Declare(name, binding);
            }
            var value = arm.Value is null ? null : _BindExpression(arm.Value);
            var body = arm.Body is null ? null : _BindBlock(arm.Body);
            _scope = _scope.Parent!;

            if (arm.Value is not null && value is null) failed = true;
            else arms.Add(new IrMatchArm(arm.Span, kind, index, binding, value, body));
        }

        if (namedDefault is not null && !failed && _BindNamedDefault(namedDefault, subject, covered) is { } defaultArm) arms.Add(defaultArm);
        else if (namedDefault is not null) failed = true;

        if (!hasDefault && !failed)
        {
            var missing = subject.Type switch
            {
                UnionType union => union.Members.Where((_, i) => !covered.Contains(i)).Select(m => m.ToString()).ToArray(),
                _ => new[] { "ok", "err" }.Where((_, i) => !covered.Contains(i)).ToArray(),
            };
            if (missing.Length > 0)
            {
                _Error(new SourceSpan(match.Span.Source, match.Span.Start, Rotomeca.Utils.Types.RUint.CreateChecked(5)), DiagnosticCodes.NonExhaustiveMatch,
                    $"ce match ne couvre pas : {string.Join(", ", missing)}", "cas manquants",
                    "ajoute un bras pour chacun, ou un bras « default |> … »");
                failed = true;
            }
        }
        if (failed) return null;
        return _TypeMatch(match, subject, arms, expected, 0);
    }

    /// <summary>
    /// <c>default x |&gt; …</c> : <c>x</c> reçoit ce que les autres bras ne prennent pas, l'union des membres
    /// restants ou leur type seul s'il n'en reste qu'un. Seule une union se lit ainsi.
    /// </summary>
    private IrMatchArm? _BindNamedDefault(MatchArm arm, IrExpression subject, HashSet<int> covered)
    {
        if (subject.Type is not UnionType union)
        {
            _Error(arm.Span, DiagnosticCodes.MatchArmMismatch, "default avec un nom lit le reste d'une union", "pas une union",
                "un résultat se lit avec ok et err ; écris « default |> … » sans nom");
            return null;
        }

        var remaining = Enumerable.Range(0, union.Members.Count).Where(i => !covered.Contains(i)).ToArray();
        if (remaining.Length == 0)
        {
            _Error(arm.Span, DiagnosticCodes.MatchArmMismatch, $"les autres bras couvrent déjà tous les types de « {union} » : « default {arm.Name!.Name} » ne reçoit rien", "rien ne reste",
                "retire ce bras");
            return null;
        }

        _scope = new Scope(_scope);
        var caught = UnionType.Of(remaining.Select(i => union.Members[i]).ToArray());
        var binding = new VariableSymbol(arm.Name!.Name, caught, true, arm.Name.Span, null);
        _Declare(arm.Name, binding);
        var value = arm.Value is null ? null : _BindExpression(arm.Value);
        var body = arm.Body is null ? null : _BindBlock(arm.Body);
        _scope = _scope.Parent!;

        if (arm.Value is not null && value is null) return null;
        return new IrMatchArm(arm.Span, IrMatchKind.Default, remaining.Length == 1 ? remaining[0] : -1, binding, value, body);
    }

    /// <summary>
    /// <c>match (n) { case 1, 2 |&gt; …; case eval $ &gt; 10 |&gt; …; default |&gt; …; }</c> : un switch en ligne qui donne une valeur. Mêmes
    /// valeurs de <c>case</c> que le switch (constantes, uniques, une variante nue sur un enum) et même <c>case eval</c> ; chaque bras
    /// est indépendant (ni fallthrough ni <c>goto</c>) et, sans <c>default</c>, tous les cas sont couverts (E082) : toutes les
    /// variantes d'un enum, <c>true</c> et <c>false</c> ; pour le reste, un <c>default</c> est obligatoire.
    /// </summary>
    private IrExpression? _BindValueMatch(MatchExpression match, IrExpression subject, IrType? expected)
    {
        var context = new SwitchContext(++_switchCount, subject.Type);
        var arms = new List<IrMatchArm>();
        var hasDefault = false;
        var failed = false;

        for (var i = 0; i < match.Arms.Count; ++i)
        {
            var arm = match.Arms[i];
            IrExpression? test = null;
            var kind = IrMatchKind.Case;

            switch (arm.Kind)
            {
                case MatchPatternKind.Default:
                    if (hasDefault) failed |= _DuplicateArm(arm, "default");
                    hasDefault = true;
                    kind = IrMatchKind.Default;
                    if (arm.Name is not null)
                    {
                        _Error(arm.Span, DiagnosticCodes.MatchArmMismatch, "default avec un nom lit le reste d'une union", "pas une union",
                            "une valeur se lit par ses cas : écris « default |> … » sans nom");
                        failed = true;
                    }
                    break;

                case MatchPatternKind.Case:
                    test = _BindCaseValues(new SwitchSection(arm.Span, arm.Span, SwitchLabelKind.Values, arm.Values!, null, []), context, i);
                    break;

                case MatchPatternKind.Eval:
                    context.HasEval = true;
                    test = _BindEvalCondition(new SwitchSection(arm.Span, arm.Span, SwitchLabelKind.Eval, [], arm.Condition, []), context);
                    break;

                default:
                    _Error(arm.Span, DiagnosticCodes.MatchArmMismatch, $"un « {subject.Type} » se lit par ses valeurs : ce bras nomme un type, ok ou err", "pas un bras de valeur",
                        "par exemple : case 1, 2 |> … ; case eval $ > 10 |> … ; default |> …");
                    failed = true;
                    continue;
            }
            if (kind == IrMatchKind.Case && test is null)
            {
                failed = true;
                continue;
            }

            _scope = new Scope(_scope);
            var value = arm.Value is null ? null : _BindExpression(arm.Value);
            var body = arm.Body is null ? null : _BindBlock(arm.Body);
            _scope = _scope.Parent!;

            if (arm.Value is not null && value is null) failed = true;
            else arms.Add(new IrMatchArm(arm.Span, kind, i, null, value, body, test));
        }

        if (!hasDefault && !failed && _UncoveredValues(subject.Type, context.Cases.Keys) is { Length: > 0 } missing)
        {
            _Error(new SourceSpan(match.Span.Source, match.Span.Start, Rotomeca.Utils.Types.RUint.CreateChecked(5)), DiagnosticCodes.NonExhaustiveMatch,
                $"ce match ne couvre pas : {string.Join(", ", missing)}", "cas manquants",
                "ajoute un bras pour chacun, ou un bras « default |> … »");
            failed = true;
        }
        return failed ? null : _TypeMatch(match, subject, arms, expected, context.Id);
    }

    /// <summary>Ce que les <c>case</c> d'un match de valeurs ne couvrent pas : des variantes d'enum, <c>true</c> ou <c>false</c> ; pour tout autre type, « les autres valeurs ».</summary>
    private static string[] _UncoveredValues(IrType subject, IEnumerable<object> covered)
    {
        var keys = covered.ToArray();
        switch (subject)
        {
            case EnumType { Symbol: var symbol }:
                var seen = keys.OfType<ConstantFolder.EnumValue>().Select(v => v.Index).ToHashSet();
                return [.. symbol.Variants.Where(v => !seen.Contains(v.Index)).Select(v => v.Name)];
            case PrimitiveType { Kind: PrimitiveKind.Bool }:
                return [.. new[] { true, false }.Where(b => !keys.Contains(b)).Select(b => b ? "true" : "false")];
            default:
                return ["les autres valeurs"];
        }
    }

    /// <summary>
    /// Le type d'un match dont les bras sont liés : celui, commun, de ses bras valeur ; <c>void</c> avec un bras bloc ou des bras void.
    /// </summary>
    /// <param name="switchId">Le numéro de la valeur lue par les tests d'un match de valeurs ; 0 pour un match de types.</param>
    private IrExpression? _TypeMatch(MatchExpression match, IrExpression subject, List<IrMatchArm> arms, IrType? expected, int switchId)
    {
        // Le type du match : celui, commun, de ses bras valeur ; void avec un bras bloc ou des bras void.
        if (arms.Any(a => a.Body is not null) || arms.All(a => a.Value!.Type == PrimitiveType.Void))
            return new IrMatch(match.Span, PrimitiveType.Void, subject, arms, switchId);

        // Un type attendu (déclaration typée, return, affectation) : chaque bras y va directement.
        IrType matchType;
        string why;
        if (expected is not null)
            (matchType, why) = (expected, $"ce match doit donner un « {expected} »");
        else
        {
            var common = arms[0].Value!;
            foreach (var arm in arms.Skip(1))
            {
                if (_Unify(common, arm.Value!) is not { } unified)
                {
                    _Error(arm.Value!.Span, DiagnosticCodes.TypeMismatch, $"les bras du match n'ont pas de type commun : « {common.Type} » et « {arm.Value!.Type} »",
                        $"de type « {arm.Value!.Type} »", "chaque bras donne une valeur du même type, ou un type commun sans perte (deux molds différents ne perdent jamais de champ en silence)",
                        "donne au match un type attendu (déclaration typée, return) ou à chaque bras un type précis (new Nom {…})");
                    return null;
                }
                common = unified.Left;
            }
            (matchType, why) = (common.Type, $"les bras de ce match donnent un « {common.Type} »");
        }

        var typed = new List<IrMatchArm>();
        foreach (var arm in arms)
        {
            if (_ConvertTo(arm.Value!, matchType, why) is not { } converted) return null;
            typed.Add(arm with { Value = converted });
        }
        return new IrMatch(match.Span, matchType, subject, typed, switchId);
    }

    private bool _DuplicateArm(MatchArm arm, string what)
    {
        _Error(arm.Span, DiagnosticCodes.DuplicateMatchArm, $"le cas « {what} » a déjà son bras dans ce match", "en double",
            "chaque cas n'a qu'un bras : le second ne serait jamais choisi");
        return true;
    }

    #endregion
}
