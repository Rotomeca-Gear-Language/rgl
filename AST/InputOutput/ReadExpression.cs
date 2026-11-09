using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.InputOutput;

/// <summary>
/// La lecture d'une source, valeur d'une déclaration ou d'une affectation : <c>var ligne &lt;- console;</c>,
/// <c>var texte &lt;- raw "a.txt";</c>. Le format écrit juste après <c>&lt;-</c> en fait une lecture, et non une affectation.
/// </summary>
/// <param name="Span">Le format lu et son chemin.</param>
/// <param name="Source">Le format : <c>console</c>, <c>raw</c>…</param>
/// <param name="Path">Le chemin du fichier lu, pour un format de fichier ; <see langword="null"/> pour la console.</param>
/// <param name="Custom">Les arguments de <c>custom&lt;M, train&gt;</c> ; <see langword="null"/> pour les autres formats.</param>
internal sealed record ReadExpression(SourceSpan Span, Identifier Source, IExpression? Path = null, CustomFormat? Custom = null) : IExpression;
