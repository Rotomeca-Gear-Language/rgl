using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>Étape <c>done</c> : termine le train avec succès (code de sortie 0 pour le train d'entrée).</summary>
/// <param name="Span">Le mot <c>done</c>.</param>
internal sealed record DoneStep(SourceSpan Span) : IStep;
