using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>
/// <c>increment v</c>, <c>decrement v</c> et leurs formes <c>post_</c> : ajoute ou retire
/// <see cref="Step"/> à une variable numérique, puis vaut sa nouvelle valeur, ou son ancienne pour
/// une forme <c>post_</c>. C'est la seule expression qui modifie quelque chose : l'émetteur doit
/// en respecter l'ordre (de gauche à droite).
/// </summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Variable">La variable modifiée, jamais une constante.</param>
/// <param name="IsDecrement">Vrai pour retirer le pas, faux pour l'ajouter.</param>
/// <param name="IsPost">Vrai pour valoir l'ancienne valeur (<c>post_increment</c>).</param>
/// <param name="Step">Le pas, au moins 1 ; il tient dans le type de la variable.</param>
internal sealed record IrIncrement(SourceSpan Span, VariableSymbol Variable, bool IsDecrement, bool IsPost, long Step) : IrExpression(Span, Variable.Type);
