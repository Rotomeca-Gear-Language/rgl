using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// <c>&lt;- xml chemin</c> : le document lu en un <c>XmlNode</c>, sa racine. Un fichier qui manque donne une <c>IoError</c>, un XML invalide
/// une <c>ParseError</c>. Seulement dans un guard.
/// </summary>
/// <param name="Span">Le format et le chemin dans le source.</param>
/// <param name="Path">Le chemin du fichier, une <c>string</c>.</param>
/// <param name="Type">Le type <c>XmlNode</c>.</param>
internal sealed record IrReadXml(SourceSpan Span, IrExpression Path, IrType Type) : IrExpression(Span, Type);
