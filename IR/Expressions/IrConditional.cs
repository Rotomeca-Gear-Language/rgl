using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Le ternaire : une seule des deux valeurs est évaluée, selon la condition.</summary>
/// <param name="Span">Le ternaire dans le source.</param>
/// <param name="Type">Le type commun des deux valeurs.</param>
/// <param name="Condition">La condition, un bool.</param>
/// <param name="WhenTrue">La valeur si la condition est vraie, déjà du type commun.</param>
/// <param name="WhenFalse">La valeur si la condition est fausse, déjà du type commun.</param>
internal sealed record IrConditional(SourceSpan Span, IrType Type, IrExpression Condition, IrExpression WhenTrue, IrExpression WhenFalse) : IrExpression(Span, Type);
