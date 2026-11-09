using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Collections;

/// <summary>Un tableau écrit : <c>[1, 2, 3]</c>, <c>[...a, x]</c>, <c>[]</c>.</summary>
/// <param name="Span">Du <c>[</c> au <c>]</c>.</param>
/// <param name="Elements">Les éléments, dans l'ordre ; un <see cref="SpreadElement"/> étale un tableau.</param>
internal sealed record ArrayExpression(SourceSpan Span, IReadOnlyList<IExpression> Elements) : IExpression;

/// <summary><c>...a</c> dans un tableau écrit : tous les éléments de <c>a</c>.</summary>
/// <param name="Span">Du <c>...</c> à la fin de la valeur.</param>
/// <param name="Value">Le tableau étalé.</param>
internal sealed record SpreadElement(SourceSpan Span, IExpression Value) : IExpression;

/// <summary>Un dictionnaire écrit : <c>["a": 1, "b": 2]</c>, ou vide : <c>[:]</c>.</summary>
/// <param name="Span">Du <c>[</c> au <c>]</c>.</param>
/// <param name="Entries">Les paires, dans l'ordre d'écriture.</param>
internal sealed record DictionaryExpression(SourceSpan Span, IReadOnlyList<DictionaryEntry> Entries) : IExpression;

/// <summary>Une paire <c>clé: valeur</c> d'un dictionnaire écrit.</summary>
/// <param name="Span">La paire.</param>
/// <param name="Key">La clé.</param>
/// <param name="Value">La valeur.</param>
internal sealed record DictionaryEntry(SourceSpan Span, IExpression Key, IExpression Value) : IAstNode;

/// <summary>Un tuple écrit : <c>(1, "a")</c>, ou avec des noms : <c>(x: 1, nom: "a")</c>.</summary>
/// <param name="Span">Des parenthèses.</param>
/// <param name="Elements">Les éléments, au moins deux.</param>
internal sealed record TupleExpression(SourceSpan Span, IReadOnlyList<TupleElement> Elements) : IExpression;

/// <summary>Un élément d'un tuple écrit : sa valeur, et son nom s'il en a un.</summary>
/// <param name="Span">L'élément.</param>
/// <param name="Name">Son nom ; <see langword="null"/> sans nom.</param>
/// <param name="Value">Sa valeur.</param>
internal sealed record TupleElement(SourceSpan Span, Identifier? Name, IExpression Value) : IAstNode;

/// <summary><c>a[i]</c> : un élément de tableau par son index, ou une valeur de dictionnaire par sa clé.</summary>
/// <param name="Span">De la valeur au <c>]</c>.</param>
/// <param name="Target">Le tableau ou le dictionnaire.</param>
/// <param name="Index">L'index ou la clé.</param>
internal sealed record IndexExpression(SourceSpan Span, IExpression Target, IExpression Index) : IExpression;

/// <summary>
/// Un intervalle d'entiers : <c>[0..10]</c> (fin comprise), <c>[0..&lt;10]</c> (fin exclue),
/// <c>[0..10 by 2]</c> (avec un pas), <c>[10..0]</c> (décroissant). C'est un tableau comme un autre.
/// </summary>
/// <param name="Span">Du <c>[</c> au <c>]</c>.</param>
/// <param name="Start">La première valeur.</param>
/// <param name="End">La dernière valeur, ou celle juste après pour <c>..&lt;</c>.</param>
/// <param name="IsExclusive">Vrai pour <c>..&lt;</c> : la fin n'est pas dans l'intervalle.</param>
/// <param name="Step">Le pas écrit après <c>by</c> ; <see langword="null"/> sans pas.</param>
internal sealed record RangeExpression(SourceSpan Span, IExpression Start, IExpression End, bool IsExclusive, IExpression? Step) : IExpression;
