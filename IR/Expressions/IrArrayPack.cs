using Rgl.IR.Types;
using Rgl.Text;
namespace Rgl.IR.Expressions;
internal sealed record IrArrayPack(SourceSpan Span, ArrayType Array, IrExpression Value) : IrExpression(Span, Array);
