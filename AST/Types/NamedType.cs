using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>Un type désigné par son nom : moule, énumération… (<c>Profile</c>).</summary>
/// <param name="Name">Le nom du type.</param>
/// <param name="TypeArguments">Les arguments d'un type générique, <c>Pair&lt;int, string&gt;</c> ; <see langword="null"/> sans argument.</param>
/// <param name="End">La fin du type écrit (le <c>&gt;</c> des arguments) ; <see langword="null"/> sans argument.</param>
internal sealed record NamedType(Identifier Name, IReadOnlyList<ITypeNode>? TypeArguments = null, SourceSpan? End = null) : ITypeNode
{
    /// <inheritdoc/>
    public SourceSpan Span => End is { } end ? Name.Span.Union(end) : Name.Span;
}
