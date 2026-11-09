namespace Rgl.IR.Symbols;

/// <summary>
/// Un opérateur ou une conversion déclarés dans un mold, appelés comme un gear.
/// </summary>
/// <param name="Operator">L'opérateur tel qu'écrit : <c>+</c>, <c>eq</c>, <c>bitand</c>…, ou <c>implicit</c> / <c>explicit</c> pour une conversion.</param>
/// <param name="Gear">Le gear qui l'implémente : ses paramètres sont les opérandes (ou la valeur convertie), son retour le résultat.</param>
internal sealed record MoldOverload(string Operator, GearSymbol Gear);
