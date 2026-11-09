using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.InputOutput;

/// <summary><c>valeur -&gt; cible;</c> : écrit la valeur vers une cible, par exemple <c>console</c>.</summary>
/// <param name="Span">De la valeur au <c>;</c> final.</param>
/// <param name="Value">La valeur écrite.</param>
/// <param name="Target">La cible : un format (<c>console</c>, <c>raw</c>…).</param>
/// <param name="Path">Le chemin du fichier écrit, pour un format de fichier (<c>texte -&gt; raw "a.txt";</c>) ; <see langword="null"/> pour la console.</param>
/// <param name="Custom">Les arguments de <c>custom&lt;M, train&gt;</c> ; <see langword="null"/> pour les autres formats.</param>
internal sealed record WriteStatement(SourceSpan Span, IExpression Value, Identifier Target, IExpression? Path = null, CustomFormat? Custom = null) : IStatement;
