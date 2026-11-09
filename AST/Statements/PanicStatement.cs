using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary>
/// <c>panic;</c>, <c>panic message;</c> ou <c>panic erreur;</c> : le programme s'arrête sur-le-champ,
/// avec le message et la position sur stderr. <c>panic</c> est un mot contextuel.
/// </summary>
/// <param name="Span">L'instruction.</param>
/// <param name="Message">Le texte ou l'erreur ; <see langword="null"/> pour un <c>panic;</c> seul.</param>
internal sealed record PanicStatement(SourceSpan Span, IExpression? Message) : IStatement;
