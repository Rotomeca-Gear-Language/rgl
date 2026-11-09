using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Une valeur d'un membre de l'union, mise dans l'union.</summary>
/// <param name="Span">La valeur.</param>
/// <param name="Union">L'union.</param>
/// <param name="Index">La position du membre dans l'union.</param>
/// <param name="Value">La valeur, déjà du type du membre.</param>
internal sealed record IrToUnion(SourceSpan Span, UnionType Union, int Index, IrExpression Value) : IrExpression(Span, Union);

/// <summary>Une union mise dans une union plus large, qui contient tous ses membres.</summary>
/// <param name="Span">La valeur.</param>
/// <param name="Union">L'union plus large.</param>
/// <param name="Value">La valeur de l'union plus étroite.</param>
internal sealed record IrUnionWiden(SourceSpan Span, UnionType Union, IrExpression Value) : IrExpression(Span, Union);

/// <summary>
/// Une union d'erreurs reçue par un type plus étroit : chaque membre est converti, un à un, en la
/// valeur attendue (duck typing : un mold qui perd des champs, ou le membre d'une autre union).
/// </summary>
/// <param name="Span">La valeur.</param>
/// <param name="Type">Le type attendu.</param>
/// <param name="Value">La valeur de l'union.</param>
/// <param name="Branches">Pour chaque membre de l'union, dans l'ordre, sa conversion, écrite sur un <see cref="IrUnionMemberValue"/>.</param>
internal sealed record IrUnionConvert(SourceSpan Span, IrType Type, IrExpression Value, IReadOnlyList<IrExpression> Branches) : IrExpression(Span, Type);

/// <summary>Le membre en cours de conversion d'un <see cref="IrUnionConvert"/>.</summary>
/// <param name="Span">La valeur.</param>
/// <param name="Type">Le type du membre.</param>
internal sealed record IrUnionMemberValue(SourceSpan Span, IrType Type) : IrExpression(Span, Type);

/// <summary><c>ok(v)</c> ou <c>err(e)</c> : un résultat, succès ou erreur.</summary>
/// <param name="Span">La valeur.</param>
/// <param name="Result">Le type du résultat ; un côté peut attendre d'être connu (<see cref="ResultType.IsPending"/>).</param>
/// <param name="IsOk">Vrai pour un succès.</param>
/// <param name="Value">La valeur du succès ou de l'erreur.</param>
internal sealed record IrResultValue(SourceSpan Span, ResultType Result, bool IsOk, IrExpression Value) : IrExpression(Span, Result);

/// <summary>Ce qu'un bras de <c>match</c> attrape.</summary>
internal enum IrMatchKind
{
    /// <summary>Un membre de l'union (<see cref="IrMatchArm.Index"/>).</summary>
    Member,

    /// <summary>Le succès d'un résultat.</summary>
    Ok,

    /// <summary>L'erreur d'un résultat.</summary>
    Err,

    /// <summary>Tout le reste.</summary>
    Default,

    /// <summary>Une valeur ou une condition sur la valeur du match (<see cref="IrMatchArm.Test"/>), comme un case de switch.</summary>
    Case,
}

/// <summary>Un bras de <c>match</c>.</summary>
/// <param name="Span">Le bras.</param>
/// <param name="Kind">Ce qu'il attrape.</param>
/// <param name="Index">La position du membre, pour <see cref="IrMatchKind.Member"/>.</param>
/// <param name="Binding">La variable qui reçoit la valeur attrapée ; <see langword="null"/> sans nom.</param>
/// <param name="Value">La valeur du bras (déjà du type du match) ; <see langword="null"/> pour un bras bloc.</param>
/// <param name="Body">Le bloc du bras ; <see langword="null"/> pour un bras valeur.</param>
/// <param name="Test">Pour <see cref="IrMatchKind.Case"/>, le test sur la valeur du match (<see cref="IrSwitchValue"/>).</param>
internal sealed record IrMatchArm(SourceSpan Span, IrMatchKind Kind, int Index, VariableSymbol? Binding, IrExpression? Value, IrBlock? Body, IrExpression? Test = null);

/// <summary>
/// <c>match</c> : le bras qui correspond à la valeur. Avec des bras valeur, le match vaut celle du bras
/// choisi ; avec un bras bloc, c'est une instruction (de type <c>void</c>).
/// </summary>
/// <param name="Span">Le match.</param>
/// <param name="Type">Le type commun des bras, ou <c>void</c>.</param>
/// <param name="Subject">La valeur résolue : une union ou un résultat.</param>
/// <param name="Arms">Les bras, dans l'ordre.</param>
/// <param name="SwitchId">Le numéro sous lequel les tests des bras <see cref="IrMatchKind.Case"/> lisent la valeur ; 0 pour un match de types.</param>
internal sealed record IrMatch(SourceSpan Span, IrType Type, IrExpression Subject, IReadOnlyList<IrMatchArm> Arms, int SwitchId = 0) : IrExpression(Span, Type);

/// <summary>
/// <c>valeur.champ</c> sur une union dont tous les membres sont des molds qui ont ce champ : le champ
/// du membre présent. Son type est celui du champ, ou l'union des types du champ s'ils diffèrent.
/// </summary>
/// <param name="Span">L'accès dans le source.</param>
/// <param name="Type">Le type du champ lu.</param>
/// <param name="Target">L'union lue.</param>
/// <param name="Fields">Le champ dans chaque membre, dans l'ordre des membres.</param>
internal sealed record IrUnionField(SourceSpan Span, IrType Type, IrExpression Target, IReadOnlyList<Symbols.MoldFieldSymbol> Fields) : IrExpression(Span, Type);
