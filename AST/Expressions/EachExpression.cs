using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Le parcours d'un tableau dans une expression : <c>nums ||&gt; double</c> applique l'étape à chaque
/// élément et garde ceux qui passent ; <c>nums |!|&gt; verifier</c> abandonne tout au premier échec.
/// Plusieurs parcours enchaînés (<c>a ||&gt; f ||&gt; g</c>) traitent chaque élément de bout en bout.
/// </summary>
/// <param name="Span">De la valeur à la fin de l'étape.</param>
/// <param name="Value">Le tableau parcouru (ou le parcours précédent).</param>
/// <param name="IsStrict">Vrai pour <c>|!|&gt;</c>.</param>
/// <param name="Target">Ce qui est appliqué à chaque élément.</param>
/// <param name="OperatorSpan">Le <c>||&gt;</c> ou le <c>|!|&gt;</c>.</param>
internal sealed record EachExpression(SourceSpan Span, IExpression Value, bool IsStrict, EachTarget Target, SourceSpan OperatorSpan) : IExpression;

/// <summary>
/// Ce qu'un parcours applique à chaque élément : un gear, un guard ou une variable lambda par son nom,
/// avec des arguments en plus (<c>scale(2)</c>, l'élément étant le premier), ou une lambda écrite sur
/// place (<c>(x) =&gt; x * 2</c>).
/// </summary>
/// <param name="Span">L'étape.</param>
/// <param name="Name">Le nom appliqué ; <see langword="null"/> pour une lambda.</param>
/// <param name="Arguments">Les arguments après l'élément ; vide sans parenthèses.</param>
/// <param name="Lambda">La lambda écrite sur place ; <see langword="null"/> pour un nom.</param>
internal sealed record EachTarget(SourceSpan Span, Identifier? Name, IReadOnlyList<IExpression> Arguments, LambdaExpression? Lambda) : IAstNode;
