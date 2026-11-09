using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// Un template <c>`total = {a + b}`</c> : une chaîne évaluée à l'exécution, faite de morceaux de
/// texte et d'expressions interpolées, dans l'ordre.
/// </summary>
/// <param name="Span">Le template, accents graves compris.</param>
/// <param name="Parts">Ses morceaux ; les morceaux de texte vides sont omis.</param>
internal sealed record TemplateExpression(SourceSpan Span, IReadOnlyList<ITemplatePart> Parts) : IExpression;

/// <summary>Texte d'un template, tel qu'écrit : ses échappements (<c>\n</c>, <c>\{</c>) sont décodés par le binder.</summary>
/// <param name="Span">Le texte dans le source.</param>
internal sealed record TemplateText(SourceSpan Span) : ITemplatePart;

/// <summary>Une expression interpolée : <c>{a + b}</c>.</summary>
/// <param name="Span">Des accolades comprises.</param>
/// <param name="Expression">L'expression.</param>
internal sealed record TemplateInterpolation(SourceSpan Span, IExpression Expression) : ITemplatePart;
