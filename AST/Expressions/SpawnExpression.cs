using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// <c>spawn "make" "-j4"</c> ou <c>spawn("timeout=5000") "make"</c> : lance un processus enfant et donne un <c>ProcessResult ! SpawnError</c>. Chaque
/// élément est une chaîne ou le nom d'une variable <c>string</c> ; ce n'est qu'une instruction, ou la valeur d'une déclaration, d'une affectation ou
/// d'un <c>return</c> — jamais un argument ni une étape de pipeline.
/// </summary>
/// <param name="Span">De <c>spawn</c> au dernier élément.</param>
/// <param name="Options">La chaîne d'options entre parenthèses (<c>timeout=5000,cwd="./build"</c>) ; <see langword="null"/> sans options.</param>
/// <param name="Parts">L'exécutable, puis ses arguments.</param>
internal sealed record SpawnExpression(SourceSpan Span, IExpression? Options, IReadOnlyList<IExpression> Parts) : IExpression;
