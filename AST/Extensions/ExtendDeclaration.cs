using Rgl.AST.Generics;
using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Extensions;

/// <summary>
/// Une extension : <c>extend ProgramValue { const ProgramValue Valid: 0; gear double(this ProgramValue v): uint =&gt; … }</c>.
/// Elle ajoute à un type des constantes (<c>ProgramValue.Valid</c>) et des gears : un gear dont le premier paramètre est
/// marqué <c>this</c> s'appelle sur une valeur du type (<c>v.double()</c>), les autres sur le type (<c>ProgramValue.make(3)</c>).
/// Elle n'existe que pour le compilateur et seulement dans le fichier où elle est écrite.
/// </summary>
/// <param name="Span">De <c>extend</c> à l'accolade fermante.</param>
/// <param name="TypeParameters">Les paramètres génériques de <c>extend&lt;T: number&gt; [T] { … }</c> ; <see langword="null"/> sans.</param>
/// <param name="Target">Le type étendu.</param>
/// <param name="Members">Ses gears et ses constantes, dans l'ordre d'écriture.</param>
internal sealed record ExtendDeclaration(SourceSpan Span, IReadOnlyList<TypeParameter>? TypeParameters, ITypeNode Target, IReadOnlyList<IStatement> Members) : IStatement;
