using Rgl.IR.Expressions;
using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary><c>return</c> : quitte le gear, avec sa valeur (déjà du type de retour) ou sans pour un gear <c>void</c>.</summary>
/// <param name="Span">L'instruction dans le source.</param>
/// <param name="Value">La valeur renvoyée ; <see langword="null"/> dans un gear <c>void</c>.</param>
/// <param name="IsDone">
/// Vrai pour un <c>done</c> de guard, qui renvoie son premier paramètre : il arrête en plus la boucle
/// de guard (<c>*</c>) qui l'appelle, là où un <c>return</c> la fait continuer.
/// </param>
/// <param name="StopsFold">Vrai pour <c>done valeur;</c> dans la lambda d'un <c>|+&gt;</c> : l'accumulation s'arrête avec cette valeur.</param>
internal sealed record IrReturn(SourceSpan Span, IrExpression? Value, bool IsDone = false, bool StopsFold = false) : IrStatement(Span);
