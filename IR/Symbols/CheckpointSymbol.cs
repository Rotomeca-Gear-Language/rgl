using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>
/// Un point de reprise <c>^N</c> du train : la valeur qui entre dans son étape y est rangée, et une
/// reprise y revient en la remplaçant. Comparé par identité, comme les autres symboles.
/// </summary>
/// <param name="number">Son numéro, unique dans le train.</param>
/// <param name="type">Le type de la valeur rangée : celui du paramètre de son étape.</param>
/// <param name="declaration">Le point dans le source.</param>
internal sealed class CheckpointSymbol(uint number, IrType type, SourceSpan declaration)
{
    /// <summary>Son numéro, unique dans le train.</summary>
    public uint Number { get; } = number;

    /// <summary>Le type de la valeur rangée : celui du paramètre de son étape.</summary>
    public IrType Type { get; } = type;

    /// <summary>Le point dans le source.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>
    /// Le nombre de reprises limitées (<c>^N&lt;M&gt;</c>) qui y reviennent : chacune compte les siennes,
    /// et son compteur repart de zéro quand le chemin passe par le point en avançant.
    /// </summary>
    public uint LimitedResumes { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"^{Number} : {Type}";
}
