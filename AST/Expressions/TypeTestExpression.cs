using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Un test sur un type dans un corps générique : <c>T is MyMold</c> (le type exact) ou <c>T accept Vec2</c> (au moins les
/// champs de <c>Vec2</c>). Il vaut un booléen connu à la compilation, pour chaque instance du générique.
/// </summary>
/// <param name="Span">Du type testé à la fin du modèle.</param>
/// <param name="Subject">Le type testé, le plus souvent un paramètre générique.</param>
/// <param name="Pattern">Le type auquel on le compare.</param>
/// <param name="IsStructural">Vrai pour <c>accept</c>, faux pour <c>is</c>.</param>
internal sealed record TypeTestExpression(SourceSpan Span, ITypeNode Subject, ITypeNode Pattern, bool IsStructural) : IExpression;
