using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Ingots;

/// <summary>
/// Un ingot : <c>ingot UserId: int;</c>, un type de même valeur que sa base mais d'une autre
/// identité, qui ne se mélange pas avec elle.
/// </summary>
/// <param name="Span">Du mot-clé au <c>;</c>.</param>
/// <param name="Name">Le nom de l'ingot.</param>
/// <param name="Base">Le type sur lequel il repose.</param>
internal sealed record IngotDeclaration(SourceSpan Span, Identifier Name, ITypeNode Base, Notes.DeclarationNotes? Notes = null) : IStatement;
