using Rgl.AST.Train;
using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Descriptors;

/// <summary>Ce qu'un membre de descriptor décrit : un constructeur, une méthode, une méthode statique ou un champ.</summary>
internal enum DescriptorMemberKind
{
    /// <summary><c>constructor(params) =&gt; Type(params);</c> : crée une valeur.</summary>
    Constructor,

    /// <summary><c>Nom(params): Type =&gt; $self.f($arg0);</c> : une méthode d'une instance.</summary>
    Method,

    /// <summary><c>static Nom(params): Type =&gt; Type::f($arg0);</c> : une méthode statique.</summary>
    Static,

    /// <summary><c>[const] field nom: Type =&gt; $self._nom;</c> : la valeur d'un champ.</summary>
    Field,
}

/// <summary>
/// Un membre d'un descriptor : sa signature RGL et l'expression C++ qui le réalise (<c>$self</c> est l'instance, <c>$arg0…</c> les arguments ; un nom seul est celui du
/// membre C++ : méthode appelée avec les arguments, ou champ lu).
/// </summary>
/// <param name="Span">Du premier mot à l'expression.</param>
/// <param name="Kind">Constructeur, méthode, statique ou champ.</param>
/// <param name="IsConst">Vrai pour <c>const field</c> : la valeur ne se réassigne pas côté RGL.</param>
/// <param name="Name">Son nom RGL (<c>constructor</c> pour un constructeur).</param>
/// <param name="Parameters">Ses paramètres RGL, dans l'ordre ; vide pour un champ.</param>
/// <param name="Type">Son type de retour (celui du champ) ; <see langword="null"/> pour un constructeur.</param>
/// <param name="Expression">L'expression C++ telle qu'écrite, sans le <c>;</c>.</param>
internal sealed record DescriptorMember(SourceSpan Span, DescriptorMemberKind Kind, bool IsConst, Identifier Name, IReadOnlyList<Parameter> Parameters, ITypeNode? Type, SourceSpan Expression) : IAstNode;

/// <summary>
/// <c>descriptor Counter -&gt; Compteur { … }</c> : donne à une classe C++ son identité RGL. Le type C++ est à gauche, le nom RGL à droite ; les membres disent comment
/// la classe se crée, se lit et s'appelle. Dans un fichier <c>forge: descriptor</c>.
/// </summary>
/// <param name="Span">Du mot <c>descriptor</c> à l'accolade fermante.</param>
/// <param name="CppType">Le type C++ tel qu'écrit (<c>ns::Counter</c>).</param>
/// <param name="Name">Le nom du type côté RGL.</param>
/// <param name="Members">Ses membres, dans l'ordre.</param>
internal sealed record DescriptorDeclaration(SourceSpan Span, SourceSpan CppType, Identifier Name, IReadOnlyList<DescriptorMember> Members, IReadOnlyList<Generics.TypeParameter>? TypeParameters = null) : IStatement;
