using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Molds;

/// <summary>
/// Un mold : <c>mold Vec3 { ...Vec2; float z: 0.0; }</c>. Ses membres sont des champs, avec ou sans
/// valeur par défaut, et des spreads qui embarquent les champs d'un autre mold.
/// </summary>
/// <param name="Span">Du mot-clé à l'accolade fermante.</param>
/// <param name="Name">Le nom du mold.</param>
/// <param name="Members">Ses membres, dans l'ordre d'écriture.</param>
/// <param name="TypeParameters">Ses paramètres génériques, <c>mold Pair&lt;T, U&gt;</c> ; <see langword="null"/> s'il n'est pas générique.</param>
/// <param name="IsError">Vrai pour un type d'erreur, <c>error NotFound { string path; }</c> : il reçoit d'office <c>message</c> et <c>where</c>.</param>
/// <param name="IsSoa">Vrai pour <c>soa mold</c> : un layout en colonnes écrit tel quel, dont chaque champ est un tableau <c>[T]</c>.</param>
/// <param name="Destructor">Le gear de fin, <c>mold Handle -&gt; fermer { … }</c> : il s'appelle tout seul quand une valeur du mold sort de portée ; <see langword="null"/> sans cycle de vie.</param>
internal sealed record MoldDeclaration(SourceSpan Span, Identifier Name, IReadOnlyList<IMoldMember> Members, bool IsError = false, IReadOnlyList<Generics.TypeParameter>? TypeParameters = null, Notes.DeclarationNotes? Notes = null, Identifier? Destructor = null, bool IsSoa = false) : IStatement;

/// <summary>Un membre d'un mold ou d'un type mold anonyme : un champ, ou le spread d'un autre mold.</summary>
internal interface IMoldMember : IAstNode;

/// <summary>Un champ : <c>float x;</c> ou <c>float z: 0.0;</c>.</summary>
/// <param name="Span">Le champ, sans son <c>;</c>.</param>
/// <param name="Type">Son type.</param>
/// <param name="Name">Son nom.</param>
/// <param name="Default">Sa valeur par défaut ; <see langword="null"/> s'il n'en a pas.</param>
internal sealed record MoldField(SourceSpan Span, ITypeNode Type, Identifier Name, IExpression? Default) : IMoldMember;

/// <summary><c>...Vec2;</c> : les champs d'un autre mold, embarqués sans héritage.</summary>
/// <param name="Span">Le spread.</param>
/// <param name="Source">Le mold dont les champs sont embarqués.</param>
internal sealed record MoldSpread(SourceSpan Span, ITypeNode Source) : IMoldMember;

/// <summary>
/// Un opérateur ou une conversion déclarés dans un mold : <c>Vec2 +(Vec2 a, Vec2 b) { … }</c>,
/// <c>bool eq(Vec2 a, Vec2 b) { … }</c>, <c>Vec3 implicit(Vec2 v) { … }</c>, <c>string explicit(Vec2 v) { … }</c>.
/// </summary>
/// <param name="Span">Du type renvoyé (ou de <c>pure</c>) à l'accolade fermante.</param>
/// <param name="IsPure">Vrai pour <c>pure Vec2 +(…)</c> : aucun effet de bord, il peut servir dans un gear pure.</param>
/// <param name="ReturnType">Le type renvoyé.</param>
/// <param name="Operator">L'opérateur tel qu'écrit (<c>+</c>, <c>eq</c>, <c>bitand</c>…), ou <c>implicit</c> ou <c>explicit</c>.</param>
/// <param name="OperatorSpan">Où l'opérateur est écrit.</param>
/// <param name="Parameters">Ses paramètres typés.</param>
/// <param name="Body">Son corps.</param>
/// <param name="IsMutable">Vrai pour l'écriture de l'opérateur <c>[]</c> (<c>mut T [int i] { … }</c>) : <c>this</c> est modifiable et <c>value</c> est la valeur écrite.</param>
internal sealed record MoldOperator(SourceSpan Span, bool IsPure, ITypeNode ReturnType, string Operator, SourceSpan OperatorSpan, IReadOnlyList<Train.Parameter> Parameters, Statements.BlockStatement Body, bool IsMutable = false) : IMoldMember;
