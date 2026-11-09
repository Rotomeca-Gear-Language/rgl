using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>
/// Le type d'une lambda : <c>(int, string) =&gt; bool</c>, <c>() =&gt; void</c>, ou
/// <c>pure (int) =&gt; int</c> pour une lambda sans effet.
/// </summary>
/// <param name="Span">Du <c>pure</c> ou de la parenthèse ouvrante au type de retour.</param>
/// <param name="Parameters">Les types des paramètres, dans l'ordre (un nom écrit à côté est ignoré).</param>
/// <param name="Return">Le type de retour, <c>void</c> si elle ne renvoie rien.</param>
/// <param name="IsPure">Vrai pour <c>pure (…) =&gt; T</c>.</param>
internal sealed record FunctionType(SourceSpan Span, IReadOnlyList<ITypeNode> Parameters, ITypeNode Return, bool IsPure) : ITypeNode;
