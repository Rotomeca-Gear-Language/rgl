using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary><c>T ! E</c> : un résultat, succès de type T ou erreur de type E (comme le <c>Result</c> de Rust).</summary>
/// <param name="Span">Du type du succès au type de l'erreur.</param>
/// <param name="Value">Le type du succès.</param>
/// <param name="Error">Le type de l'erreur.</param>
internal sealed record ResultType(SourceSpan Span, ITypeNode Value, ITypeNode Error) : ITypeNode;
