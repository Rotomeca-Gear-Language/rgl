using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>Déclaration d'une variable avec sa valeur initiale, déjà du type de la variable.</summary>
/// <param name="Span">La déclaration dans le source.</param>
/// <param name="Variable">La variable déclarée.</param>
/// <param name="Value">Sa valeur initiale.</param>
internal sealed record IrVariableDeclaration(SourceSpan Span, VariableSymbol Variable, IrExpression Value) : IrStatement(Span);

/// <summary>Nouvelle valeur d'une variable (jamais une constante), déjà du type de la variable.</summary>
/// <param name="Span">L'affectation dans le source.</param>
/// <param name="Variable">La variable affectée.</param>
/// <param name="Value">Sa nouvelle valeur.</param>
internal sealed record IrAssignment(SourceSpan Span, VariableSymbol Variable, IrExpression Value) : IrStatement(Span);

/// <summary>Libération d'une variable avant la fin de son bloc ; elle n'est plus utilisée ensuite.</summary>
/// <param name="Span">Le <c>undefine</c> dans le source.</param>
/// <param name="Variable">La variable libérée.</param>
internal sealed record IrUndefine(SourceSpan Span, VariableSymbol Variable) : IrStatement(Span);

/// <summary>Un bloc : ses variables sont détruites à sa fin.</summary>
/// <param name="Span">Le bloc dans le source.</param>
/// <param name="Statements">Ses instructions, dans l'ordre.</param>
/// <param name="IsArena">Vrai pour un bloc <c>arena</c> : une arène est ouverte pour sa durée.</param>
/// <param name="FirstSegment">La taille voulue du premier segment de l'arène, en octets ; 0 pour celle par défaut.</param>
internal sealed record IrBlock(SourceSpan Span, IReadOnlyList<IrStatement> Statements, bool IsArena = false, uint FirstSegment = 0) : IrStatement(Span);
