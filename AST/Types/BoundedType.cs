using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary><c>int&lt;0..100&gt;</c> : un type numérique dont les valeurs restent entre deux bornes, comprises.</summary>
/// <param name="Span">Du type au <c>&gt;</c>.</param>
/// <param name="Base">Le type numérique.</param>
/// <param name="Min">La borne basse.</param>
/// <param name="Max">La borne haute.</param>
internal sealed record BoundedType(SourceSpan Span, PrimitiveType Base, IExpression Min, IExpression Max) : ITypeNode;
