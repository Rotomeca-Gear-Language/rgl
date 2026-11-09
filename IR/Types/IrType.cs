namespace Rgl.IR.Types;

/// <summary>
/// Un type RGL dans l'IR. Contrairement à l'AST, qui garde le type tel qu'il est écrit, l'IR ne
/// connaît que des types résolus : chaque expression en porte un.
/// </summary>
internal abstract record IrType
{
    /// <summary>Le type tel qu'on l'écrit en RGL (<c>int</c>, <c>[string]</c>), pour les messages.</summary>
    public abstract override string ToString();
}
