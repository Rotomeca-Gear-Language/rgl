using Rgl.AST.Interfaces;
using Rgl.Text;
namespace Rgl.AST.Expressions;

/// <summary>Un type générique utilisé comme cible d'un membre statique : Box&lt;int&gt;.create().</summary>
internal sealed record GenericTypeExpression(SourceSpan Span, IExpression Target, IReadOnlyList<ITypeNode> Arguments) : IExpression;
