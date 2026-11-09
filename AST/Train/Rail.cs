using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// Le rail d'erreur d'un guard : <c>!&gt; [reparer |&gt; $ 3]</c>, placé juste après lui. Il reçoit
/// l'erreur du guard dans sa première étape et termine le train ; ses guards peuvent avoir leurs
/// propres rails.
/// </summary>
/// <param name="Span">Du <c>!&gt;</c> au <c>]</c>.</param>
/// <param name="Steps">Les étapes du rail, la première sans <c>|&gt;</c> devant elle.</param>
internal sealed record Rail(SourceSpan Span, IReadOnlyList<IStep> Steps) : IAstNode;
