using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Variables;

/// <summary><c>undefine nom;</c> : libère une variable avant la fin de son bloc.</summary>
/// <param name="Span">Du mot-clé au <c>;</c> final.</param>
/// <param name="Name">La variable libérée.</param>
internal sealed record UndefineStatement(SourceSpan Span, Identifier Name) : IStatement;
