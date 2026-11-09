using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// <c>match (valeur) { int i |&gt; …; string s |&gt; …; default |&gt; …; }</c> : résout une union ou un
/// résultat ; <c>match (n) { case 1, 2 |&gt; …; case eval $ &gt; 10 |&gt; …; default |&gt; …; }</c> choisit, comme un
/// switch, selon la valeur de n. Chaque bras donne une valeur (le match est alors une expression), ou exécute un bloc.
/// </summary>
/// <param name="Span">Du mot-clé à l'accolade fermante.</param>
/// <param name="Subject">La valeur résolue.</param>
/// <param name="Arms">Les bras, dans l'ordre.</param>
internal sealed record MatchExpression(SourceSpan Span, IExpression Subject, IReadOnlyList<MatchArm> Arms) : IExpression;

/// <summary>Ce qu'un bras de <c>match</c> attrape.</summary>
internal enum MatchPatternKind
{
    /// <summary><c>int i</c> : un type de l'union.</summary>
    Type,

    /// <summary><c>ok v</c> : le succès d'un résultat.</summary>
    Ok,

    /// <summary><c>err e</c> : l'erreur d'un résultat.</summary>
    Err,

    /// <summary><c>default</c> : tout le reste.</summary>
    Default,

    /// <summary><c>case 1, 2</c> : une ou plusieurs valeurs constantes, comme un case de switch.</summary>
    Case,

    /// <summary><c>case eval $ &gt; 10</c> : une condition sur la valeur (<c>$</c>), comme un case eval de switch.</summary>
    Eval,
}

/// <summary>Un bras de <c>match</c> : son motif, puis une valeur ou un bloc.</summary>
/// <param name="Span">Le bras.</param>
/// <param name="Kind">Ce qu'il attrape.</param>
/// <param name="Type">Le type attrapé, pour un bras <see cref="MatchPatternKind.Type"/>.</param>
/// <param name="Name">Le nom donné à la valeur attrapée ; <see langword="null"/> s'il n'en a pas.</param>
/// <param name="Value">La valeur du bras ; <see langword="null"/> pour un bras bloc.</param>
/// <param name="Body">Le bloc du bras ; <see langword="null"/> pour un bras valeur.</param>
/// <param name="Values">Les valeurs d'un bras <see cref="MatchPatternKind.Case"/> ; <see langword="null"/> pour les autres.</param>
/// <param name="Condition">La condition d'un bras <see cref="MatchPatternKind.Eval"/> ; <see langword="null"/> pour les autres.</param>
internal sealed record MatchArm(SourceSpan Span, MatchPatternKind Kind, ITypeNode? Type, Identifier? Name, IExpression? Value, BlockStatement? Body,
    IReadOnlyList<IExpression>? Values = null, IExpression? Condition = null) : IAstNode;
