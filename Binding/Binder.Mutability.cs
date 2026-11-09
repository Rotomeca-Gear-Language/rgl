using Rgl.AST;
using Rgl.AST.Collections;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// La mutabilité : <c>mut</c> sur une variable ou un paramètre rend son contenu modifiable en place.
/// </summary>
/// <remarks>
/// <para>
/// <c>mut</c> ne concerne que le contenu, en profondeur : les éléments d'un tableau, les champs d'un mold,
/// les valeurs d'un dictionnaire (<c>a[i] &lt;- v</c>, <c>a[i].champ &lt;- v</c>, <c>pos.x &lt;- v</c>), et ce
/// qu'ils contiennent. Il ne s'écrit donc que sur un tableau, un mold ou un dictionnaire (E101), jamais sur
/// une constante. Une variable sans <c>mut</c> reste réaffectable ; <c>push</c> et <c>add</c> sur une variable
/// locale ne demandent pas <c>mut</c>. Toute affectation copie : deux variables ne partagent jamais un contenu.
/// </para>
/// <para>
/// Un paramètre <c>mut</c> désigne la variable de l'appelant : le gear la modifie (contenu, <c>push</c>, ou
/// nouvelle valeur). L'argument est une variable <c>mut</c> du type exact du paramètre — ni constante, ni
/// paramètre ordinaire, ni valeur calculée —, et la même variable n'est pas passée deux fois en <c>mut</c> au
/// même appel (E102). Rien ne s'écrit à l'appel. Un gear à paramètre <c>mut</c> ne devient pas une lambda (E101).
/// </para>
/// </remarks>
internal sealed partial class Binder
{
    /// <summary>
    /// Vrai si <paramref name="type"/> peut être <c>mut</c> : un tableau, un mold ou un dictionnaire ; sinon
    /// E101, signalé ici.
    /// </summary>
    /// <param name="span">Le <c>mut</c> et ce qu'il qualifie, dans le source.</param>
    /// <param name="type">Le type de la variable ou du paramètre.</param>
    /// <param name="what">Ce qui est déclaré <c>mut</c>, pour le message : « « ps » », « le paramètre « p » ».</param>
    private bool _CheckMutType(SourceSpan span, IrType type, string what)
    {
        if (type is ArrayType or MoldType or DictionaryType) return true;
        _Error(span, DiagnosticCodes.InvalidMut, $"{what} est un « {type} » : il ne peut pas être mut", "mut",
            "mut rend modifiable le contenu d'un tableau, d'un mold ou d'un dictionnaire",
            "une autre valeur se remplace entière, sans mut : nom <- nouvelleValeur;");
        return false;
    }

    /// <summary>
    /// <c>p &lt;- valeur</c> sur un paramètre <c>mut</c> : la variable de l'appelant reçoit la nouvelle
    /// valeur, du type du paramètre (E003). Une affectation composée relit la valeur d'avant.
    /// </summary>
    private IrStatement? _BindMutableParameterAssignment(AST.Variables.AssignmentStatement assignment, ParameterSymbol parameter)
    {
        var plain = assignment.Operator is TokenKind.Assign or TokenKind.MoveAssign;
        var value = _BindExpected(assignment.Value, plain ? parameter.Type : null);
        if (_ReportCapturedParameter(assignment.Target.Span, parameter) || value is null) return null;
        if (assignment.TypeHint is { } hint && _BindType(hint) is { } written && written != parameter.Type)
        {
            _Error(hint.Span, DiagnosticCodes.TypeHintMismatch, $"« {parameter.Name} » est de type « {parameter.Type} », pas « {written} »", "type rappelé",
                "le type écrit après le nom doit être celui de la déclaration : ce n'est pas une conversion");
            return null;
        }

        var target = new IrParameterReference(assignment.Target.Span, parameter);
        if (!plain)
        {
            TokenKind? op = assignment.Operator switch
            {
                TokenKind.PlusAssign => TokenKind.Plus,
                TokenKind.MinusAssign => TokenKind.Minus,
                TokenKind.StarAssign => TokenKind.Star,
                TokenKind.SlashAssign => TokenKind.Slash,
                _ => null,
            };
            if (op is null)
            {
                _Unsupported(assignment.Span, "« °<- » sur un paramètre mut");
                return null;
            }
            value = _BindOperation(assignment.Span, assignment.OperatorSpan, op.Value, new IrTargetValue(target.Span, parameter.Type), value);
            if (value is null) return null;
        }

        var converted = _ConvertTo(value, parameter.Type, $"« {parameter.Name} » est de type « {parameter.Type} »");
        if (converted is not null && assignment.Operator == TokenKind.MoveAssign) converted = _Moved(assignment.Value, converted, parameter.Name);
        converted = _NoCopy(converted, assignment.Value.Span, $"« {parameter.Name} »");
        return converted is null ? null : new IrContentAssignment(assignment.Span, target, converted);
    }

