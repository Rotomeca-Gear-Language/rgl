using Rgl.IR.Types;
using Rgl.Text;
namespace Rgl.IR.Expressions;
internal enum RawMemoryOperation { Address, Dereference, Allocate, Delete, Store, CharCast, Transfer }
internal sealed record IrRawMemory(SourceSpan Span, IrType Type, RawMemoryOperation Operation, IReadOnlyList<IrExpression> Arguments) : IrExpression(Span, Type);
