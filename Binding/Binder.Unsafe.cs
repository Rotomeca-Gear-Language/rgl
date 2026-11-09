using Rgl.AST.Expressions;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    private bool _unsafeSignature;
    private bool _InUnsafe => _unsafeSignature || _currentGear is { DeclaredUnsafe: true } or { IsTrusted: true };

    private static bool _BorrowedPointer(IrExpression? value) => value switch
    {
        IrRawMemory { Operation: RawMemoryOperation.Address } => true,
        IrVariableReference reference => reference.Variable.IsBorrowedPointer,
        IrMove move => move.Source.Variable.IsBorrowedPointer,
        IrRawMemory { Operation: RawMemoryOperation.Transfer } transfer => _BorrowedPointer(transfer.Arguments[0]),
        IrBinary { Type: PointerType { Owned: false } } => true,
        _ => false,
    };

    private IrExpression? _BindPointerFree(SourceSpan span, IrExpression value)
    {
        if (!_RequireUnsafe(span)) return null;
        if (value is not IrVariableReference reference || value.Type is not PointerType { Element: not PrimitiveType { Kind: PrimitiveKind.Void } })
        {
            _Error(span, DiagnosticCodes.UnsafeRequired, "free exige une variable pointeur", "pointeur attendu");
            return null;
        }
        if (_ReportCaptured(new AST.Identifier(value.Span), reference.Variable)) return null;
        if (reference.Variable.IsBorrowedPointer)
        {
            _Error(span, DiagnosticCodes.UnsafeRequired, "cette adresse est empruntée : sa mémoire n'appartient pas au pointeur", "libération interdite");
            return null;
        }
        _scope.Release(reference.Variable.Name, span);
        return new IrRawMemory(span, PrimitiveType.Void, RawMemoryOperation.Delete, [value]);
    }

    private static bool _RawType(IrType type) => type is PointerType || type == PrimitiveType.Char
        || type is ArrayType array && _RawType(array.Element);

    private bool _RequireUnsafe(SourceSpan span)
    {
        if (_InUnsafe) return true;
        _Error(span, DiagnosticCodes.UnsafeRequired, "cette opération mémoire exige un gear unsafe", "hors de unsafe",
            "écris unsafe nom(...): Type { ... } ; les pointeurs ne sont pas des références gérées");
        return false;
    }

    private IrExpression? _BindRawUnary(UnaryExpression syntax, IrExpression value)
    {
        if (!_RequireUnsafe(syntax.Span)) return null;
        if (syntax.Operator == TokenKind.Ampersand)
        {
            if (LifecycleRules.Has(value.Type))
            {
                _Error(syntax.Span, DiagnosticCodes.UnsafeRequired, "une valeur à cycle de vie ne s'expose pas par pointeur brut", "cycle de vie");
                return null;
            }
            // Une adresse ne se prend jamais sur un temporaire, ni sur une donnée immuable.
            if (value is not IrVariableReference { Variable.IsConstant: false })
            {
                _Error(syntax.Span, DiagnosticCodes.UnsafeRequired, "une adresse modifiable exige une variable non constante", "adresse invalide");
                return null;
            }
            return new IrRawMemory(syntax.Span, new PointerType(value.Type, Owned: false), RawMemoryOperation.Address, [value]);
        }
        if (value.Type is not PointerType pointer)
        {
            _Error(syntax.Span, DiagnosticCodes.TypeMismatch, "un pointeur est attendu", "pas un ptr<T>");
            return null;
        }
        if (syntax.Operator == TokenKind.DeleteKeyword)
        {
            if (pointer.Element == PrimitiveType.Void || value is IrRawMemory { Operation: RawMemoryOperation.Address })
            {
                _Error(syntax.Span, DiagnosticCodes.UnsafeRequired, "delete exige l'adresse d'un objet alloué avec new", "pointeur non libérable");
                return null;
            }
            return _BindPointerFree(syntax.Span, value);
        }
        if (pointer.Element == PrimitiveType.Void)
        {
            _Error(syntax.Span, DiagnosticCodes.TypeMismatch, "ptr<void> ne se déréférence pas", "type pointé inconnu");
            return null;
        }
        return new IrRawMemory(syntax.Span, pointer.Element, RawMemoryOperation.Dereference, [value]);
    }

    private IrStatement? _BindRawStore(RawStoreStatement syntax)
    {
        if (!_AllowEffect(syntax.Span, DiagnosticCodes.ImpureCall, "modifier une valeur par pointeur brut")) return null;
        if (!_RequireUnsafe(syntax.Span)) return null;
        var target = _BindExpression(syntax.Target);
        if (target is not IrRawMemory { Operation: RawMemoryOperation.Dereference })
        {
            if (target is not null) _Error(syntax.Span, DiagnosticCodes.TypeMismatch, "l'écriture exige un pointeur déréférencé", "*p <- valeur");
            return null;
        }
        var value = _BindExpected(syntax.Value, target.Type);
        if (value is null) return null;
        value = _ConvertTo(value, target.Type, "valeur pointée");
        if (value is null) return null;
        return new IrExpressionStatement(syntax.Span, new IrRawMemory(syntax.Span, PrimitiveType.Void, RawMemoryOperation.Store, [target, value]));
    }

    private IrExpression? _BindRawBinary(SourceSpan span, TokenKind op, IrExpression left, IrExpression right)
    {
        if (!_RequireUnsafe(span)) return null;
        if (left.Type is PointerType { Element: var element } && element != PrimitiveType.Void && right.Type is PrimitiveType { IsInteger: true } && op is TokenKind.Plus or TokenKind.Minus)
            return new IrBinary(span, new PointerType(element, Owned: false), op == TokenKind.Plus ? IrBinaryOperator.Add : IrBinaryOperator.Subtract, left, right, span);
        if (left.Type is PointerType && right is IrNullConstant) right = new IrRetype(right.Span, left.Type, right);
        if (right.Type is PointerType && left is IrNullConstant) left = new IrRetype(left.Span, right.Type, left);
        if (left.Type is PointerType && right.Type == left.Type)
        {
            if (op == TokenKind.Minus) return new IrBinary(span, PrimitiveType.Long, IrBinaryOperator.Subtract, left, right, span);
            if (op == TokenKind.EqKeyword) return new IrBinary(span, PrimitiveType.Bool, IrBinaryOperator.Equal, left, right, span);
        }
        _Error(span, DiagnosticCodes.TypeMismatch, "opération de pointeurs incompatible", "opération brute");
        return null;
    }

    private void _PropagateUnsafe()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var (caller, calls) in _calls)
                if (!caller.IsUnsafe && !caller.IsTrusted && calls.Any(c => c.IsUnsafe)) { caller.IsUnsafe = true; changed = true; }
        } while (changed);

    }
}
