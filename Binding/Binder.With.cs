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

namespace Rgl.Binding;

/// <summary>
/// <c>p with {pos.x: 3, cells[i]: State.J1}</c> (decisions : une copie, jamais une modification de
/// <c>p</c> ; chaque changement est l'affectation en place d'un chemin de la copie, avec les règles de
/// <c>a.b[i].c &lt;- v</c> — un index hors limites arrête le programme, ou fait échouer le guard ; les
/// changements s'appliquent dans l'ordre écrit, un champ peut être changé deux fois ; la valeur d'un changement
/// lit les variables d'avant, pas la copie).
/// </summary>
internal sealed partial class Binder
{
    /// <summary>
    /// Des morceaux de syntaxe qui valent une valeur déjà liée : le nom de champ au départ du chemin d'un
    /// changement vaut le champ de la copie, quels que soient les noms visibles.
    /// </summary>
    private readonly Dictionary<IExpression, IrExpression> _pathOverrides = new(ReferenceEqualityComparer.Instance);

    private IrExpression? _BindWith(WithExpression with)
    {
        var source = _BindExpression(with.Source);
        if (source is null)
        {
            foreach (var change in with.Changes) _BindExpression(change.Value);
            return null;
        }

        source = _Unbranded(source);
        if (source.Type is not (MoldType or TupleType))
        {
            _Error(with.Source.Span, DiagnosticCodes.TypeMismatch, $"with copie un mold ou un tuple, pas un « {source.Type} »", $"de type « {source.Type} »",
                source.Type is NullableType ? "la valeur peut être null : vérifie-la avant (if (… not eq null) { … })" : "par exemple : p with {x: 3.0}");
            foreach (var change in with.Changes) _BindExpression(change.Value);
            return null;
        }

        var temp = new VariableSymbol("with", source.Type, false, with.Span, null, isMutable: true);
        _lambda?.Locals.Add(temp);
        var root = new IrVariableReference(with.Source.Span, temp);

        var changes = new List<IrContentAssignment>();
        var failed = false;
        foreach (var change in with.Changes)
        {
            var bound = _BindChange(change, root);
            if (bound is null) failed = true;
            else changes.Add(bound);
        }
        return failed ? null : new IrWith(with.Span, source.Type, source, temp, changes);
    }

    /// <summary>Un changement : l'affectation du chemin, dont le premier champ est celui de la copie.</summary>
    private IrContentAssignment? _BindChange(WithChange change, IrVariableReference copy)
    {
        var leaf = change.Path;
        while (leaf is MemberAccessExpression or IndexExpression)
            leaf = leaf is MemberAccessExpression access ? access.Target : ((IndexExpression)leaf).Target;

        if (leaf is not NameExpression name)
        {
            _Error(change.Path.Span, DiagnosticCodes.TypeMismatch, "un changement vise un champ de la valeur copiée", "pas un chemin de champ",
                "par exemple : p with {pos.x: 3.0, cells[i]: State.J1}");
            _BindExpression(change.Value);
            return null;
        }

        var field = _FieldAccess(new MemberAccessExpression(name.Name.Span, name, name.Name), copy, copy.Type);
        if (field is null)
        {
            _BindExpression(change.Value);
            return null;
        }

        _pathOverrides[name] = field;
        try
        {
            var statement = _BindFieldAssignment(new FieldAssignmentStatement(change.Span, change.Path, TokenKind.Assign, change.Value));
            if (statement is IrContentAssignment assignment) return assignment;
            if (statement is not null) _Unsupported(change.Span, "un changement par un opérateur d'index surchargé");
            return null;
        }
        finally
        {
            _pathOverrides.Remove(name);
        }
    }
}
