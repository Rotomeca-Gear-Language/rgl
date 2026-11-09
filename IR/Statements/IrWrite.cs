using Rgl.IR.Expressions;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>
/// <c>valeur -&gt; console;</c> : écrit la valeur sur la console, telle quelle, sans retour à la
/// ligne. Une chaîne s'écrit directement ; un nombre, un bool, un symbole ou une Exception comme
/// dans un template.
/// </summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Value">La valeur écrite.</param>
internal sealed record IrWrite(SourceSpan Span, IrExpression Value) : IrStatement(Span);

/// <summary>
/// <c>valeur -&gt; raw chemin;</c> : écrit toute la valeur — une <c>string</c> en UTF-8, ou des <c>bytes</c> — dans le fichier, créé ou
/// remplacé. Seulement dans un guard : un échec d'entrée-sortie envoie une <c>IoError</c>.
/// </summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Value">La valeur écrite : une <c>string</c> ou un tableau de <c>ubyte</c>.</param>
/// <param name="Path">Le chemin du fichier, une <c>string</c>, relatif au dossier courant.</param>
internal sealed record IrWriteFile(SourceSpan Span, IrExpression Value, IrExpression Path) : IrStatement(Span);

/// <summary>
/// <c>valeur -&gt; json chemin;</c> : la valeur écrite en JSON (indenté de deux espaces) dans le fichier, créé ou remplacé. Un nombre
/// infini ou NaN donne une <c>ParseError</c>. Seulement dans un guard.
/// </summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Value">La valeur écrite.</param>
/// <param name="Path">Le chemin du fichier, une <c>string</c>.</param>
internal sealed record IrWriteJson(SourceSpan Span, IrExpression Value, IrExpression Path) : IrStatement(Span);

/// <summary>
/// <c>noeud -&gt; xml chemin;</c> : l'<c>XmlNode</c> écrit en XML (indenté de deux espaces) dans le fichier, créé ou remplacé. Un nom de balise
/// ou d'attribut qui n'en est pas un donne une <c>ParseError</c>. Seulement dans un guard.
/// </summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Value">Le nœud écrit.</param>
/// <param name="Path">Le chemin du fichier, une <c>string</c>.</param>
internal sealed record IrWriteXml(SourceSpan Span, IrExpression Value, IrExpression Path) : IrStatement(Span);
