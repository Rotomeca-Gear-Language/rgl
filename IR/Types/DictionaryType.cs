namespace Rgl.IR.Types;

/// <summary>
/// Un dictionnaire <c>[K:V]</c> : des valeurs rangées par clé. Il se lit mais ne se modifie pas ;
/// on y ajoute seulement des clés nouvelles.
/// </summary>
/// <param name="Key">Le type des clés : entier, bool, string, symbol, enum, ou ingot de l'un d'eux.</param>
/// <param name="Value">Le type des valeurs.</param>
internal sealed record DictionaryType(IrType Key, IrType Value) : IrType
{
    /// <inheritdoc/>
    public override string ToString() => $"[{Key}:{Value}]";
}
