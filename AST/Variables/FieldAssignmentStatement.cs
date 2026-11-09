using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.AST.Variables;

/// <summary>
/// <c>pos.x &lt;- 3.0;</c> ou <c>nums[0] &lt;- 9;</c> : l'affectation d'un champ ou d'un élément, qui
/// modifie le contenu d'une variable sur place. Seule une variable (ou un paramètre) <c>mut</c> le
/// permet : ailleurs, le binder la refuse (E013).
/// </summary>
/// <param name="Span">L'instruction.</param>
/// <param name="Target">Le champ (<see cref="Expressions.MemberAccessExpression"/>) ou l'élément (<see cref="Collections.IndexExpression"/>) visé.</param>
/// <param name="Operator"><see cref="TokenKind.Assign"/> ou une affectation composée.</param>
/// <param name="Value">La valeur affectée.</param>
internal sealed record FieldAssignmentStatement(SourceSpan Span, IExpression Target, TokenKind Operator, IExpression Value) : IStatement;
