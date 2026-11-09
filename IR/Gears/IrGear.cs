using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Gears;

/// <summary>
/// Un gear et son corps. Un corps expression (<c>=&gt; a + b</c>) devient un bloc qui renvoie sa
/// valeur. Un gear qui renvoie une valeur ne peut jamais atteindre la fin de son corps.
/// </summary>
/// <param name="Span">La déclaration dans le source.</param>
/// <param name="Symbol">Sa signature.</param>
/// <param name="Body">Son corps.</param>
/// <param name="ImplicitDone">Vrai pour un guard qui peut atteindre la fin de son corps : il fait alors un <c>done</c> implicite (W007).</param>
/// <param name="Defaults">Les valeurs par défaut écrites (expressions), dans l'ordre des paramètres, liées avec les seuls paramètres qui les précèdent ; <see langword="null"/> sans.</param>
/// <param name="Overflow">Le mode de débordement du corps quand le gear porte <c>@checked</c> ou <c>@unchecked</c> ; <see langword="null"/> pour celui du programme.</param>
/// <param name="Taps">Les <c>@tap</c> du gear, dans l'ordre d'écriture ; <see langword="null"/> sans.</param>
internal sealed record IrGear(SourceSpan Span, GearSymbol Symbol, IrBlock Body, bool ImplicitDone = false, IReadOnlyList<IrParameterDefault>? Defaults = null, IReadOnlyList<IrTap>? Taps = null, OverflowMode? Overflow = null);

/// <summary>Un <c>@tap</c> : une instruction qui s'exécute avant le gear, ou après, avec la valeur qu'il renvoie.</summary>
/// <param name="Span">L'annotation.</param>
/// <param name="IsAfter">Vrai pour un tap qui s'exécute après le gear.</param>
/// <param name="Statement">L'instruction.</param>
internal sealed record IrTap(SourceSpan Span, bool IsAfter, IR.Statements.IrStatement Statement);

/// <summary>La valeur par défaut d'un paramètre, évaluée dans le gear appelé quand l'appel ne le donne pas.</summary>
/// <param name="Parameter">Le paramètre.</param>
/// <param name="Value">Sa valeur, du type du paramètre.</param>
internal sealed record IrParameterDefault(ParameterSymbol Parameter, IR.Expressions.IrExpression Value);
