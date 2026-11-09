using Rgl.AST.Interfaces;
using Rgl.Text;
namespace Rgl.AST.Statements;
internal sealed record RawStoreStatement(SourceSpan Span, IExpression Target, IExpression Value) : IStatement;
