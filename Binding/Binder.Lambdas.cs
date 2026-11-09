using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;

using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Les lambdas : leur type <c>(int) =&gt; bool</c>, leur valeur <c>(int v) =&gt; v &gt; seuil</c>, leur appel,
/// et un gear passé par son nom.
/// </summary>
/// <remarks>
/// <para>
/// Le corps d'une lambda se lie comme celui d'un gear, dans une portée qui voit les variables et
/// paramètres extérieurs : ceux qu'il lit sont capturés par copie à la création de la lambda, et ne
/// peuvent donc pas y être modifiés (E005). <c>fail</c>, <c>done</c> et <c>as!</c> n'y ont pas de sens
/// (E060) : une lambda n'est pas un guard. Un nom de paramètre ne peut pas masquer un nom visible (E044).
/// </para>
/// <para>
/// Un paramètre sans type le reçoit du type attendu (E001 sans type attendu). Le type de retour est
/// celui attendu, sinon celui du corps expression, ou du premier <c>return</c> d'un corps bloc
/// (<c>void</c> sans <c>return</c> de valeur).
/// </para>
/// <para>
/// Pureté : quand le type attendu est <c>pure</c>, le corps est lié comme celui d'un gear pure (E057,
/// E066) ; sinon elle est déduite : la lambda est pure si son corps n'appelle que du pur et ne fait
/// aucune entrée ni sortie. Une lambda pure entre dans le type sans <c>pure</c>, pas l'inverse ; un
/// gear pure n'appelle que des lambdas de type pure.
/// </para>
/// </remarks>
internal sealed partial class Binder
{
    /// <summary>La lambda dont on lie le corps : ce qu'elle exige et ce qu'on en apprend.</summary>
    private sealed class LambdaContext(IrType? returnType, bool requirePure)
    {
        /// <summary>Le type de retour ; <see langword="null"/> tant qu'il reste à déduire du premier <c>return</c>.</summary>
        public IrType? ReturnType { get; set; } = returnType;

        /// <summary>Vrai quand le type attendu est <c>pure</c> : un effet dans le corps est une erreur.</summary>
        public bool RequirePure { get; } = requirePure;

        /// <summary>Vrai tant qu'aucun effet n'a été vu dans le corps.</summary>
        public bool IsPure { get; set; } = true;

        /// <summary>Les variables déclarées dans le corps : toute autre variable visible est capturée.</summary>
        public HashSet<VariableSymbol> Locals { get; } = [];

        /// <summary>La portée extérieure à la lambda : une source capturée est consommée seulement dans son corps.</summary>
        public Scope? OuterScope { get; init; }

        /// <summary>Les paramètres de la lambda : tout autre paramètre visible est capturé.</summary>
        public HashSet<ParameterSymbol> Parameters { get; } = [];

        /// <summary>
        /// Vrai pour la lambda d'une étape de parcours ou d'accumulation : elle est appelée sur place, sans quitter la boucle, donc ce
        /// qu'elle capture n'est pas une copie et le contenu d'une variable <c>mut</c> s'y modifie.
        /// </summary>
        public bool InPlace { get; init; }

        /// <summary>Pour la lambda d'une accumulation : le type de l'accumulateur, que <c>done valeur;</c> arrête avec ; <see langword="null"/> ailleurs.</summary>
        public IrType? FoldType { get; init; }
    }

    /// <summary>La lambda dont on lie le corps ; <see langword="null"/> ailleurs.</summary>
    private LambdaContext? _lambda;

    /// <summary><c>(T, U) =&gt; R</c> : <c>void</c> en retour, pas en paramètre.</summary>
    private FunctionType? _BindFunctionType(AstTypes.FunctionType function)
    {
        RArray<IrType> parameters = new();
        var failed = false;
        foreach (var parameter in function.Parameters)
        {
            if (parameter is AstTypes.PrimitiveType { Keyword: TokenKind.VoidKeyword })
            {
                _Error(parameter.Span, DiagnosticCodes.TypeMismatch, "un paramètre de lambda n'est pas void", "void",
                    "une lambda sans paramètre s'écrit () => T");
                failed = true;
            }
            else if (_BindType(parameter) is { } type) parameters.Push(type);
            else failed = true;
        }

        var returnType = function.Return is AstTypes.PrimitiveType { Keyword: TokenKind.VoidKeyword } ? PrimitiveType.Void : _BindType(function.Return);
        return failed || returnType is null ? null : new FunctionType([.. parameters], returnType, function.IsPure);
    }

