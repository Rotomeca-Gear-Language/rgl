using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>
/// <c>p with {pos.x: 3, hp: p.hp - 1}</c> : une copie de la valeur dont certaines parties changent, sans
/// réécrire le mold. Chaque changement vise un chemin de champs et d'index qui part d'un champ de la valeur.
/// </summary>
/// <param name="Span">De la valeur à l'accolade fermante.</param>
/// <param name="Source">La valeur copiée.</param>
/// <param name="Changes">Les changements, dans l'ordre écrit.</param>
internal sealed record WithExpression(SourceSpan Span, IExpression Source, IReadOnlyList<WithChange> Changes) : IExpression;

/// <summary>Un changement d'un <see cref="WithExpression"/> : <c>cells[i]: State.J1</c>.</summary>
/// <param name="Span">Du chemin à la valeur.</param>
/// <param name="Path">Le chemin : un nom de champ, suivi d'accès <c>.champ</c> et <c>[index]</c>.</param>
/// <param name="Value">La nouvelle valeur.</param>
internal sealed record WithChange(SourceSpan Span, IExpression Path, IExpression Value) : IAstNode;
