using Rgl.IR.Expressions;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary><c>if</c> : <see cref="Then"/> si la condition est vraie, sinon <see cref="Else"/> (un bloc ou un autre <c>if</c>).</summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Condition">La condition, un bool.</param>
/// <param name="Then">Le bloc exécuté si elle est vraie.</param>
/// <param name="Else">Ce qui est exécuté sinon ; <see langword="null"/> sans <c>else</c>.</param>
internal sealed record IrIf(SourceSpan Span, IrExpression Condition, IrBlock Then, IrStatement? Else) : IrStatement(Span);

/// <summary>
/// <c>switch</c> : la valeur est évaluée une fois ; le premier cas dont le test est vrai (dans
/// l'ordre d'écriture, <c>default</c> en dernier) est exécuté, puis les suivants tant qu'aucun
/// <c>break</c> ni <c>goto</c> n'en sort.
/// </summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Id">Numéro unique du switch dans le programme, que visent <see cref="IrBreak"/> et <see cref="IrGoto"/>.</param>
/// <param name="Subject">La valeur testée, pure.</param>
/// <param name="Sections">Les cas, dans l'ordre d'écriture.</param>
/// <param name="CoversAll">Vrai si les cas couvrent toutes les variantes d'un enum : un cas est toujours choisi, même sans <c>default</c>.</param>
internal sealed record IrSwitch(SourceSpan Span, int Id, IrExpression Subject, IReadOnlyList<IrSwitchSection> Sections, bool CoversAll = false) : IrStatement(Span);

/// <summary>Un cas de switch.</summary>
/// <param name="Span">Le cas dans le source.</param>
/// <param name="Test">Le test, qui compare la valeur du switch (<see cref="IrSwitchValue"/>) ; <see langword="null"/> pour <c>default</c>.</param>
/// <param name="Body">Ses instructions, dans leur propre portée.</param>
internal sealed record IrSwitchSection(SourceSpan Span, IrExpression? Test, IrBlock Body);

/// <summary>Sort du switch <see cref="SwitchId"/> (et de tous ceux qu'il englobe).</summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="SwitchId">Le switch dont on sort.</param>
internal sealed record IrBreak(SourceSpan Span, int SwitchId) : IrStatement(Span);

/// <summary>Saute au cas <see cref="SectionIndex"/> du switch <see cref="SwitchId"/>.</summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="SwitchId">Le switch.</param>
/// <param name="SectionIndex">Le rang du cas visé dans <see cref="IrSwitch.Sections"/>.</param>
internal sealed record IrGoto(SourceSpan Span, int SwitchId, int SectionIndex) : IrStatement(Span);
