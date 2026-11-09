using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary>
/// <c>fail;</c> ou <c>fail message;</c> dans un guard : le guard s'arrête et envoie une Exception
/// sur le rail du train.
/// </summary>
/// <param name="Span">L'instruction.</param>
/// <param name="Message">Le message de l'Exception ; <see langword="null"/> pour un <c>fail;</c> seul.</param>
internal sealed record FailStatement(SourceSpan Span, IExpression? Message) : IStatement;
