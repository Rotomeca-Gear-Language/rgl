using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Un entier connu à la compilation (littéral déjà décodé : <c>1_000</c>, <c>0xFF</c>).</summary>
/// <param name="Span">Le littéral.</param>
/// <param name="Type">Son type : <c>int</c> pour un littéral.</param>
/// <param name="Value">Sa valeur.</param>
internal sealed record IrIntegerConstant(SourceSpan Span, IrType Type, long Value) : IrExpression(Span, Type);

/// <summary>Un flottant connu à la compilation.</summary>
/// <param name="Span">Le littéral.</param>
/// <param name="Value">Sa valeur ; de type <c>double</c>.</param>
internal sealed record IrFloatConstant(SourceSpan Span, double Value) : IrExpression(Span, PrimitiveType.Double);

/// <summary><c>true</c> ou <c>false</c>.</summary>
/// <param name="Span">Le littéral.</param>
/// <param name="Value">Sa valeur.</param>
internal sealed record IrBoolConstant(SourceSpan Span, bool Value) : IrExpression(Span, PrimitiveType.Bool);

/// <summary>Une chaîne connue à la compilation, échappements déjà décodés.</summary>
/// <param name="Span">Le littéral.</param>
/// <param name="Type"><c>string</c>, ou <c>mstring</c> pour un littéral <c>m"…"</c>.</param>
/// <param name="Value">Le texte décodé.</param>
internal sealed record IrStringConstant(SourceSpan Span, IrType Type, string Value) : IrExpression(Span, Type);

/// <summary>Un symbole interné <c>§nom</c>.</summary>
/// <param name="Span">Le littéral.</param>
/// <param name="Name">Le nom du symbole, sans <c>§</c>.</param>
internal sealed record IrSymbolConstant(SourceSpan Span, string Name) : IrExpression(Span, PrimitiveType.Symbol);

/// <summary>Une durée littérale (<c>100ms</c>, <c>1.5s</c>), ramenée à ses millisecondes.</summary>
/// <param name="Span">Le littéral.</param>
/// <param name="Milliseconds">Sa valeur en millisecondes.</param>
internal sealed record IrDurationConstant(SourceSpan Span, long Milliseconds) : IrExpression(Span, PrimitiveType.Duration);

/// <summary>Le littéral <c>null</c>.</summary>
/// <param name="Span">Le littéral.</param>
internal sealed record IrNullConstant(SourceSpan Span) : IrExpression(Span, NullType.Instance);
