using Rgl.Text;

namespace Rgl.IR.Statements;

/// <summary>Une instruction de l'IR, exécutée à sa place dans l'ordre du source.</summary>
/// <param name="Span">L'instruction dans le source.</param>
internal abstract record IrStatement(SourceSpan Span);
