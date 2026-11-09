using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Types;
using Rgl.Syntax;

namespace Rgl.Binding;

/// <summary>
/// Les nullables : <c>T?</c>, <c>??</c>, <c>?°</c>, <c>?.</c>, <c>as?</c>, et le rétrécissement. Un
/// <c>T?</c> ne va pas où un <c>T</c> est attendu, sauf là où le binder sait qu'il n'est pas
/// <c>null</c> : après <c>if (x not eq null)</c> dans le bloc, après <c>if (x eq null) { sortie }</c>
/// dans la suite, à droite d'un <c>and</c>, dans la branche d'un ternaire. Une affectation de la
/// variable fait oublier ce qu'on savait.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Ce qu'un test apprend : les valeurs non <c>null</c> quand il est vrai, et quand il est faux.</summary>
    private readonly record struct NullFacts(IReadOnlyList<(object Symbol, IrType Type)> WhenTrue, IReadOnlyList<(object Symbol, IrType Type)> WhenFalse)
    {
        public static readonly NullFacts None = new([], []);
    }

    #region Rétrécissement

    /// <summary>
    /// Ce qu'une condition apprend sur les nullables : <c>x eq null</c>, <c>x not eq null</c>, et leurs
    /// combinaisons par <c>and</c>, <c>or</c> et <c>not</c>.
    /// </summary>
    private static NullFacts _NullFacts(IrExpression? condition)
    {
        switch (condition)
        {
            case IrBinary { Operator: IrBinaryOperator.Equal } equal when _NullComparison(equal) is { } fact:
                return new([], [fact]);

            case IrUnary { Operator: IrUnaryOperator.Not } not:
                var inner = _NullFacts(not.Operand);
                return new(inner.WhenFalse, inner.WhenTrue);

            case IrBinary { Operator: IrBinaryOperator.And } and:
                return new([.. _NullFacts(and.Left).WhenTrue, .. _NullFacts(and.Right).WhenTrue], []);

            case IrBinary { Operator: IrBinaryOperator.Or } or:
                return new([], [.. _NullFacts(or.Left).WhenFalse, .. _NullFacts(or.Right).WhenFalse]);

            default:
                return NullFacts.None;
        }
    }

    /// <summary>Le nom comparé à <c>null</c> dans <c>x eq null</c> (ou <c>null eq x</c>), avec son type sans nullable.</summary>
    private static (object Symbol, IrType Type)? _NullComparison(IrBinary equal)
    {
        var value = _IsNull(equal.Right) ? equal.Left : _IsNull(equal.Left) ? equal.Right : null;
        while (value is IrConversion or IrNonNull) value = value is IrConversion conversion ? conversion.Operand : ((IrNonNull)value).Value;

        return value switch
        {
            IrVariableReference { Type: NullableType type } reference => (reference.Variable, type.Inner),
            IrParameterReference { Type: NullableType type } reference => (reference.Parameter, type.Inner),
            _ => null,
        };
    }

    private static bool _IsNull(IrExpression expression) => expression is IrNullConstant or IrConversion { Operand: IrNullConstant };

    /// <summary>Lie <paramref name="bind"/> dans un bloc où les valeurs de <paramref name="facts"/> ne sont pas <c>null</c>.</summary>
    private T _WithNarrowings<T>(IReadOnlyList<(object Symbol, IrType Type)> facts, Func<T> bind)
    {
        if (facts.Count == 0) return bind();

        _scope = new Scope(_scope);
        foreach (var (symbol, type) in facts) _scope.Narrow(symbol, type);
        var result = bind();
        _scope = _scope.Parent!;
        return result;
    }

    /// <summary>Une variable ou un paramètre lu : une valeur non <c>null</c> si le binder le sait à cet endroit.</summary>
    private IrExpression _Narrowed(IrExpression reference, object symbol) =>
        reference.Type is NullableType && _scope.NarrowedType(symbol) is not null ? new IrNonNull(reference.Span, reference) : reference;

    /// <summary>
    /// Après l'affectation d'une variable nullable : non <c>null</c> si la valeur ne l'est pas (un
    /// <c>T</c>), sinon on n'en sait plus rien.
    /// </summary>
    private void _AfterAssignment(Rgl.IR.Symbols.VariableSymbol variable, IrExpression value)
    {
        if (variable.Type is not NullableType nullable) return;
        _scope.Widen(variable);
        if (value.Type is not (NullableType or NullType)) _scope.Narrow(variable, nullable.Inner);
    }

    /// <summary>
    /// Avant un switch : un <c>goto</c> peut revenir en arrière, donc ce qu'on savait d'une variable
    /// modifiée quelque part dans le switch est oublié pour tout le switch.
    /// </summary>
    private void _WidenAssignedIn(SwitchStatement switchStatement)
    {
        foreach (var name in switchStatement.Sections.SelectMany(s => s.Body).SelectMany(_AssignedNames))
        {
            if (_scope[name] is { HasValue: true } entry && entry.Value.Symbol is { } variable) _scope.Widen(variable);
        }
    }

    /// <summary>Les noms affectés (ou libérés) dans une instruction et ce qu'elle contient.</summary>
    private static IEnumerable<string> _AssignedNames(IStatement statement) => statement switch
    {
        AssignmentStatement assignment => [assignment.Target.Name],
        UndefineStatement undefine => [undefine.Name.Name],
        BlockStatement block => block.Statements.SelectMany(_AssignedNames),
        IfStatement ifStatement => [.. _AssignedNames(ifStatement.Then), .. ifStatement.Else is null ? [] : _AssignedNames(ifStatement.Else)],
        SwitchStatement nested => nested.Sections.SelectMany(s => s.Body).SelectMany(_AssignedNames),
        _ => [],
    };

    #endregion

    #region Coalescence, accès sûr, as?

    /// <summary>
    /// <c>a ?? b</c> : <c>a</c> est nullable (W011 sinon) ; <c>b</c> est un <c>T</c> (le résultat aussi)
    /// ou un <c>T?</c> (le résultat reste nullable).
    /// </summary>
    private IrExpression? _BindCoalesce(BinaryExpression binary)
    {
        var left = _BindExpression(binary.Left);
        var right = _BindExpression(binary.Right);
        if (left is null || right is null) return null;

        if (left.Type is NullType)
        {
            _diagnostics.Warning(left.Span, DiagnosticCodes.UselessCoalescence, "la valeur est toujours null : « ?? » donne toujours la droite", "toujours null");
            return right;
        }

        // Un résultat : sa valeur en cas de succès, sinon la valeur de secours.
        if (left.Type is ResultType { IsPending: false } result)
        {
            if (_Coerce(right, result.Value) is { } fallback) return new IrCoalesce(binary.Span, result.Value, left, fallback);
            _Error(right.Span, DiagnosticCodes.TypeMismatch, $"la valeur de secours de « ?? » doit être un « {result.Value} », pas un « {right.Type} »", $"de type « {right.Type} »",
                $"« {left.Span.Text} » est un « {result} » : la droite remplace l'erreur");
            return null;
        }
        if (left.Type is not NullableType nullable)
        {
            _diagnostics.Warning(left.Span, DiagnosticCodes.UselessCoalescence, $"un « {left.Type} » n'est jamais null : « ?? » garde toujours la gauche", "jamais null",
                ["« ?? » sert aux valeurs nullables (T?) : retire-le"]);
            return left;
        }

        if (_Coerce(right, nullable.Inner) is { } value) return new IrCoalesce(binary.Span, nullable.Inner, left, value);
        if (_Coerce(right, nullable) is { } maybe) return new IrCoalesce(binary.Span, nullable, left, maybe);

        _Error(right.Span, DiagnosticCodes.TypeMismatch, $"la valeur de secours de « ?? » doit être un « {nullable.Inner} », pas un « {right.Type} »", $"de type « {right.Type} »",
            $"« {left.Span.Text} » est un « {nullable} » : la droite le remplace quand il est null");
        return null;
    }

    /// <summary>
    /// <c>a ?° b</c> : <c>a</c> s'il n'est pas vide, sinon <c>b</c>. Vide : une chaîne <c>""</c>, un
    /// tableau sans élément, <c>null</c> pour un nullable, un mold sans champ (<c>{}</c>). Un mold qui a
    /// des champs n'est jamais vide (W011).
    /// </summary>
    private IrExpression? _BindEmptyCoalesce(BinaryExpression binary)
    {
        var left = _BindExpression(binary.Left);
        var right = _BindExpression(binary.Right);
        if (left is null || right is null) return null;

        var nullable = left.Type as NullableType;
        var core = nullable?.Inner ?? left.Type;

        if (core is MoldType mold)
        {
            if (mold.Symbol.Fields.Count == 0)
            {
                _diagnostics.Warning(left.Span, DiagnosticCodes.UselessCoalescence, "un mold sans champ est toujours vide : « ?° » donne toujours la droite", "toujours vide");
                return right;
            }
            if (nullable is null)
            {
                _diagnostics.Warning(left.Span, DiagnosticCodes.UselessCoalescence, $"un mold qui a des champs n'est jamais vide : « ?° » garde toujours la gauche", "jamais vide",
                    ["un mold n'est vide que s'il n'a aucun champ ({})"]);
                return left;
            }
            // Un mold nullable n'est vide que s'il est null : c'est « ?? ».
            if (_Coerce(right, mold) is { } value) return new IrCoalesce(binary.Span, mold, left, value);
            if (_Coerce(right, nullable) is { } maybe) return new IrCoalesce(binary.Span, nullable, left, maybe);
        }
        else if (core is ArrayType or PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString })
        {
            if (_Coerce(right, core) is { } value) return new IrEmptyCoalesce(binary.Span, core, left, value);
            if (nullable is not null && _Coerce(right, nullable) is { } maybe) return new IrEmptyCoalesce(binary.Span, nullable, left, maybe);
        }
        else
        {
            _Error(left.Span, DiagnosticCodes.TypeMismatch, $"« ?° » teste si une valeur est vide, et un « {left.Type} » ne l'est jamais", $"de type « {left.Type} »",
                "« ?° » s'applique aux chaînes, aux tableaux, aux molds et à leurs nullables ; pour un null seulement, écris « ?? »");
            return null;
        }

        _Error(right.Span, DiagnosticCodes.TypeMismatch, $"la valeur de secours de « ?° » doit être un « {core} », pas un « {right.Type} »", $"de type « {right.Type} »");
        return null;
    }

    /// <summary>
    /// <c>valeur?.champ</c> : le champ si la valeur n'est pas <c>null</c>, sinon <c>null</c> ; le résultat
    /// est nullable. Sur une valeur qui n'est jamais <c>null</c>, c'est un simple <c>.</c> (W011).
    /// </summary>
    private IrExpression? _BindSafeAccess(MemberAccessExpression access, IrExpression target)
    {
        if (target.Type is not NullableType nullable)
        {
            if (target.Type is MoldType)
                _diagnostics.Warning(access.Span, DiagnosticCodes.UselessCoalescence, $"un « {target.Type} » n'est jamais null : « ?. » se lit comme « . »", "jamais null");
            return _FieldAccess(access, target, target.Type);
        }

        if (_FieldOf(access, nullable.Inner) is not { } field) return null;
        var result = field.Type as NullableType ?? new NullableType(field.Type);
        return new IrSafeAccess(access.Span, result, target, field);
    }

    #endregion
}