    /// <summary>
    /// Vrai si le paramètre <c>mut</c> d'une lambda est permis : seulement pour l'élément de la première
    /// étape d'un parcours d'une variable <c>mut</c> (E101 ailleurs, E102 sur une autre étape), et d'un type
    /// qui peut être <c>mut</c> (E101).
    /// </summary>
    private bool _CheckLambdaMut(LambdaParameter parameter, IrType type, bool isEachStep)
    {
        if (!isEachStep)
        {
            _Error(parameter.Span, DiagnosticCodes.InvalidMut, $"le paramètre « {parameter.Name.Name} » d'une lambda n'est mut qu'à l'étape d'un parcours", "mut",
                "une lambda reçoit une copie de ses arguments ; seul un parcours modifie ses éléments sur place, par exemple : ps ||> (mut p) => { p.vie <- 0; }");
            return false;
        }
        if (!_mutableElement)
        {
            _Error(parameter.Span, DiagnosticCodes.InvalidMutArgument, $"l'élément « {parameter.Name.Name} » ne peut pas être modifié sur place ici", "mut",
                "un parcours modifie sur place les éléments d'une variable mut, à sa première étape : mut var ps: …; puis ps ||> (mut p) => …");
            return false;
        }
        return _CheckMutType(parameter.Span, type, $"le paramètre « {parameter.Name.Name} »");
    }