    /// <summary>
    /// <c>cible &lt;- valeur</c> sur un champ ou un élément : le contenu d'une variable ou d'un paramètre
    /// <c>mut</c> (E013 sinon), modifié en place. Une affectation composée (<c>+&lt;-</c>) relit la cible par
    /// <see cref="IrTargetValue"/>, sans évaluer ses index deux fois.
    /// </summary>
    private IrStatement? _BindFieldAssignment(FieldAssignmentStatement assignment)
    {
        var indexer = _TryBindIndexerWrite(assignment, out var isIndexer);
        if (isIndexer) return indexer;

        var target = _BindExpression(assignment.Target);
        if (target is null)
        {
            _BindExpression(assignment.Value);
            return null;
        }
        if (!_CheckContentTarget(assignment.Target, target, "modifié en place")) return null;

        var plain = assignment.Operator is TokenKind.Assign or TokenKind.MoveAssign;
        var value = _BindExpected(assignment.Value, plain ? target.Type : null);
        if (value is null) return null;

        if (!plain)
        {
            TokenKind? op = assignment.Operator switch
            {
                TokenKind.PlusAssign => TokenKind.Plus,
                TokenKind.MinusAssign => TokenKind.Minus,
                TokenKind.StarAssign => TokenKind.Star,
                TokenKind.SlashAssign => TokenKind.Slash,
                _ => null,
            };
            if (op is null)
            {
                _Unsupported(assignment.Span, "« °<- » sur un champ ou un élément");
                return null;
            }

            value = _BindOperation(assignment.Span, assignment.Target.Span, op.Value, new IrTargetValue(target.Span, target.Type), value);
            if (value is null) return null;
        }

        var converted = _ConvertTo(value, target.Type, $"« {assignment.Target.Span.Text} » est un « {target.Type} »");
        if (converted is not null && assignment.Operator == TokenKind.MoveAssign) converted = _Moved(assignment.Value, converted, null);
        converted = _NoCopy(converted, assignment.Value.Span, $"« {assignment.Target.Span.Text} »");
        return converted is null ? null : new IrContentAssignment(assignment.Span, target, converted);
    }

    /// <summary>
    /// La valeur de <c>::</c> ou <c>&lt;-|</c>, déplacée quand c'est possible : une variable locale, ni
    /// constante ni capturée, dont la valeur n'a pas à être convertie, devient un <see cref="IrMove"/> et
    /// est libérée (comme par <c>undefine</c>) ; une valeur calculée est déjà déplacée. Une autre variable
    /// est copiée, et le dit (W014). Dans les deux cas, le nom source est libéré ; la copie ne conserve pas son accès.
    /// </summary>
    /// <param name="syntax">La valeur écrite.</param>
    /// <param name="value">La valeur liée, déjà du type de la cible.</param>
    /// <param name="target">Le nom qui la reçoit ; un déplacement vers soi-même ne libère rien.</param>
    private IrExpression? _Moved(IExpression syntax, IrExpression? value, string? target)
    {
        if (value is null || syntax is not NameExpression { Name: var name } || name.Name == target) return value;
        if (LifecycleRules.Has(value.Type) && value is not IrVariableReference { Variable.IsConstant: false }) return value;   // E136 le dit : la valeur n'a pas à être copiée

        string? reason = value switch
        {
            IrVariableReference { Variable.IsConstant: true } => $"« {name.Name} » est une constante",
            IrVariableReference { Variable: var captured } when _lambda is not null && !_lambda.Locals.Contains(captured) => $"« {name.Name} » est capturée par la lambda",
            IrVariableReference => null,
            IrParameterReference => $"« {name.Name} » est un paramètre : la valeur appartient à l'appelant",
            _ => $"la valeur de « {name.Name} » est convertie au type de la cible",
        };
        if (reason is not null)
        {
            _diagnostics.Warning(syntax.Span, DiagnosticCodes.MoveNotPossible, $"« {name.Name} » ne peut pas être déplacée : {reason}", "copiée",
                ["la valeur est copiée, mais le nom source est libéré ; pour conserver la source, écris « : » ou « <- »"]);
            // La table des paramètres appartient au gear courant (et est copiée pour une lambda).
            _parameters.Remove(name.Name);
            var source = _scope[name.Name];
            if (source.HasValue && source.Value.Symbol is { } variable
                && (_lambda is null || _lambda.Locals.Contains(variable))
                && !(_ns.Scope[name.Name] is { HasValue: true } global && global.Value.Symbol == variable))
            {
                _scope.Release(name.Name, syntax.Span);
                _scope.Widen(variable);
            }
            else
            {
                // Une capture appartient à la lambda, un paramètre à cet appel : ne pas consommer le nom dans l'appelant.
                var bodyScope = _scope;
                while (bodyScope.Parent is { } parent && parent != _ns.Scope && parent != _lambda?.OuterScope)
                    bodyScope = parent;
                bodyScope.Hide(name.Name, syntax.Span);
            }
            return value;
        }

        var reference = (IrVariableReference)value;
        _parameters.Remove(name.Name);
        _scope.Release(name.Name, syntax.Span);
        _scope.Widen(reference.Variable);
        return new IrMove(value.Span, reference);
    }


