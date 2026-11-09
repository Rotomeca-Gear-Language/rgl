using Rgl.IR.Expressions;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>
/// <c>panic</c> : le programme s'arrête avec le code 101, son message et la position du <c>panic</c>
/// sur stderr. Comme <c>return</c>, il ne laisse jamais l'exécution passer à la suite, et il est permis
/// partout (même dans un gear <c>pure</c>).
/// </summary>
/// <param name="Span">L'instruction dans le source : sa position est écrite avec le message.</param>
/// <param name="Value">Un texte, ou une erreur (un mold d'erreur écrit comme un texte).</param>
internal sealed record IrPanic(SourceSpan Span, IrExpression Value) : IrStatement(Span);
