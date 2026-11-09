using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// Le chemin principal d'un train : une suite d'étapes, chacune précédée de <c>|&gt;</c>
/// (<c>|&gt; positive |&gt; twice |&gt; done</c>).
/// </summary>
/// <param name="Span">Du premier <c>|&gt;</c> à la dernière étape.</param>
/// <param name="Steps">Les étapes, dans l'ordre ; la dernière termine le train.</param>
internal sealed record Pipeline(SourceSpan Span, IReadOnlyList<IStep> Steps) : IAstNode;
