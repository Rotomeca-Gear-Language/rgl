using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary><c>cancel promesse;</c> : demande l'annulation de la tâche que la promesse représente. <c>cancel</c> est un mot contextuel.</summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Operand">La promesse à annuler.</param>
internal sealed record CancelStatement(SourceSpan Span, IExpression Operand) : IStatement;
