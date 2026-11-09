using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// Une valeur de mold construite champ par champ. <see cref="Fields"/> donne la valeur de chaque
/// champ du type, dans son ordre. Ce qui doit être évalué une seule fois et dans l'ordre d'écriture
/// (une valeur étalée par <c>...</c>, une valeur qui a un effet) est rangé dans <see cref="Parts"/>,
/// évaluées d'abord, de gauche à droite ; les champs y renvoient par <see cref="IrPart"/>.
/// </summary>
/// <param name="Span">La valeur dans le source.</param>
/// <param name="Mold">Le mold construit, type de la valeur.</param>
/// <param name="Parts">Les valeurs évaluées d'abord, dans l'ordre d'écriture.</param>
/// <param name="Fields">La valeur de chaque champ du mold, dans son ordre.</param>
/// <param name="ExplicitFields">
/// Pour une valeur écrite <c>{…}</c> dans le source, les champs écrits explicitement : elle peut encore
/// prendre le type attendu (un champ inconnu y est alors une erreur) ; <see langword="null"/> sinon.
/// </param>
internal sealed record IrMoldValue(
    SourceSpan Span,
    MoldType Mold,
    IReadOnlyList<IrExpression> Parts,
    IReadOnlyList<IrExpression> Fields,
    IReadOnlySet<string>? ExplicitFields) : IrExpression(Span, Mold);

/// <summary>Une valeur de <see cref="IrMoldValue.Parts"/>, déjà évaluée, lue par un champ.</summary>
/// <param name="Span">La valeur dans le source.</param>
/// <param name="Value">La valeur évaluée d'abord (comparée par identité).</param>
internal sealed record IrPart(SourceSpan Span, IrExpression Value) : IrExpression(Span, Value.Type);

/// <summary><c>valeur.champ</c> : un champ d'un mold, ou <c>message</c> et <c>where</c> d'une Exception.</summary>
/// <param name="Span">L'accès dans le source.</param>
/// <param name="Target">La valeur lue.</param>
/// <param name="Field">Le champ.</param>
internal sealed record IrMemberAccess(SourceSpan Span, IrExpression Target, MoldFieldSymbol Field) : IrExpression(Span, Field.Type);
