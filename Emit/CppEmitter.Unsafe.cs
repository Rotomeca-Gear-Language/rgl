using Rgl.IR.Expressions;
using Rgl.IR.Types;
namespace Rgl.Emit;

internal sealed partial class CppEmitter
{
    private string _RawMemory(IrRawMemory memory, Func<IrExpression, string> operand)
    {
        switch (memory.Operation)
        {
            case RawMemoryOperation.Allocate:
                _includes.Add("new");
                return $"{_Type(memory.Type)}(new (std::nothrow) {_Type(((PointerType)memory.Type).Element)}({string.Join(", ", memory.Arguments.Select(operand))}))";
            case RawMemoryOperation.Address: return $"{_Type(memory.Type)}(&{operand(memory.Arguments[0])})";
            case RawMemoryOperation.Dereference: return $"(*{operand(memory.Arguments[0])})";
            case RawMemoryOperation.Delete: return $"({operand(memory.Arguments[0])}).free()";
            case RawMemoryOperation.Store: return $"({operand(memory.Arguments[0])} = {operand(memory.Arguments[1])})";
            case RawMemoryOperation.Transfer: return $"{_Type(memory.Type)}(({operand(memory.Arguments[0])}).release())";
            case RawMemoryOperation.CharCast: return $"static_cast<{_Type(memory.Type)}>({operand(memory.Arguments[0])})";
            default: throw new InvalidOperationException("Opération mémoire inconnue.");
        }
    }
}
