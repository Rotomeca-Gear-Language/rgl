using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Expression attendue mais absente (<c>$ ;</c>) : l'erreur est déjà signalée.</summary>
/// <param name="Span">Span vide, à l'endroit où l'expression était attendue.</param>
internal sealed record MissingExpression(SourceSpan Span) : IExpression;
