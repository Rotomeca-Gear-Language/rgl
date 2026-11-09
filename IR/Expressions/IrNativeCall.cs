using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// L'appel d'une fonction C déclarée par un fichier <c>forge: native</c>, arguments déjà du type de ses paramètres. Le gear qui l'enveloppe est
/// celui que le reste du programme appelle.
/// </summary>
/// <param name="Span">L'appel dans le texte des liaisons.</param>
/// <param name="Type">Le type RGL de son résultat.</param>
/// <param name="Name">Le nom de la fonction C.</param>
/// <param name="Arguments">Les arguments, dans l'ordre d'évaluation.</param>
internal sealed record IrNativeCall(SourceSpan Span, IrType Type, string Name, IReadOnlyList<IrExpression> Arguments) : IrExpression(Span, Type);
