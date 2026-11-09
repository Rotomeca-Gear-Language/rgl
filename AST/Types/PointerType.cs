using Rgl.AST.Interfaces;
using Rgl.Text;
namespace Rgl.AST.Types;
internal sealed record PointerType(SourceSpan Span, ITypeNode Element, bool Owned = true) : ITypeNode;
