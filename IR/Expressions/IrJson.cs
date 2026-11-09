using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// <c>&lt;- json&lt;T&gt; chemin</c> : le fichier lu en JSON et converti en <c>T</c> champ par champ. Un fichier qui manque donne une
/// <c>IoError</c>, un JSON invalide ou qui ne correspond pas à <c>T</c> une <c>ParseError</c> qui porte le chemin de la valeur fautive.
/// Seulement dans un guard.
/// </summary>
/// <param name="Span">Le format et le chemin dans le source.</param>
/// <param name="Path">Le chemin du fichier, une <c>string</c>.</param>
/// <param name="Type">Le type lu.</param>
internal sealed record IrReadJson(SourceSpan Span, IrExpression Path, IrType Type) : IrExpression(Span, Type);

/// <summary>
/// <c>parseJson&lt;T&gt;(texte)</c> : un texte JSON converti en <c>T</c>, sans fichier ; un résultat <c>T ! ParseError</c>, jamais un échec de guard.
/// </summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Text">Le texte JSON, une <c>string</c>.</param>
/// <param name="Target">Le type lu.</param>
/// <param name="Type">Le résultat : <c>Target ! ParseError</c>.</param>
internal sealed record IrParseJson(SourceSpan Span, IrExpression Text, IrType Target, ResultType Result) : IrExpression(Span, Result);

/// <summary><c>toJson(valeur)</c> : la valeur écrite en JSON dans une <c>string</c> ; un résultat <c>string ! ParseError</c> (un flottant infini ou NaN n'a pas de JSON).</summary>
/// <param name="Span">L'appel dans le source.</param>
/// <param name="Value">La valeur à écrire.</param>
/// <param name="Result">Le résultat : <c>string ! ParseError</c>.</param>
internal sealed record IrToJson(SourceSpan Span, IrExpression Value, ResultType Result) : IrExpression(Span, Result);
