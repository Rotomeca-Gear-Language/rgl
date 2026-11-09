namespace Rgl.IR;

/// <summary>Ce que fait une opération entière (<c>+</c>, <c>-</c>, <c>*</c>, négation) qui déborde de son type.</summary>
internal enum OverflowMode
{
    /// <summary>Le programme s'arrête avec un message qui donne l'endroit du débordement. Mode par défaut.</summary>
    Checked,

    /// <summary>Le résultat fait le tour du type (roulement) : <c>255 + 1</c> vaut 0 pour un <c>ubyte</c>.</summary>
    Unchecked,
}
