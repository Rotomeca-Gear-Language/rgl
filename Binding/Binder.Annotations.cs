using Rgl.AST;
using Rgl.AST.Collections;
using Rgl.AST.Gears;
using Rgl.AST.Interfaces;
using Rgl.AST.Notes;
using Rgl.AST.Train;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Les annotations du programme et <c>@before</c>. <c>@before(contrôle)</c> s'exécute avant le corps d'un gear : <c>contrôle</c> est une fonction qui reçoit les
/// paramètres du gear et rend <c>R?</c> ; une réponse non nulle est renvoyée à la place du corps (elle doit entrer dans le type de retour du gear), <c>null</c>
/// laisse le gear s'exécuter. Le contrôle est lié comme du code ordinaire, donc ses types, ses erreurs et sa pureté sont vérifiés comme partout.
/// <c>annotation exigeRole(params [Role] roles) = @before(refuseSauf(roleCourant, roles));</c> déclare une annotation : un paquet d'annotations de base dont les
/// paramètres se lisent, dans son corps, comme des constantes ; <c>@exigeRole(Role.Admin, Role.Editeur)</c> devant un gear la développe à la compilation, sans
/// rien lire à l'exécution. Le nom d'une annotation est unique dans le programme (celui d'une annotation de base est refusé) ; le corps d'une annotation se lie
/// dans le fichier qui la déclare (ses noms, ses types), les arguments dans celui du gear. Pas encore : une annotation dans le corps d'une annotation, <c>@tap</c>
/// et <c>@deprecated</c> développés, <c>@before</c> sur un emit ou un gear async.
/// </summary>
internal sealed partial class Binder
{
    private static readonly HashSet<string> _builtInAnnotations = new(["deprecated", "tap", "before", "async", "sync", "fire", "task", "trusted", "trust", "checked", "unchecked"], StringComparer.Ordinal);

    /// <summary>Les annotations déclarées par le programme, par nom, avec l'espace de noms de leur fichier.</summary>
    private readonly Dictionary<string, (AnnotationDeclaration Declaration, Namespace Namespace)> _annotations = new(StringComparer.Ordinal);

