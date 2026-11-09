using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// Un template : la chaîne obtenue en écrivant ses morceaux à la suite, à l'exécution. Chaque
/// morceau est une chaîne, un nombre ou un bool ; un flottant s'écrit avec le texte le plus court
/// qui relit exactement sa valeur, toujours avec un point.
/// </summary>
/// <param name="Span">Le template dans le source.</param>
/// <param name="Parts">Ses morceaux dans l'ordre : le texte devient des constantes chaînes.</param>
internal sealed record IrTemplate(SourceSpan Span, IReadOnlyList<IrExpression> Parts) : IrExpression(Span, PrimitiveType.String);
