using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// Une valeur nullable dont le binder a prouvé qu'elle n'est pas <c>null</c> à cet endroit (après
/// <c>if (x not eq null)</c>…) : elle s'utilise comme une valeur de son type.
/// </summary>
/// <param name="Span">La valeur dans le source.</param>
/// <param name="Value">La valeur nullable, une variable ou un paramètre.</param>
internal sealed record IrNonNull(SourceSpan Span, IrExpression Value) : IrExpression(Span, ((NullableType)Value.Type).Inner);

/// <summary>
/// <c>a ?? b</c> : <c>a</c> s'il n'est pas <c>null</c>, sinon <c>b</c>, évalué seulement dans ce cas.
/// Le résultat est du type de <c>a</c> sans nullable si <c>b</c> n'est pas nullable.
/// </summary>
/// <param name="Span">L'expression.</param>
/// <param name="Type">Son type, celui de <paramref name="Right"/>.</param>
/// <param name="Left">La valeur nullable.</param>
/// <param name="Right">La valeur de secours, déjà du type du résultat.</param>
internal sealed record IrCoalesce(SourceSpan Span, IrType Type, IrExpression Left, IrExpression Right) : IrExpression(Span, Type);

/// <summary>
/// <c>a ?° b</c> : <c>a</c> s'il n'est pas vide, sinon <c>b</c>, évalué seulement dans ce cas. Vide :
/// une chaîne <c>""</c>, un tableau sans élément, ou <c>null</c> pour un nullable.
/// </summary>
/// <param name="Span">L'expression.</param>
/// <param name="Type">Son type, celui de <paramref name="Right"/>.</param>
/// <param name="Left">La valeur testée : chaîne, tableau, ou leur nullable.</param>
/// <param name="Right">La valeur de secours, déjà du type du résultat.</param>
internal sealed record IrEmptyCoalesce(SourceSpan Span, IrType Type, IrExpression Left, IrExpression Right) : IrExpression(Span, Type);

/// <summary><c>valeur?.champ</c> : le champ, ou <c>null</c> si la valeur l'est ; le résultat est nullable.</summary>
/// <param name="Span">L'accès.</param>
/// <param name="Result">Le type du champ, rendu nullable.</param>
/// <param name="Target">La valeur nullable lue.</param>
/// <param name="Field">Le champ.</param>
internal sealed record IrSafeAccess(SourceSpan Span, NullableType Result, IrExpression Target, MoldFieldSymbol Field) : IrExpression(Span, Result);

/// <summary><c>texte as? int</c> : le nombre écrit dans la chaîne, ou <c>null</c> si elle n'en est pas entièrement un.</summary>
/// <param name="Span">Le cast.</param>
/// <param name="Result">Le type numérique visé, rendu nullable.</param>
/// <param name="Text">La chaîne lue.</param>
internal sealed record IrTryParseNumber(SourceSpan Span, NullableType Result, IrExpression Text) : IrExpression(Span, Result);