    /// <summary>
    /// Une lambda : ses paramètres (typés, ou par le type attendu), puis son corps, lié comme celui
    /// d'un gear dans une portée qui voit l'extérieur.
    /// </summary>
    /// <param name="lambda">La lambda écrite.</param>
    /// <param name="expectedType">Le type attendu par le contexte, s'il y en a un.</param>
    /// <param name="elementType">
    /// Pour l'étape d'un parcours, le type de l'élément, son unique paramètre ; son retour se déduit
    /// alors du corps.
    /// </param>
    private IrExpression? _BindLambda(LambdaExpression lambda, IrType? expectedType, IrType? elementType = null, bool inPlace = false, IrType? foldType = null)
    {
        var expected = _ExpectedOf<FunctionType>(expectedType);
        // Une étape de parcours reçoit l'élément, et peut recevoir en plus son rang (un uint) : (x, i) => …
        IReadOnlyList<IrType>? given = expected?.Parameters ?? (elementType is null ? null : lambda.Parameters.Count == 2 ? [elementType, PrimitiveType.Int] : [elementType]);
        if (expected is null && given is not null && lambda.Parameters.Count is not (1 or 2))
        {
            _Error(lambda.Span, DiagnosticCodes.AssignmentTypeMismatch, $"l'étape d'un parcours reçoit un élément, et en plus son rang : la lambda prend un ou deux paramètres, pas {lambda.Parameters.Count}",
                $"{lambda.Parameters.Count} paramètre(s)", "par exemple : nums ||> (x) => x * 2, ou nums ||> (x, i) => x * i");
            return null;
        }
        if (expected is not null && expected.Parameters.Count != lambda.Parameters.Count)
        {
            _Error(lambda.Span, DiagnosticCodes.AssignmentTypeMismatch, $"une lambda « {expected} » est attendue : elle prend {expected.Parameters.Count} paramètre(s), pas {lambda.Parameters.Count}",
                $"{lambda.Parameters.Count} paramètre(s)", $"par exemple : {_LambdaExample(expected)}");
            return null;
        }

        RArray<ParameterSymbol> parameters = new();
        var failed = false;
        for (var i = 0; i < lambda.Parameters.Count; ++i)
        {
            var parameter = lambda.Parameters[i];
            var name = parameter.Name;
            if (_scope[name.Name].HasValue || _parameters.ContainsKey(name.Name) || parameters.Some(p => p.Name == name.Name))
            {
                _Error(name.Span, DiagnosticCodes.AlreadyDeclared, $"« {name.Name} » est déjà un nom visible ici", "déjà déclaré",
                    "un paramètre de lambda ne masque pas une variable ni un paramètre : choisis un autre nom");
                failed = true;
                continue;
            }

            IrType? type;
            if (parameter.Type is null)
            {
                type = given?[i];
                if (type is null)
                {
                    _Error(name.Span, DiagnosticCodes.CannotInferType, $"type du paramètre « {name.Name} » inconnu", "de type inconnu",
                        $"écris son type, par exemple : (int {name.Name}) => …, ou déclare le type de la lambda : (int) => int f: ({name.Name}) => …;");
                    failed = true;
                    continue;
                }
            }
            else if (parameter.Type is AstTypes.PrimitiveType { Keyword: TokenKind.VoidKeyword })
            {
                _Error(parameter.Type.Span, DiagnosticCodes.TypeMismatch, "un paramètre de lambda n'est pas void", "void");
                failed = true;
                continue;
            }
            else
            {
                type = _BindType(parameter.Type);
                if (type is null)
                {
                    failed = true;
                    continue;
                }
                if (given is not null && type != given[i])
                {
                    _Error(parameter.Type.Span, DiagnosticCodes.AssignmentTypeMismatch,
                        expected is null ? $"le paramètre « {name.Name} » est un « {type} », mais les éléments parcourus sont des « {given[i]} »"
                            : $"le paramètre « {name.Name} » est un « {type} », mais une lambda « {expected} » est attendue", $"de type « {type} »",
                        $"écris « {given[i]} {name.Name} », ou laisse le type se déduire : ({name.Name}) => …");
                    failed = true;
                    continue;
                }
            }
            // Seul l'élément (le premier paramètre) d'une étape de parcours se modifie sur place : pas son rang.
            if (parameter.IsMutable && i > 0 && elementType is not null && expected is null)
            {
                _Error(parameter.Span, DiagnosticCodes.InvalidMut, $"le rang « {name.Name} » ne peut pas être mut", "mut", "seul l'élément d'un parcours se modifie sur place");
                failed = true;
                continue;
            }
            if (parameter.IsMutable && !_CheckLambdaMut(parameter, type, elementType is not null && expected is null))
            {
                failed = true;
                continue;
            }
            parameters.Push(new ParameterSymbol(name.Name, type, name.Span, parameter.IsMutable));
        }
        if (failed) return null;

        // Le corps : une portée qui voit l'extérieur, sans les switchs ni le « $ » qui l'entourent.
        var context = new LambdaContext(expected?.Return, expected?.IsPure == true) { InPlace = inPlace, FoldType = foldType, OuterScope = _scope };
        foreach (var parameter in parameters) context.Parameters.Add(parameter);
        var saved = (_lambda, _parameters, _scope, _dollar, _expectedEnum, _mutableElement);
        _mutableElement = false;
        var switches = _switches.ToArray();
        _switches.Clear();
        _lambda = context;
        _parameters = new Dictionary<string, ParameterSymbol>(_parameters, StringComparer.Ordinal);
        foreach (var parameter in parameters) _parameters[parameter.Name] = parameter;
        (_dollar, _expectedEnum) = (null, null);
        var errorsBefore = _ErrorCount;

        IrBlock? body;
        if (lambda.Body is { } block) body = _BindStatements(block.Span, block.Statements);
        else body = _BindLambdaExpressionBody(lambda.ExpressionBody!, context);

        if (body is not null && context.ReturnType is { } returnType && returnType != PrimitiveType.Void
            && _ErrorCount == errorsBefore && ControlFlow.CanCompleteNormally(body))
            _Error(lambda.Span, DiagnosticCodes.MissingReturn, $"la lambda peut finir sans renvoyer de « {returnType} »", "un chemin ne renvoie rien",
                "chaque chemin de la lambda se termine par « return valeur; » : vérifie les branches sans else et les cas de switch");

        (_lambda, _parameters, _scope, _dollar, _expectedEnum, _mutableElement) = saved;
        _switches.AddRange(switches);
        if (body is null || _ErrorCount != errorsBefore) return null;

        var function = new FunctionType([.. parameters.AsReadOnly().Select(p => p.Type)], context.ReturnType ?? PrimitiveType.Void, context.IsPure);
        return new IrLambda(lambda.Span, function, [.. parameters], body);
    }

