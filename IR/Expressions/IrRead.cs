using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary><c>&lt;- console</c> : une ligne lue sur la console, sans son retour à la ligne ; <c>""</c> en fin d'entrée.</summary>
/// <param name="Span">Le format lu dans le source.</param>
internal sealed record IrRead(SourceSpan Span) : IrExpression(Span, PrimitiveType.String);

/// <summary>
/// <c>&lt;- raw chemin</c> : tout le fichier, lu d'un coup — en <c>string</c> (UTF-8 strict, BOM retiré) ou, quand le
/// contexte attend des <c>bytes</c>, octet pour octet. Seulement dans un guard : un échec d'entrée-sortie envoie une <c>IoError</c>.
/// </summary>
/// <param name="Span">Le format et le chemin dans le source.</param>
/// <param name="Path">Le chemin du fichier, une <c>string</c>, relatif au dossier courant.</param>
/// <param name="Type">Le type lu : <c>string</c> ou <c>[ubyte]</c> (<c>bytes</c>).</param>
internal sealed record IrReadFile(SourceSpan Span, IrExpression Path, IrType Type) : IrExpression(Span, Type)
{
    /// <summary>Vrai quand le fichier est lu comme du texte.</summary>
    public bool IsText => Type is PrimitiveType;
}
