using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>Type attendu mais absent : l'erreur est déjà signalée.</summary>
/// <param name="Span">Span vide, à l'endroit où le type était attendu.</param>
internal sealed record MissingType(SourceSpan Span) : ITypeNode;
