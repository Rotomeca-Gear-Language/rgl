using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.AST.Train;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.AST.Gears;

/// <summary>
/// Un gear : <c>gear [pure] nom(paramètres): Type { … }</c>, ou avec un corps expression
/// <c>gear nom(paramètres): Type =&gt; expression;</c>. Un guard (<c>guard</c>, qui peut échouer) et
/// un emit (<c>emit</c>, dernière étape d'un train) s'écrivent de la même façon.
/// </summary>
/// <param name="Span">Du mot-clé à la fin du corps.</param>
/// <param name="Keyword">Le mot-clé : <see cref="TokenKind.GearKeyword"/>, <see cref="TokenKind.GuardKeyword"/> ou <see cref="TokenKind.EmitKeyword"/>.</param>
/// <param name="IsPure">Vrai pour <c>gear pure</c> : aucun effet de bord.</param>
/// <param name="Name">Le nom du gear.</param>
/// <param name="Parameters">Ses paramètres, dans l'ordre.</param>
/// <param name="ReturnType">Le type de retour, toujours écrit (<c>void</c> s'il ne renvoie rien).</param>
/// <param name="Body">Le corps bloc ; <see langword="null"/> pour un corps expression.</param>
/// <param name="TypeParameters">Ses paramètres génériques, <c>gear clamp&lt;T: number&gt;(…)</c> ; <see langword="null"/> s'il n'est pas générique.</param>
/// <param name="ExpressionBody">Le corps expression (<c>=&gt; a + b</c>) ; <see langword="null"/> pour un corps bloc.</param>
internal sealed record GearDeclaration(
    SourceSpan Span,
    TokenKind Keyword,
    bool IsPure,
    Identifier Name,
    IReadOnlyList<Parameter> Parameters,
    ITypeNode ReturnType,
    BlockStatement? Body,
    IExpression? ExpressionBody,
    IReadOnlyList<Generics.TypeParameter>? TypeParameters = null,
    Notes.DeclarationNotes? Notes = null) : IStatement
{
    /// <summary>
    /// Vrai pour un gear sans corps (<c>guard normalize(string s): string;</c>) : son implémentation est le C++ d'un fichier <c>.rgl.cpp</c> du même nom,
    /// écrit à côté du fichier forge.
    /// </summary>
    public bool IsNative => Body is null && ExpressionBody is null;
}
