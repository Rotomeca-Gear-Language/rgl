using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.Text;

namespace Rgl.AST.Types;

/// <summary>Un type mold anonyme : <c>{int x, int y}</c>. Deux types anonymes de mêmes champs sont le même type.</summary>
/// <param name="Span">Des accolades comprises.</param>
/// <param name="Members">Ses champs (sans valeur par défaut) et ses spreads.</param>
internal sealed record AnonymousMoldType(SourceSpan Span, IReadOnlyList<IMoldMember> Members) : ITypeNode;
