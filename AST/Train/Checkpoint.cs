using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// Un point de reprise posé devant une étape : <c>^1 lire</c>. Une reprise <c>^1</c> écrite plus
/// loin y revient, et l'étape reçoit alors la valeur apportée par la reprise.
/// </summary>
/// <param name="Span">Le <c>^</c> et son numéro.</param>
/// <param name="Number">Le numéro du point, unique dans le train.</param>
internal sealed record Checkpoint(SourceSpan Span, LiteralExpression Number) : IAstNode;
