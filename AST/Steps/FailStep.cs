using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>Étape <c>fail [valeur]</c> : envoie la valeur sur le rail (code de sortie 1 pour le train d'entrée).</summary>
/// <param name="Span">Du mot <c>fail</c> à la fin de la valeur.</param>
/// <param name="Value">La valeur envoyée sur le rail ; <see langword="null"/> pour un <c>fail</c> seul.</param>
internal sealed record FailStep(SourceSpan Span, IExpression? Value) : IStep;
