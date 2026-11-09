using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

internal sealed record CppArgument(SourceSpan Span, ITypeNode Type, Identifier Name, IExpression Value) : IAstNode;
internal sealed record CppExpression(SourceSpan Span, SourceSpan Code, IReadOnlyList<CppArgument> Arguments, ITypeNode ReturnType) : IExpression;
