using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary><c>if (condition) { … } else { … }</c> ; <c>else if</c> est un <see cref="IfStatement"/> dans <see cref="Else"/>.</summary>
/// <param name="Span">Du mot-clé à la fin de la dernière branche.</param>
/// <param name="Condition">La condition, entre parenthèses.</param>
/// <param name="Then">Le bloc exécuté si elle est vraie.</param>
/// <param name="Else">Le bloc ou le <c>if</c> exécuté sinon ; <see langword="null"/> sans <c>else</c>.</param>
internal sealed record IfStatement(SourceSpan Span, IExpression Condition, BlockStatement Then, IStatement? Else) : IStatement;
