using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>Étape attendue mais absente (<c>train |&gt; ;</c>) : l'erreur est déjà signalée.</summary>
/// <param name="Span">Span vide, à l'endroit où l'étape était attendue.</param>
internal sealed record MissingStep(SourceSpan Span) : IStep;
