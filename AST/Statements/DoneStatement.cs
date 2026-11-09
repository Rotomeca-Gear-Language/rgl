using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Statements;

/// <summary>
/// <c>done;</c> dans un guard : la validation réussit, et le guard renvoie son premier paramètre. <c>done valeur;</c> dans la lambda
/// d'un <c>|+&gt;</c> : l'accumulation s'arrête, avec cette valeur pour résultat.
/// </summary>
/// <param name="Span">L'instruction.</param>
/// <param name="Value">La valeur de <c>done valeur;</c> ; <see langword="null"/> pour <c>done;</c>.</param>
internal sealed record DoneStatement(SourceSpan Span, IExpression? Value = null) : IStatement;
