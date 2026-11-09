using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary><c>switch (valeur) { case … : … default: … }</c>.</summary>
/// <param name="Span">Du mot-clé à l'accolade fermante.</param>
/// <param name="Subject">La valeur testée, entre parenthèses ; une expression pure.</param>
/// <param name="Sections">Les cas, dans l'ordre d'écriture.</param>
internal sealed record SwitchStatement(SourceSpan Span, IExpression Subject, IReadOnlyList<SwitchSection> Sections) : IStatement;

/// <summary>La sorte d'un cas de <c>switch</c>.</summary>
internal enum SwitchLabelKind
{
    /// <summary><c>case v:</c> ou <c>case v1, v2:</c> : des valeurs constantes.</summary>
    Values,

    /// <summary><c>case eval condition:</c> : une condition où <c>$</c> désigne la valeur du switch.</summary>
    Eval,

    /// <summary><c>default:</c> : tout le reste.</summary>
    Default,
}

/// <summary>Un cas de <c>switch</c> et ses instructions, jusqu'au cas suivant.</summary>
/// <param name="Span">Du mot-clé à la fin de ses instructions.</param>
/// <param name="LabelSpan">Le libellé seul (<c>case 3, 4:</c>), pour les diagnostics.</param>
/// <param name="Kind">La sorte de cas.</param>
/// <param name="Values">Les valeurs d'un <c>case v:</c> ; vide pour les autres.</param>
/// <param name="Condition">La condition d'un <c>case eval</c> ; <see langword="null"/> sinon.</param>
/// <param name="Body">Ses instructions ; vide pour des <c>case</c> empilés.</param>
internal sealed record SwitchSection(
    SourceSpan Span,
    SourceSpan LabelSpan,
    SwitchLabelKind Kind,
    IReadOnlyList<IExpression> Values,
    IExpression? Condition,
    IReadOnlyList<IStatement> Body) : IAstNode;

/// <summary><c>break;</c>, <c>break N;</c> (sort aussi des N switch englobants) ou <c>break all;</c>.</summary>
/// <param name="Span">L'instruction.</param>
/// <param name="Depth">Le nombre N ; <see langword="null"/> pour <c>break</c> seul ou <c>break all</c>.</param>
/// <param name="All">Vrai pour <c>break all</c>.</param>
internal sealed record BreakStatement(SourceSpan Span, LiteralExpression? Depth, bool All) : IStatement;

/// <summary><c>goto v;</c> : saute au cas de valeur v du switch courant.</summary>
/// <param name="Span">L'instruction.</param>
/// <param name="Target">La valeur du cas visé.</param>
internal sealed record GotoStatement(SourceSpan Span, IExpression Target) : IStatement;