    /// <summary>
    /// Le corps expression d'une lambda : sa valeur est renvoyée (convertie au type de retour attendu,
    /// ou qui le devient) ; une valeur <c>void</c>, ou un retour attendu <c>void</c>, en fait une instruction.
    /// </summary>
    private IrBlock? _BindLambdaExpressionBody(IExpression expression, LambdaContext context)
    {
        var value = _BindExpected(expression, context.ReturnType);
        if (value is null) return null;

        if (context.ReturnType is null)
        {
            if (!_IsLambdaReturnType(value)) return null;
            context.ReturnType = value.Type;
        }

        if (context.ReturnType == PrimitiveType.Void) return new IrBlock(expression.Span, [new IrExpressionStatement(value.Span, value)]);

        var converted = _ConvertTo(value, context.ReturnType, $"la lambda renvoie un « {context.ReturnType} »");
        return converted is null ? null : new IrBlock(expression.Span, [new IrReturn(value.Span, converted)]);
    }

    /// <summary>
    /// Indique si la valeur peut donner le type de retour d'une lambda qui le déduit : pas <c>null</c>
    /// seul, ni un résultat dont un côté manque (E001).
    /// </summary>
    private bool _IsLambdaReturnType(IrExpression value)
    {
        if (value.Type is not NullType && !_IsPending(value.Type)) return true;
        _Error(value.Span, DiagnosticCodes.CannotInferType, $"type de retour de la lambda impossible à déduire de « {value.Type} »", "de type inconnu",
            "déclare le type de la lambda, par exemple : (int) => string? f: (x) => null;");
        return false;
    }

    /// <summary>
    /// <c>return</c> dans une lambda : sa valeur a le type de retour attendu, ou fixe celui de la
    /// lambda au premier <c>return</c> ; <c>return;</c> seul la rend void.
    /// </summary>
    private IrStatement? _BindLambdaReturn(ReturnStatement returnStatement, LambdaContext lambda)
    {
        if (returnStatement.Value is null)
        {
            lambda.ReturnType ??= PrimitiveType.Void;
            if (lambda.ReturnType == PrimitiveType.Void) return new IrReturn(returnStatement.Span, null);
            _Error(returnStatement.Span, DiagnosticCodes.MissingReturn, $"return sans valeur dans une lambda qui renvoie un « {lambda.ReturnType} »", "valeur manquante",
                "par exemple : return 0;");
            return null;
        }

        var value = _BindExpected(returnStatement.Value, lambda.ReturnType);
        if (value is null) return null;

        if (lambda.ReturnType is null)
        {
            if (!_IsLambdaReturnType(value)) return null;
            lambda.ReturnType = value.Type;
            return new IrReturn(returnStatement.Span, value);
        }

        if (lambda.ReturnType == PrimitiveType.Void)
        {
            _Error(value.Span, DiagnosticCodes.TypeMismatch, "cette lambda est void : elle ne renvoie pas de valeur", "valeur en trop",
                "écris « return; », ou une lambda qui renvoie une valeur");
            return null;
        }

        var converted = _ConvertTo(value, lambda.ReturnType, $"la lambda renvoie un « {lambda.ReturnType} »");
        return converted is null ? null : new IrReturn(returnStatement.Span, converted);
    }

