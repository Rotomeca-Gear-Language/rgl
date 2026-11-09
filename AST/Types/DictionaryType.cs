using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>Un dictionnaire : <c>[K:V]</c>, par exemple <c>[string:int]</c>.</summary>
/// <param name="Span">Du <c>[</c> au <c>]</c>.</param>
/// <param name="Key">Le type des clés.</param>
/// <param name="Value">Le type des valeurs.</param>
internal sealed record DictionaryType(SourceSpan Span, ITypeNode Key, ITypeNode Value) : ITypeNode;
