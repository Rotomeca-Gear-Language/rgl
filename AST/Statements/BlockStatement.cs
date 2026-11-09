using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary>
/// Un bloc <c>{ … }</c> : les variables qui y sont déclarées vivent jusqu'à son accolade fermante.
/// </summary>
/// <param name="Span">De l'accolade ouvrante à l'accolade fermante.</param>
/// <param name="Statements">Les instructions du bloc, dans l'ordre.</param>
/// <param name="IsArena">Vrai pour <c>arena { … }</c> : ce que le bloc crée est alloué dans une arène, rendue d'un coup à sa fin.</param>
/// <param name="ArenaSize">La taille voulue du premier segment de l'arène, en octets (<c>arena(65536) { … }</c>) ; <see langword="null"/> pour celle par défaut.</param>
internal sealed record BlockStatement(SourceSpan Span, IReadOnlyList<IStatement> Statements, bool IsArena = false, IExpression? ArenaSize = null) : IStatement;