    /// <summary>
    /// Vrai si un effet (appel non pur, entrée ou sortie) est permis ici : dans une lambda, il la rend
    /// impure, sauf si son type attendu est <c>pure</c> (<paramref name="code"/>) ; dans un gear pure,
    /// jamais (<paramref name="code"/>). L'erreur est alors signalée.
    /// </summary>
    /// <param name="span">L'effet dans le source.</param>
    /// <param name="code">E057 pour un appel, E066 pour une entrée ou une sortie.</param>
    /// <param name="what">Ce qui a un effet, pour le message : « appeler « log » », « écrire sur la console »…</param>
    private bool _AllowEffect(SourceSpan span, string code, string what)
    {
        if (_lambda is { } lambda)
        {
            if (!lambda.RequirePure)
            {
                lambda.IsPure = false;
                return true;
            }
            _Error(span, code, $"une lambda pure est attendue ici : elle ne peut pas {what}", "pas pure",
                "le type attendu est « pure (…) => … » : retire pure de ce type, ou retire l'effet de la lambda");
            return false;
        }

        if (_currentGear is not { IsPure: true } gear) return true;
        _Error(span, code, $"« {gear.Name} » est pure : il ne peut pas {what}", "pas pure",
            code == DiagnosticCodes.ImpureCall
                ? $"un gear pure n'appelle que du pur (gears pure, lambdas de type pure) : retire pure de « {gear.Name} »"
                : $"un gear pure n'a aucun effet : retire pure de « {gear.Name} », ou fais l'entrée ou la sortie ailleurs");
        return false;
    }

    /// <summary>
    /// Vrai si <paramref name="variable"/> est capturée par la lambda dont on lie le corps : déclarée
    /// hors d'elle, elle y est une copie, qu'on ne modifie pas (E005, signalé ici).
    /// </summary>
    private bool _ReportCaptured(Identifier name, VariableSymbol variable)
    {
        if (_lambda is null || _lambda.Locals.Contains(variable) || variable.IsConstant) return false;
        _Error(name.Span, DiagnosticCodes.AssignToConstant, $"« {name.Name} » est capturée par la lambda : elle n'y est qu'une copie, qui ne se modifie pas", "capturée",
            $"la lambda garde la valeur qu'avait « {name.Name} » à sa création ; pour la modifier dans la lambda, copie-la : var copie: {name.Name};");
        return true;
    }

    /// <summary>
    /// Vrai si <paramref name="parameter"/> est capturé par la lambda dont on lie le corps : paramètre
    /// d'un gear ou d'une lambda englobante, il n'y est qu'une copie, qu'on ne modifie pas (E005, signalé ici).
    /// </summary>
    private bool _ReportCapturedParameter(SourceSpan span, ParameterSymbol parameter)
    {
        if (_lambda is null || _lambda.Parameters.Contains(parameter)) return false;
        _Error(span, DiagnosticCodes.AssignToConstant, $"« {parameter.Name} » est capturé par la lambda : il n'y est qu'une copie, qui ne se modifie pas", "capturé",
            $"la lambda garde la valeur qu'avait « {parameter.Name} » à sa création ; modifie-le hors de la lambda");
        return true;
    }

    /// <summary>Un nom de variable ou de paramètre visible ici : appelé, c'est une lambda.</summary>
    private bool _IsValueName(string name) => _scope[name].HasValue || _parameters.ContainsKey(name);

