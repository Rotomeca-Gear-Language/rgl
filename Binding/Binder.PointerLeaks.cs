using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    // Analyse conservatrice : une libération dans une branche ne prouve pas celle de l'autre.
    private void _CheckPointerLeaks(IrBlock body)
    {
        bool ContainsUptr(IrType type, HashSet<MoldSymbol>? seen = null)
        {
            seen ??= [];
            return type switch
            {
                PointerType { Owned: false } => true,
                PointerType pointer => ContainsUptr(pointer.Element, seen),
                ArrayType array => ContainsUptr(array.Element, seen),
                DictionaryType dictionary => ContainsUptr(dictionary.Key, seen) || ContainsUptr(dictionary.Value, seen),
                TupleType tuple => tuple.Elements.Any(t => ContainsUptr(t, seen)),
                IngotType ingot => ContainsUptr(ingot.Base, seen),
                MoldType mold when seen.Add(mold.Symbol) => mold.Symbol.Fields.Any(f => ContainsUptr(f.Type, seen)),
                _ => false,
            };
        }
        bool BorrowedOnly(IrExpression value) => _BorrowedPointer(value)
            || value is IrArrayValue array && array.Parts.All(p => BorrowedOnly(p.Value));
        var warned = new HashSet<VariableSymbol>();
        void Warn(IEnumerable<VariableSymbol> variables)
        {
            foreach (var variable in variables)
                if (warned.Add(variable))
                    _diagnostics.Warning(variable.Declaration, DiagnosticCodes.UnsafePointerLeak,
                        $"« {variable.Name} » contient un uptr qui peut sortir de portée sans être libéré", "mémoire possiblement non libérée",
                        ["appelle nom.free, écris undefine nom, ou transfère la propriété à un ptr avec :: ou <-|"]);
        }
        void Expression(IrExpression expression, HashSet<VariableSymbol> active)
        {
            foreach (var child in IrEffects.Children(expression)) Expression(child, active);
            if (expression is IrMove move) active.Remove(move.Source.Variable);
            if (expression is IrRawMemory { Operation: RawMemoryOperation.Delete, Arguments: [IrVariableReference released] })
            {
                if (released.Variable.Type is not PointerType { Owned: false } && active.Contains(released.Variable)) Warn([released.Variable]);
                active.Remove(released.Variable);
            }
        }
        bool Walk(IrStatement statement, HashSet<VariableSymbol> active)
        {
            switch (statement)
            {
                case IrBlock block:
                    var locals = new HashSet<VariableSymbol>();
                    foreach (var item in block.Statements)
                    {
                        if (item is IrVariableDeclaration local) locals.Add(local.Variable);
                        if (!Walk(item, active)) return false;
                    }
                    Warn(active.Intersect(locals));
                    active.ExceptWith(locals);
                    return true;
                case IrVariableDeclaration declaration:
                    Expression(declaration.Value, active);
                    if (ContainsUptr(declaration.Variable.Type) && !BorrowedOnly(declaration.Value)
                        && declaration.Value is not IrNullConstant and not IrRetype { Value: IrNullConstant })
                        active.Add(declaration.Variable);
                    return true;
                case IrAssignment assignment:
                    if (assignment.Value is IrVariableReference self && self.Variable == assignment.Variable) return true;
                    if (active.Contains(assignment.Variable)) Warn([assignment.Variable]);
                    Expression(assignment.Value, active);
                    if (ContainsUptr(assignment.Variable.Type))
                    {
                        if (!BorrowedOnly(assignment.Value) && assignment.Value is not IrNullConstant and not IrRetype { Value: IrNullConstant })
                            active.Add(assignment.Variable);
                        else active.Remove(assignment.Variable);
                    }
                    return true;
                case IrUndefine released:
                    if (released.Variable.Type is not PointerType { Owned: false } && active.Contains(released.Variable)) Warn([released.Variable]);
                    active.Remove(released.Variable);
                    return true;
                case IrExpressionStatement expression:
                    Expression(expression.Expression, active);
                    return true;
                case IrReturn returned:
                    if (returned.Value is { } value)
                    {
                        Expression(value, active);
                        if (value is IrVariableReference escaping) active.Remove(escaping.Variable);
                    }
                    Warn(active);
                    active.Clear();
                    return false;
                case IrIf branch:
                    Expression(branch.Condition, active);
                    if (Rgl.IR.ConstantFolder.Fold(branch.Condition) is bool known)
                        return known ? Walk(branch.Then, active) : branch.Else is null || Walk(branch.Else, active);
                    var yes = new HashSet<VariableSymbol>(active);
                    var no = new HashSet<VariableSymbol>(active);
                    var continuesYes = Walk(branch.Then, yes);
                    var continuesNo = branch.Else is null || Walk(branch.Else, no);
                    active.Clear();
                    if (continuesYes) active.UnionWith(yes);
                    if (continuesNo) active.UnionWith(no);
                    return continuesYes || continuesNo;
                case IrSwitch choice:
                    Expression(choice.Subject, active);
                    var possible = new HashSet<VariableSymbol>(choice.CoversAll ? [] : active);
                    foreach (var section in choice.Sections)
                    {
                        var state = new HashSet<VariableSymbol>(active);
                        if (Walk(section.Body, state)) possible.UnionWith(state);
                    }
                    active.Clear();
                    active.UnionWith(possible);
                    return true;
                default:
                    return true;
            }
        }
        Walk(body, []);
    }
}
