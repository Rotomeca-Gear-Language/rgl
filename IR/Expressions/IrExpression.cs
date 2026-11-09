using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Une expression de l'IR : elle a toujours un type résolu.</summary>
/// <param name="Span">L'expression dans le source.</param>
/// <param name="Type">Le type de sa valeur.</param>
internal abstract record IrExpression(SourceSpan Span, IrType Type);
