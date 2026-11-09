using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// La limite d'une boucle de guard ou d'une reprise : <c>&lt;3&gt;</c> passe à la suite une fois
/// atteinte, <c>&lt;!3&gt;</c> échoue.
/// </summary>
/// <param name="Span">Des chevrons, eux compris.</param>
/// <param name="Count">Le nombre de tours ou de reprises permis.</param>
/// <param name="FailsWhenReached">Vrai pour <c>&lt;!N&gt;</c>.</param>
internal sealed record RepeatLimit(SourceSpan Span, LiteralExpression Count, bool FailsWhenReached) : IAstNode;
