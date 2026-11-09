using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Une opération à un opérande.</summary>
/// <param name="Span">L'opération dans le source.</param>
/// <param name="Type">Le type du résultat.</param>
/// <param name="Operator">L'opérateur.</param>
/// <param name="Operand">L'opérande.</param>
internal sealed record IrUnary(SourceSpan Span, IrType Type, IrUnaryOperator Operator, IrExpression Operand) : IrExpression(Span, Type);

/// <summary>Une opération à deux opérandes, de types déjà vérifiés.</summary>
/// <param name="Span">L'opération dans le source.</param>
/// <param name="Type">Le type du résultat.</param>
/// <param name="Operator">L'opérateur.</param>
/// <param name="Left">L'opérande gauche.</param>
/// <param name="Right">L'opérande droit.</param>
/// <param name="OperatorSpan">L'opérateur dans le source : l'endroit signalé si l'opération échoue à l'exécution.</param>
internal sealed record IrBinary(SourceSpan Span, IrType Type, IrBinaryOperator Operator, IrExpression Left, IrExpression Right, SourceSpan OperatorSpan) : IrExpression(Span, Type);

/// <summary>
/// Une conversion ajoutée par le binder, absente du source : par exemple le littéral <c>0</c>
/// (un <c>int</c>) converti en <c>uint</c> pour devenir un code de sortie.
/// </summary>
/// <param name="Span">L'expression convertie dans le source.</param>
/// <param name="Type">Le type d'arrivée.</param>
/// <param name="Operand">L'expression convertie.</param>
internal sealed record IrConversion(SourceSpan Span, IrType Type, IrExpression Operand) : IrExpression(Span, Type);
