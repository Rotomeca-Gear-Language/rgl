namespace Rgl.IR.Symbols;

/// <summary>Les trois sortes de gears.</summary>
internal enum GearKind
{
    /// <summary><c>gear</c> : une transformation.</summary>
    Gear,

    /// <summary><c>guard</c> : une validation, qui peut échouer (<c>fail</c>) ; il ne s'appelle que comme étape d'un train.</summary>
    Guard,

    /// <summary><c>emit</c> : la dernière étape d'un train.</summary>
    Emit,
}