    /// <summary>
    /// Vrai si <paramref name="target"/> se modifie en place : une suite d'accès (champ, élément, élément de
    /// tuple) qui part d'une variable ou d'un paramètre <c>mut</c>. Sinon l'erreur est signalée : E013 sans
    /// <c>mut</c> ou sur ce qui n'est pas un contenu (<c>a.length</c>, <c>a?.b</c>), E005 pour une variable
    /// capturée par une lambda.
    /// </summary>
    /// <param name="syntax">La cible écrite.</param>
    /// <param name="target">La cible liée.</param>
    /// <param name="action">Ce qui lui arrive, pour le message : « modifié en place », « agrandi par push ».</param>
    private bool _CheckContentTarget(IExpression syntax, IrExpression target, string action)
    {
        var current = target;
        while (current is IrMemberAccess or IrIndex or IrTupleItem or IrRetype)
            current = current switch
            {
                IrMemberAccess access => access.Target,
                IrIndex index => index.Target,
                IrTupleItem item => item.Target,
                _ => ((IrRetype)current).Value,
            };

        var root = _SyntaxRoot(syntax);
        switch (current)
        {
            // Une lambda appelée sur place (une étape de parcours) modifie le contenu de ce qu'elle capture : ce n'est pas une copie.
            case IrVariableReference { Variable: var variable } when variable.IsMutable:
                return root is null || _lambda is { InPlace: true } || !_ReportCaptured(root, variable);

            case IrParameterReference { Parameter: { IsMutable: true } mutable }:
                return _lambda is { InPlace: true } || !_ReportCapturedParameter(syntax.Span, mutable);

            case IrVariableReference { Variable: var variable }:
                _Error(syntax.Span, DiagnosticCodes.InPlaceModification, $"« {syntax.Span.Text} » ne peut pas être {action} : « {variable.Name} » n'est pas mut", "pas mut",
                    variable.IsConstant
                        ? $"« {variable.Name} » est une constante : copie-la dans une variable mut, par exemple : mut var copie: {variable.Name};"
                        : $"pour modifier son contenu, écris mut devant sa déclaration, ligne {variable.Declaration.StartPosition.Line}");
                return false;

            case IrParameterReference { Parameter: var parameter }:
                _Error(syntax.Span, DiagnosticCodes.InPlaceModification, $"« {syntax.Span.Text} » ne peut pas être {action} : le paramètre « {parameter.Name} » n'est pas mut", "pas mut",
                    $"un paramètre mut modifie la variable de l'appelant : (mut {parameter.Type} {parameter.Name})",
                    $"sinon, copie-le dans une variable : mut var copie: {parameter.Name};");
                return false;

            default:
                _Error(syntax.Span, DiagnosticCodes.InPlaceModification, $"« {syntax.Span.Text} » ne peut pas être {action}", "pas une variable",
                    "seul le contenu d'une variable ou d'un paramètre mut se modifie : ses champs, ses éléments et les valeurs de ses clés");
                return false;
        }
    }

    /// <summary>Le nom au départ d'une cible écrite (<c>ps</c> dans <c>ps[i].vie</c>) ; <see langword="null"/> s'il n'y en a pas.</summary>
    private static Identifier? _SyntaxRoot(IExpression syntax) => syntax switch
    {
        NameExpression name => name.Name,
        MemberAccessExpression access => _SyntaxRoot(access.Target),
        IndexExpression index => _SyntaxRoot(index.Target),
        _ => null,
    };

