using Rgl.AST.Notes;

namespace Rgl.Lsp;

/// <summary>Les annotations déclarées par le programme (<c>annotation nom(…) = …;</c>), pour la complétion, le survol et l'accès à la définition.</summary>
internal sealed partial class SemanticDocument
{
    /// <summary>Les annotations déclarées dans le fichier, puis dans les autres fichiers du projet et de la std ; un nom n'y est qu'une fois.</summary>
    public IEnumerable<RglSymbol> AnnotationSymbols()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in new[] { Self }.Concat(Project.Modules).Concat(Project.Std ?? []))
        {
            foreach (var declaration in module.Unit.Statements.OfType<AnnotationDeclaration>().Where(a => !a.Name.IsMissing))
            {
                if (seen.Add(declaration.Name.Name))
                    yield return new RglSymbol(declaration.Name.Name, RglSymbolKind.Annotation, declaration.Name.Span, declaration, module, Documentation: module.DocumentationFor(declaration.Span));
            }
        }
    }
}
