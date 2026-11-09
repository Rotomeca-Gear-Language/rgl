using Rgl.AST.Interfaces;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Un littéral : nombre, durée, chaîne, symbole, regex, <c>true</c>, <c>false</c> ou <c>null</c>.
/// Le texte reste brut (<c>"a\n"</c>, <c>1_000</c>) : sa valeur est calculée plus tard.
/// </summary>
/// <param name="Span">Le token du littéral.</param>
/// <param name="Kind">La sorte du token, par exemple <see cref="TokenKind.IntegerLiteral"/>.</param>
internal sealed record LiteralExpression(SourceSpan Span, TokenKind Kind) : IExpression
{
    /// <summary>Le littéral tel qu'écrit dans le source.</summary>
    public string Text => Span.Text;
}
