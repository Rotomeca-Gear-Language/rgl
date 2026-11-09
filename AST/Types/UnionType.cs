using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary><c>int | string</c> : une valeur de l'un des types, résolue par <c>match</c>.</summary>
/// <param name="Span">Du premier au dernier type.</param>
/// <param name="Members">Les types, dans l'ordre d'écriture.</param>
internal sealed record UnionType(SourceSpan Span, IReadOnlyList<ITypeNode> Members) : ITypeNode;
