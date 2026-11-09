using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary><c>return valeur;</c>, ou <c>return;</c> dans un gear <c>void</c>.</summary>
/// <param name="Span">L'instruction.</param>
/// <param name="Value">La valeur renvoyée ; <see langword="null"/> pour <c>return;</c>.</param>
internal sealed record ReturnStatement(SourceSpan Span, IExpression? Value) : IStatement;
