using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Files;

/// <summary>
/// <c>namespace Mon/Name/Space;</c> : le nom sous lequel les extensions d'un fichier <c>forge: gears</c> se distinguent de celles d'un
/// autre fichier qui étendent le même type avec le même nom (<c>v.&lt;Mon/Name/Space&gt;f()</c>). Il ne sert qu'à cela.
/// </summary>
/// <param name="Span">De <c>namespace</c> au nom.</param>
/// <param name="Name">Le nom, segments séparés par <c>/</c>.</param>
internal sealed record NamespaceDeclaration(SourceSpan Span, string Name) : IStatement;
