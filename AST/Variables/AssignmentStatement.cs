using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Variables;

/// <summary>
/// Affectation d'une nouvelle valeur à une variable existante : <c>score &lt;- 5;</c>, avec le
/// type de la cible rappelé : <c>score: int &lt;- 5;</c>, ou composée : <c>score +&lt;- 5;</c>.
/// </summary>
/// <param name="Span">De la cible au <c>;</c> final.</param>
/// <param name="Target">La variable affectée.</param>
/// <param name="TypeHint">Le type rappelé après le nom ; <see langword="null"/> s'il n'est pas écrit.</param>
/// <param name="Operator">
/// <see cref="Syntax.TokenKind.Assign"/> pour <c>&lt;-</c>, <see cref="Syntax.TokenKind.MoveAssign"/> pour
/// <c>&lt;-|</c> (la valeur est déplacée quand c'est possible), ou l'affectation composée :
/// <see cref="Syntax.TokenKind.PlusAssign"/> (<c>+&lt;-</c>)…
/// </param>
/// <param name="Value">La nouvelle valeur, ou l'opérande de droite d'une affectation composée.</param>
/// <param name="OperatorSpan">L'opérateur d'affectation dans le source : l'endroit signalé si le calcul d'une affectation composée échoue.</param>
internal sealed record AssignmentStatement(SourceSpan Span, Identifier Target, ITypeNode? TypeHint, Syntax.TokenKind Operator, IExpression Value, SourceSpan OperatorSpan) : IStatement;
