using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Enums;

/// <summary>
/// Un enum : <c>enum Direction { North, South }</c>, ou avec un type et une valeur pour chaque
/// variante : <c>enum Status: int { Idle: 0, Running: 1 }</c>.
/// </summary>
/// <param name="Span">Du mot-clé à l'accolade fermante.</param>
/// <param name="Name">Le nom de l'enum.</param>
/// <param name="Underlying">Le type écrit après <c>:</c> ; <see langword="null"/> s'il est choisi par le compilateur.</param>
/// <param name="Variants">Les variantes, dans l'ordre.</param>
internal sealed record EnumDeclaration(SourceSpan Span, Identifier Name, ITypeNode? Underlying, IReadOnlyList<EnumVariant> Variants, Notes.DeclarationNotes? Notes = null) : IStatement;

/// <summary>Une variante d'enum, avec sa valeur si l'enum a un type écrit.</summary>
/// <param name="Span">La variante.</param>
/// <param name="Name">Son nom.</param>
/// <param name="Value">Sa valeur ; <see langword="null"/> si elle n'en a pas.</param>
internal sealed record EnumVariant(SourceSpan Span, Identifier Name, IExpression? Value) : IAstNode;
