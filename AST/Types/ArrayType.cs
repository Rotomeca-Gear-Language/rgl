using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>Les sortes de tableaux : <c>[T]</c>, <c>[T;N]</c>, <c>[T;N..M]</c>, <c>[T+]</c>.</summary>
internal enum ArrayTypeKind
{
    /// <summary><c>[T]</c> : dynamique.</summary>
    Dynamic,

    /// <summary><c>[T;N]</c> : exactement N éléments.</summary>
    Fixed,

    /// <summary><c>[T;N..M]</c> : de N à M éléments.</summary>
    Bounded,

    /// <summary><c>[T+]</c> : au moins un élément.</summary>
    NonEmpty,
}

/// <summary>Un tableau : <c>[T]</c>, <c>[T;N]</c>, <c>[T;N..M]</c> ou <c>[T+]</c>.</summary>
/// <param name="Span">Du <c>[</c> au <c>]</c>.</param>
/// <param name="Element">Le type des éléments.</param>
/// <param name="Kind">La sorte de tableau.</param>
/// <param name="Size">N pour <c>[T;N]</c> et <c>[T;N..M]</c> ; <see langword="null"/> sinon.</param>
/// <param name="Max">M pour <c>[T;N..M]</c> ; <see langword="null"/> sinon.</param>
internal sealed record ArrayType(SourceSpan Span, ITypeNode Element, ArrayTypeKind Kind = ArrayTypeKind.Dynamic, IExpression? Size = null, IExpression? Max = null) : ITypeNode;
