using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>Étape <c>$ valeur</c> : termine le train d'entrée avec ce code de sortie.</summary>
/// <param name="Span">Du <c>$</c> à la fin de la valeur.</param>
/// <param name="Code">Le code de sortie.</param>
internal sealed record ExitCodeStep(SourceSpan Span, IExpression Code) : IStep;
