using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Train;
using Rgl.Text;

namespace Rgl.AST.Steps;

/// <summary>
/// Étape <c>^1</c>, <c>^1&lt;3&gt;</c> ou <c>^1&lt;!3&gt;</c> : revient au point de reprise
/// <c>^1</c> avec la valeur courante. Une fois sa limite atteinte, <c>&lt;3&gt;</c> laisse le chemin
/// continuer après elle, <c>&lt;!3&gt;</c> le fait échouer.
/// </summary>
/// <param name="Span">Du <c>^</c> à la fin de la limite.</param>
/// <param name="Number">Le numéro du point visé.</param>
/// <param name="Limit">La limite de reprises ; <see langword="null"/> sans limite.</param>
internal sealed record ResumeStep(SourceSpan Span, LiteralExpression Number, RepeatLimit? Limit) : IStep;
