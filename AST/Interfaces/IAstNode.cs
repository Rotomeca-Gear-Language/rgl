using Rgl.Text;

namespace Rgl.AST.Interfaces;

/// <summary>
/// Un nœud de l'arbre syntaxique. Il ne garde que son sens et son span : les tokens et leurs
/// trivia restent dans la liste produite par le lexer.
/// </summary>
internal interface IAstNode
{
    /// <summary>
    /// Morceau du source couvert par le nœud, du début de son premier token à la fin du
    /// dernier. Vide pour un nœud manquant, à l'endroit où il était attendu.
    /// </summary>
    SourceSpan Span { get; }
}
