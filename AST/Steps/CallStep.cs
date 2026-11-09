using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>
/// Appel d'un gear dans un pipeline : <c>positive</c> ou <c>add(b)</c>. La valeur courante est
/// passée en premier argument, avant <see cref="Arguments"/>. Un point de reprise peut le précéder
/// (<c>^1 lire</c>), et un guard peut boucler (<c>converge*&lt;10&gt;</c>).
/// </summary>
/// <param name="Span">Du point de reprise ou du nom du gear à la fin de l'appel, boucle comprise.</param>
/// <param name="Gear">Le nom du gear.</param>
/// <param name="Arguments">Les arguments écrits entre parenthèses ; vide sans parenthèses.</param>
/// <param name="Rail">Le rail qui suit un guard (<c>!&gt; […]</c>) ; <see langword="null"/> sans rail.</param>
/// <param name="Checkpoint">Le point de reprise posé devant l'appel ; <see langword="null"/> sans point.</param>
/// <param name="Repeat">La boucle du guard ; <see langword="null"/> pour un seul appel.</param>
internal sealed record CallStep(
    SourceSpan Span,
    Identifier Gear,
    IReadOnlyList<IExpression> Arguments,
    Train.Rail? Rail = null,
    Train.Checkpoint? Checkpoint = null,
    Train.Repeat? Repeat = null) : IStep;
