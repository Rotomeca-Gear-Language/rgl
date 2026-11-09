using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// <c>Moves.find(nom)</c> : l'identifiant de l'entrée de ce nom, ou <c>null</c> s'il n'y en a pas. Le nom vient de l'exécution ; ceux écrits
/// dans le source sont des constantes (<c>Moves.attaquer</c>).
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Type">Le type du résultat : l'identifiant du dataset, nullable.</param>
/// <param name="Names">Les noms des entrées, dans l'ordre : la position de celui qu'on cherche est l'identifiant.</param>
/// <param name="Text">Le nom cherché, une chaîne.</param>
internal sealed record IrDatasetFind(SourceSpan Span, IrType Type, IrExpression Names, IrExpression Text) : IrExpression(Span, Type);
