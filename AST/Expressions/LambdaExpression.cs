using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Une lambda : <c>(int v) =&gt; v &gt; seuil</c> ou <c>(string m) =&gt; { m -&gt; console; }</c>. Les
/// variables extérieures qu'elle lit sont capturées par copie, à sa création.
/// </summary>
/// <param name="Span">De la parenthèse ouvrante à la fin du corps.</param>
/// <param name="Parameters">Ses paramètres, dans l'ordre.</param>
/// <param name="ExpressionBody">Le corps expression ; <see langword="null"/> avec un bloc.</param>
/// <param name="Body">Le corps bloc ; <see langword="null"/> avec une expression.</param>
internal sealed record LambdaExpression(SourceSpan Span, IReadOnlyList<LambdaParameter> Parameters, IExpression? ExpressionBody, BlockStatement? Body) : IExpression;

/// <summary>
/// Un paramètre de lambda : <c>int v</c>, ou <c>v</c> seul quand le contexte donne son type ;
/// <c>mut p</c> pour modifier sur place l'élément d'un parcours.
/// </summary>
/// <param name="Span">Le paramètre, <c>mut</c> compris.</param>
/// <param name="Type">Son type ; <see langword="null"/> s'il vient du type attendu.</param>
/// <param name="Name">Son nom.</param>
/// <param name="IsMutable">Vrai pour <c>mut</c> : son contenu se modifie.</param>
internal sealed record LambdaParameter(SourceSpan Span, ITypeNode? Type, Identifier Name, bool IsMutable = false) : IAstNode;
