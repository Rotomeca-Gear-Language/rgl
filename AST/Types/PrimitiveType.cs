using Rgl.AST.Interfaces;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>Un type primitif : <c>int</c>, <c>string</c>, <c>uint</c>…</summary>
/// <param name="Span">Le mot-clé du type.</param>
/// <param name="Keyword">Le mot-clé, par exemple <see cref="TokenKind.StringKeyword"/>.</param>
internal sealed record PrimitiveType(SourceSpan Span, TokenKind Keyword) : ITypeNode;
