namespace Rgl.IR.Types;

/// <summary>
/// <c>T ! E</c> : un succès de type T ou une erreur de type E, avec une étiquette (comme le
/// <c>Result</c> de Rust) : T et E peuvent être le même type.
/// </summary>
/// <param name="Value">Le type du succès.</param>
/// <param name="Error">Le type de l'erreur.</param>
internal sealed record ResultType(IrType Value, IrType Error) : IrType
{
    /// <summary>Vrai si un côté n'est pas encore connu : <c>ok(v)</c> attend le type de l'erreur de là où il va.</summary>
    public bool IsPending => Value is PendingType || Error is PendingType;

    /// <inheritdoc/>
    public override string ToString() => $"{Value} ! {Error}";
}

/// <summary>Le côté inconnu de <c>ok(v)</c> ou <c>err(e)</c> : le type attendu le donnera.</summary>
internal sealed record PendingType : IrType
{
    /// <summary>L'unique instance.</summary>
    public static readonly PendingType Instance = new();

    private PendingType() { }

    /// <inheritdoc/>
    public override string ToString() => "?";
}
