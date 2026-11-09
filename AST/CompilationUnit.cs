using Rgl.AST.Files;
using Rgl.AST.Interfaces;
using Rgl.AST.Train;
using Rgl.Text;

namespace Rgl.AST;

/// <summary>Racine de l'arbre : un fichier source entier.</summary>
/// <param name="Span">Tout le fichier.</param>
/// <param name="Statements">
/// Les instructions du fichier dans l'ordre du source : des déclarations de variables et au plus
/// un train, les imports, et pour un fichier <c>forge: train</c> des blocs de trains
/// (<see cref="TrainBlock"/>). Vide pour un fichier sans instruction.
/// </param>
/// <param name="Header">L'en-tête <c>forge:</c> ; <see langword="null"/> sans en-tête.</param>
internal sealed record CompilationUnit(SourceSpan Span, IReadOnlyList<IStatement> Statements, FileHeader? Header = null) : IAstNode
{
    /// <summary>Le train du fichier ; <see langword="null"/> s'il n'en contient pas.</summary>
    public TrainDeclaration? Train => Statements.OfType<TrainDeclaration>().FirstOrDefault();

    /// <summary>Les imports du fichier, dans l'ordre.</summary>
    public IEnumerable<ImportDeclaration> Imports => Statements.OfType<ImportDeclaration>();
}