    /// <summary>
    /// Les arguments des paramètres <c>mut</c> d'un appel de <paramref name="gear"/> : chacun est une
    /// variable ou un paramètre <c>mut</c> du type exact du paramètre, que la lambda en cours ne capture pas,
    /// et aucun n'est passé deux fois (E102). L'élément d'un parcours qui modifie sur place est permis.
    /// </summary>
    /// <param name="gear">Le gear appelé.</param>
    /// <param name="arguments">Ses arguments liés, avant conversion, la valeur du pipeline comprise.</param>
    /// <param name="mutableElement">Vrai quand l'élément d'un parcours (<see cref="IrElementValue"/>) peut être modifié.</param>
    private bool _CheckMutArguments(GearSymbol gear, IReadOnlyList<IrExpression> arguments, bool mutableElement)
    {
        var passed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var valid = true;
        for (var i = 0; i < arguments.Count; ++i)
        {
            var parameter = gear.Parameters[i];
            if (!parameter.IsMutable) continue;

            var argument = arguments[i];
            string? problem = argument switch
            {
                IrElementValue when mutableElement => null,
                IrElementValue => "l'élément d'un parcours ne se modifie qu'à la première étape d'un parcours de variable mut",
                IrVariableReference { Variable: { IsMutable: false } variable } => variable.IsConstant ? $"« {variable.Name} » est une constante" : $"« {variable.Name} » n'est pas mut",
                IrParameterReference { Parameter: { IsMutable: false } other } => $"le paramètre « {other.Name} » n'est pas mut",
                IrVariableReference or IrParameterReference => null,
                // La valeur qui circule dans un train n'appartient qu'à lui : l'étape la modifie sur place.
                IrPipelineValue => null,
                _ => "c'est une valeur calculée, pas une variable",
            };
            if (problem is null && argument.Type != parameter.Type)
                problem = $"c'est un « {argument.Type} », et un paramètre mut reçoit exactement son type, « {parameter.Type} »";
            if (problem is null && argument is IrVariableReference { Variable: var captured } && _lambda is { InPlace: false } && !_lambda.Locals.Contains(captured))
                problem = $"« {captured.Name} » est capturée par la lambda, qui n'en a qu'une copie";
            if (problem is null && argument is IrParameterReference { Parameter: var outer } && _lambda is { InPlace: false } && !_lambda.Parameters.Contains(outer))
                problem = $"« {outer.Name} » est capturé par la lambda, qui n'en a qu'une copie";
            if (problem is null && IrMutation.Symbol(argument) is { } symbol && !passed.Add(symbol))
                problem = "cette variable est déjà passée en mut à cet appel";

            if (problem is null) continue;
            _Error(argument.Span, DiagnosticCodes.InvalidMutArgument, $"« {argument.Span.Text} » ne peut pas être passé au paramètre mut « {parameter.Name} » de « {gear.Name} » : {problem}",
                "argument mut", "un paramètre mut reçoit une variable mut, que le gear modifie : mut var liste: …; puis " + gear.Name + "(liste)");
            valid = false;
        }
        return valid;
    }

    /// <summary>
    /// La cible de <c>push</c> ou <c>add</c> : une variable locale (sans <c>mut</c>, ni constante — E005),
    /// un paramètre <c>mut</c> (E014 pour un paramètre ordinaire), ou un tableau ou un dictionnaire dans le
    /// contenu d'une variable ou d'un paramètre <c>mut</c> (E013 sinon).
    /// </summary>
    /// <param name="syntax">La cible écrite, avant <c>.push</c>.</param>
    /// <param name="target">La cible liée.</param>
    /// <param name="method"><c>push</c> ou <c>add</c>.</param>
    private bool _CheckGrowTarget(IExpression syntax, IrExpression target, string method)
    {
        if (syntax is not NameExpression { Name: var name }) return _CheckContentTarget(syntax, target, $"agrandi par « {method} »");

        switch (IrMutation.Root(target))
        {
            case IrParameterReference { Parameter: { IsMutable: true } mutable }:
                return !_ReportCapturedParameter(name.Span, mutable);

            case IrParameterReference:
                _Error(name.Span, DiagnosticCodes.AppendToParameter, $"« {name.Name} » est un paramètre : « {method} » ne peut pas y ajouter", "paramètre",
                    $"un paramètre mut modifie la variable de l'appelant : (mut … {name.Name})",
                    $"sinon, copie-le dans une variable qui grandit, par exemple : var copie: {name.Name};");
                return false;

            case IrVariableReference { Variable: var variable }:
                if (_ReportCaptured(name, variable)) return false;
                if (!variable.IsConstant) return true;
                _Error(name.Span, DiagnosticCodes.AssignToConstant, $"« {name.Name} » est une constante : « {method} » ne peut pas y ajouter", "constante",
                    $"déclarée constante ligne {variable.Declaration.StartPosition.Line} : pour la faire grandir, déclare-la avec var ou avec son type");
                return false;

            default:
                return false;
        }
    }
}
