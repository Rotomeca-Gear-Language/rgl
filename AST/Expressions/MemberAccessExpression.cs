using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary><c>valeur.champ</c> : la lecture d'un champ ; <c>valeur?.champ</c> : <c>null</c> si la valeur l'est.</summary>
/// <param name="Span">De la valeur au nom du champ.</param>
/// <param name="Target">La valeur lue.</param>
/// <param name="Member">Le nom du champ.</param>
/// <param name="IsSafe">Vrai pour l'accès sûr <c>?.</c>.</param>
/// <param name="Namespace">Pour <c>valeur.&lt;Mon/Ns&gt;nom</c> : le namespace de l'extension visée ; <see langword="null"/> sinon.</param>
internal sealed record MemberAccessExpression(SourceSpan Span, IExpression Target, Identifier Member, bool IsSafe = false, string? Namespace = null) : IExpression;
