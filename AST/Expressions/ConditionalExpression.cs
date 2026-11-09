using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Le ternaire <c>condition ? siVrai : siFaux</c>.</summary>
/// <param name="Span">De la condition à la valeur « si faux ».</param>
/// <param name="Condition">La condition.</param>
/// <param name="WhenTrue">La valeur si la condition est vraie.</param>
/// <param name="WhenFalse">La valeur si la condition est fausse.</param>
internal sealed record ConditionalExpression(SourceSpan Span, IExpression Condition, IExpression WhenTrue, IExpression WhenFalse) : IExpression;
