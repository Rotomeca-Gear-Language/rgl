using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// La boucle d'un guard, collée à son appel : <c>converge*</c>, <c>converge*&lt;10&gt;</c>,
/// <c>converge*&lt;!10&gt;</c>. Le guard reçoit à chaque tour la valeur qu'il vient de renvoyer,
/// jusqu'à un <c>done</c>, un <c>fail</c> ou la limite.
/// </summary>
/// <param name="Span">L'étoile et sa limite.</param>
/// <param name="Limit">La limite de tours ; <see langword="null"/> pour une boucle sans limite.</param>
internal sealed record Repeat(SourceSpan Span, RepeatLimit? Limit) : IAstNode;
