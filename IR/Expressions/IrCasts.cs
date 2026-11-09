using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// La même valeur sous un autre type RGL, sans rien changer en C++ : une valeur qui entre dans un
/// ingot ou en sort (<c>UserId(42)</c>, <c>uid°</c>), ou un type borné vu comme sa base.
/// </summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Type">Le nouveau type.</param>
/// <param name="Value">La valeur.</param>
internal sealed record IrRetype(SourceSpan Span, IrType Type, IrExpression Value) : IrExpression(Span, Type);

/// <summary>
/// Une valeur qui entre dans un type borné : contrôlée à l'exécution. Hors des bornes, le programme
/// s'arrête, ou le guard qui l'évalue échoue.
/// </summary>
/// <param name="Span">La valeur dans le source : sa position est celle de l'échec.</param>
/// <param name="Target">Le type borné.</param>
/// <param name="Value">La valeur, du type non borné de même genre.</param>
internal sealed record IrBoundCheck(SourceSpan Span, PrimitiveType Target, IrExpression Value) : IrExpression(Span, Target);

/// <summary>
/// <c>v as T</c> ou <c>v as! T</c> entre deux types numériques. Un entier tronque un flottant vers
/// zéro. Contrôlé, un résultat hors du type arrête le programme, ou fait échouer le guard qui
/// l'évalue ; sinon, un entier trop grand perd ses bits de poids fort.
/// </summary>
/// <param name="Span">Le cast dans le source : sa position est celle de l'échec.</param>
/// <param name="Target">Le type visé, sans bornes.</param>
/// <param name="Value">La valeur convertie.</param>
/// <param name="Checked">Vrai si la valeur doit tenir dans le type.</param>
internal sealed record IrNumericCast(SourceSpan Span, PrimitiveType Target, IrExpression Value, bool Checked) : IrExpression(Span, Target);

/// <summary>
/// <c>v as? T</c> entre deux types numériques : la valeur convertie, ou <c>null</c> si elle ne
/// tient pas dans le type (ou dans ses bornes). Pour une valeur déjà nullable (un texte lu par
/// <c>as?</c>), seules les bornes sont contrôlées.
/// </summary>
/// <param name="Span">Le cast dans le source.</param>
/// <param name="Result">Le type visé, nullable.</param>
/// <param name="Value">La valeur convertie.</param>
internal sealed record IrTryCast(SourceSpan Span, NullableType Result, IrExpression Value) : IrExpression(Span, Result);

/// <summary>Les millisecondes d'une durée, ou celles depuis 1970 d'une date, en <c>long</c> (<c>d as int</c> passe par elles).</summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Value">La durée.</param>
internal sealed record IrDurationMilliseconds(SourceSpan Span, IrExpression Value) : IrExpression(Span, new PrimitiveType(PrimitiveKind.Long));

/// <summary>La durée de ces millisecondes (<c>n as duration</c>, après conversion de <c>n</c> en <c>long</c>).</summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Value">Les millisecondes, en <c>long</c>.</param>
internal sealed record IrDurationOf(SourceSpan Span, IrExpression Value) : IrExpression(Span, PrimitiveType.Duration);
