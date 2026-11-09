using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// <c>p with {…}</c> : une copie de <see cref="Source"/> rangée dans une variable <c>mut</c> de passage,
/// dont les changements modifient le contenu sur place ; la valeur est cette copie.
/// </summary>
/// <param name="Span">L'expression.</param>
/// <param name="Type">Le type de la valeur copiée.</param>
/// <param name="Source">La valeur copiée.</param>
/// <param name="Temp">La variable de passage qui reçoit la copie ; les cibles des changements partent d'elle.</param>
/// <param name="Changes">Les changements, dans l'ordre écrit.</param>
internal sealed record IrWith(SourceSpan Span, IrType Type, IrExpression Source, VariableSymbol Temp, IReadOnlyList<IrContentAssignment> Changes) : IrExpression(Span, Type);
