using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary><c>T?</c> : un type nullable, qui accepte aussi <c>null</c>.</summary>
/// <param name="Span">Du type au <c>?</c>.</param>
/// <param name="Inner">Le type rendu nullable.</param>
internal sealed record NullableType(SourceSpan Span, ITypeNode Inner) : ITypeNode;
