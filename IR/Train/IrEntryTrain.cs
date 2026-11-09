using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.Text;

namespace Rgl.IR.Train;

/// <summary>
/// Le train d'entrée : la signature <c>([string] args): uint</c>, sa valeur de départ et son chemin
/// principal, dont les guards portent leurs rails. Son résultat devient le code de sortie du programme.
/// </summary>
/// <param name="Span">Le train dans le source.</param>
/// <param name="Arguments">Le paramètre des arguments de la ligne de commande ; <see langword="null"/> si le train ne le déclare pas.</param>
/// <param name="StartValue">La valeur qui entre dans le pipeline ; <see langword="null"/> s'il part de <c>null</c>.</param>
/// <param name="Main">Le chemin principal.</param>
internal sealed record IrEntryTrain(
    SourceSpan Span,
    ParameterSymbol? Arguments,
    IrExpression? StartValue,
    IrPath Main) : IrStatement(Span)
{
    public bool IsUnsafe { get; set; }
}