    /// <summary>
    /// L'appel d'une valeur : <paramref name="target"/> est une lambda (E040 sinon), qui reçoit autant
    /// d'arguments que de paramètres (E058), chacun de son type (E003). Une lambda qui n'est pas pure a un effet.
    /// </summary>
    /// <param name="span">L'appel dans le source.</param>
    /// <param name="target">La valeur appelée, déjà liée.</param>
    /// <param name="first">La valeur qui entre par le pipeline, premier argument ; <see langword="null"/> sans pipeline.</param>
    /// <param name="arguments">Les arguments écrits.</param>
    private IrExpression? _BindInvoke(SourceSpan span, IrExpression? target, IrExpression? first, IReadOnlyList<IExpression> arguments)
    {
        if (target is null) return null;
        var text = target.Span.Text;
        if (target.Type is not FunctionType function)
        {
            // Les arguments sont liés pour signaler leurs propres erreurs.
            foreach (var argument in arguments) _BindExpression(argument);
            _Error(target.Span, DiagnosticCodes.TypeMismatch, $"« {text} » est un « {target.Type} » : seule une lambda s'appelle", "pas une lambda",
                target.Type is NullableType { Inner: FunctionType }
                    ? $"« {text} » peut être null : teste-la d'abord, if ({text} not eq null) {{ … }}"
                    : "un gear s'appelle par son nom ; une variable ou un paramètre ne s'appelle que s'il contient une lambda");
            return null;
        }

        var count = arguments.Count + (first is null ? 0 : 1);
        if (count != function.Parameters.Count)
        {
            foreach (var argument in arguments) _BindExpression(argument);
            _Error(span, DiagnosticCodes.ArgumentCount, $"« {text} » attend {function.Parameters.Count} argument(s), pas {count}", $"{count} argument(s)",
                $"son type : {function}" + (first is null ? "" : " (la valeur du pipeline est le premier argument)"),
                "un appel par une variable ou une référence de gear donne tous les arguments : les valeurs par défaut d'un gear ne s'y appliquent pas");
            return null;
        }

        var converted = new IrExpression?[count];
        var offset = 0;
        if (first is not null)
        {
            converted[0] = _ConvertTo(first, function.Parameters[0], $"le paramètre 1 de « {text} » est un « {function.Parameters[0]} »");
            offset = 1;
        }
        for (var i = 0; i < arguments.Count; ++i)
        {
            var parameter = function.Parameters[i + offset];
            var value = _BindExpected(arguments[i], parameter);
            converted[i + offset] = value is null ? null : _ConvertTo(value, parameter, $"le paramètre {i + offset + 1} de « {text} » est un « {parameter} »");
        }
        if (converted.Any(c => c is null)) return null;

        if (!function.IsPure && !_AllowEffect(span, DiagnosticCodes.ImpureCall, $"appeler « {text} », une lambda qui n'est pas pure")) return null;
        return new IrInvoke(span, target, [.. converted!]);
    }

    /// <summary>
    /// Un gear utilisé comme valeur, par son nom : il a le type de sa signature. Un guard ou un emit ne
    /// se passe pas ainsi (E062) : il ne s'appelle que comme étape d'un train.
    /// </summary>
    private IrExpression? _BindGearReference(Identifier name, GearSymbol? gear)
    {
        if (gear is null) return null;
        if (gear.Kind != GearKind.Gear)
        {
            _Error(name.Span, DiagnosticCodes.GuardOrEmitInExpression, $"« {gear.Name} » est un {_KindName(gear)} : il ne se passe pas comme une lambda", _KindName(gear),
                $"un {_KindName(gear)} ne s'appelle que comme étape d'un train ; seul un gear devient une valeur");
            return null;
        }

        if (gear.Parameters.FirstOrDefault(p => p.IsMutable) is { } mutable)
        {
            _Error(name.Span, DiagnosticCodes.InvalidMut, $"« {gear.Name} » a un paramètre mut, « {mutable.Name} » : il ne se passe pas comme une lambda", "paramètre mut",
                $"une lambda ne modifie pas les variables de l'appelant : appelle « {gear.Name} » directement, ou enveloppe-le dans une lambda qui travaille sur une copie");
            return null;
        }

        _WarnDeprecated(gear, name.Span);
        if (_currentGear is { } current) _calls[current].Add(gear);
        var function = new FunctionType([.. gear.Parameters.Select(p => p.Type)], gear.ReturnType, gear.IsPure);
        return new IrGearReference(name.Span, function, gear);
    }

    /// <summary>Une lambda de ce type, pour les messages : <c>(a, b) =&gt; …</c>.</summary>
    private static string _LambdaExample(FunctionType function) =>
        "(" + string.Join(", ", function.Parameters.Select((_, i) => ((char)('a' + i)).ToString())) + ") => …";
}