    /// <summary>Retient les annotations déclarées dans la portée courante.</summary>
    private void _DeclareAnnotations(IEnumerable<AnnotationDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            var name = declaration.Name.Name;
            if (_builtInAnnotations.Contains(name))
                _Error(declaration.Name.Span, DiagnosticCodes.InvalidAnnotation, $"« @{name} » est une annotation du langage : choisis un autre nom", "nom pris", "les annotations du langage ne se redéfinissent pas");
            else if (_annotations.ContainsKey(name))
                _Error(declaration.Name.Span, DiagnosticCodes.InvalidAnnotation, $"l'annotation « @{name} » est déjà déclarée", "déjà déclarée", "le nom d'une annotation est unique dans le programme");
            else _annotations[name] = (declaration, _ns);

            foreach (var body in declaration.Body)
            {
                if (body is not BeforeAnnotation)
                    _Unsupported(body.Span, "une annotation autre que @before dans le corps d'une annotation");
            }
            foreach (var parameter in declaration.Parameters.Where(p => p.IsMutable || p.IsThis || p.Default is not null))
                _Error(parameter.Span, DiagnosticCodes.InvalidAnnotation, $"le paramètre « {parameter.Name.Name} » de l'annotation : ni mut, ni this, ni valeur par défaut", "pas permis ici",
                    "un paramètre d'annotation se lit comme une constante");
        }
    }

    /// <summary>Les contrôles <c>@before</c> d'un gear, écrits ou développés depuis ses annotations, liés dans l'ordre ; ils s'exécutent avant son corps.</summary>
    private List<IrStatement> _BindBefores(GearDeclaration gear, GearSymbol symbol)
    {
        List<IrStatement> result = [];
        var annotations = gear.Notes?.Annotations.Where(a => a is BeforeAnnotation or CustomAnnotation).ToArray() ?? [];
        if (annotations.Length == 0) return result;

        if (symbol.Kind == GearKind.Emit)
        {
            _Error(annotations[0].Span, DiagnosticCodes.InvalidAnnotation, "@before ne se pose pas sur un emit : il termine le chemin du train", "sur un emit", "pose-le sur un gear ou un guard");
            return result;
        }
        if (symbol.Async != AsyncMode.None)
        {
            _Unsupported(annotations[0].Span, "@before sur un gear async");
            return result;
        }

        foreach (var annotation in annotations)
        {
            switch (annotation)
            {
                case BeforeAnnotation before:
                    if (_BindBefore(before.Check, before.Span, symbol, _ns) is { } statement) result.Add(statement);
                    break;
                case CustomAnnotation custom:
                    result.AddRange(_ExpandAnnotation(custom, symbol));
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Développe <c>@nom(arguments)</c> : les arguments deviennent les constantes que le corps de l'annotation lit, puis chaque <c>@before</c> du corps est lié. Le
    /// tout vit dans un bloc : les constantes n'existent pas dans le corps du gear.
    /// </summary>
    private List<IrStatement> _ExpandAnnotation(CustomAnnotation use, GearSymbol symbol)
    {
        List<IrStatement> result = [];
        if (!_annotations.TryGetValue(use.Name.Name, out var found))
        {
            _ReportUnknownAnnotation(use);
            return result;
        }

        var (declaration, declarationNamespace) = found;
        var parameters = declaration.Parameters;
        var fixedCount = parameters.Count(p => !p.IsParams);
        var hasParams = parameters.Any(p => p.IsParams);
        if (use.Arguments.Count < fixedCount || (!hasParams && use.Arguments.Count > fixedCount))
        {
            _Error(use.Span, DiagnosticCodes.InvalidAnnotation,
                $"@{use.Name.Name} attend {(hasParams ? $"au moins {fixedCount}" : fixedCount.ToString())} argument(s), pas {use.Arguments.Count}", "mauvais nombre d'arguments",
                $"annotation {use.Name.Name}({string.Join(", ", parameters.Select(p => $"{(p.IsParams ? "params " : "")}{p.Type.Span.Text} {p.Name.Name}"))})");
            return result;
        }

        var callerNamespace = _ns;
        var outer = _scope;
        _scope = new Scope(outer);
        List<IrStatement> block = [];
        var failed = false;
        for (var i = 0; i < parameters.Count && !failed; ++i)
        {
            var parameter = parameters[i];
            IExpression argument = parameter.IsParams
                ? new ArrayExpression(use.Span, [.. use.Arguments.Skip(i)])
                : use.Arguments[i];

            _ns = declarationNamespace;
            var type = _BindType(parameter.Type);
            _ns = callerNamespace;
            if (type is null) { failed = true; break; }

            var value = _BindExpected(argument, type);
            if (value is null || _ConvertTo(value, type, $"le paramètre « {parameter.Name.Name} » de @{use.Name.Name} est « {type} »") is not { } converted) { failed = true; break; }

            var variable = new VariableSymbol(parameter.Name.Name, type, true, parameter.Name.Span, ConstantFolder.Fold(converted));
            _scope.Declare(parameter.Name.Name, new ScopeEntry(variable, parameter.Name.Span));
            block.Add(new IrVariableDeclaration(use.Span, variable, converted));
        }

        if (!failed)
        {
            foreach (var body in declaration.Body.OfType<BeforeAnnotation>())
            {
                if (_BindBefore(body.Check, use.Span, symbol, declarationNamespace) is { } statement) block.Add(statement);
            }
            result.Add(new IrBlock(use.Span, block));
        }

        _ns = callerNamespace;
        _scope = outer;
        return result;
    }

    /// <summary>
    /// Lie un contrôle : la fonction (dans l'espace de noms de <paramref name="checkNamespace"/>), son appel avec les paramètres du gear, puis « si la réponse n'est
    /// pas null, elle est renvoyée ».
    /// </summary>
    private IrStatement? _BindBefore(IExpression check, SourceSpan span, GearSymbol symbol, Namespace checkNamespace)
    {
        var callerNamespace = _ns;
        _ns = checkNamespace;
        var function = _BindExpression(check);
        _ns = callerNamespace;
        if (function is null) return null;

        if (function.Type is not FunctionType { Return: NullableType } type)
        {
            _Error(check.Span, DiagnosticCodes.InvalidAnnotation, $"le contrôle d'un @before est une fonction qui rend un résultat nullable ; celui-ci est « {function.Type} »", "pas une fonction T?",
                "il reçoit les paramètres du gear et rend une réponse, ou null pour laisser le gear s'exécuter");
            return null;
        }
        if (type.Parameters.Count != symbol.Parameters.Count)
        {
            _Error(check.Span, DiagnosticCodes.InvalidAnnotation, $"le contrôle reçoit {type.Parameters.Count} paramètre(s) mais « {symbol.Name} » en a {symbol.Parameters.Count}", "mauvaise forme",
                "le contrôle reçoit exactement les paramètres du gear");
            return null;
        }

        List<IrExpression> arguments = [];
        for (var i = 0; i < symbol.Parameters.Count; ++i)
        {
            if (_ConvertTo(new IrParameterReference(span, symbol.Parameters[i]), type.Parameters[i], $"le contrôle de @before reçoit « {type.Parameters[i]} » pour « {symbol.Parameters[i].Name} »") is not { } converted)
                return null;
            arguments.Add(converted);
        }

        var checkVariable = new VariableSymbol("before", function.Type, true, span, null);
        var invoke = new IrInvoke(span, new IrVariableReference(span, checkVariable), arguments);
        var answerVariable = new VariableSymbol("denied", invoke.Type, true, span, null);
        var answer = new IrVariableReference(span, answerVariable);

        var denied = new IrNonNull(span, answer);
        if (_ConvertTo(denied, symbol.BodyType, $"la réponse du contrôle est renvoyée par « {symbol.Name} », qui renvoie « {symbol.BodyType} »") is not { } returned) return null;

        var isAnswered = new IrUnary(span, PrimitiveType.Bool, IrUnaryOperator.Not,
            new IrBinary(span, PrimitiveType.Bool, IrBinaryOperator.Equal, answer, new IrConversion(span, invoke.Type, new IrNullConstant(span)), span));
        return new IrBlock(span,
        [
            new IrVariableDeclaration(span, checkVariable, function),
            new IrVariableDeclaration(span, answerVariable, invoke),
            new IrIf(span, isAnswered, new IrBlock(span, [new IrReturn(span, returned)]), null),
        ]);
    }

    /// <summary>E125 pour <c>@nom</c> qui n'est ni une annotation du langage ni une annotation déclarée.</summary>
    private void _ReportUnknownAnnotation(CustomAnnotation use) =>
        _Error(use.Name.Span, DiagnosticCodes.InvalidAnnotation, $"annotation « @{use.Name.Name} » inconnue", "annotation inconnue",
            $"annotations du langage : {string.Join(", ", _builtInAnnotations.Order())}",
            _annotations.Count == 0 ? "une annotation se déclare par : annotation nom(paramètres) = @before(controle);" : $"annotations déclarées : {string.Join(", ", _annotations.Keys.Order())}");
}
