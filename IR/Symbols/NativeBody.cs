namespace Rgl.IR.Symbols;

/// <summary>Ce que réalise le C++ d'un guard ou d'un gear d'un descriptor.</summary>
internal enum NativeBodyKind
{
    /// <summary>Une expression C++ : <c>$self</c> est l'instance, <c>$arg0…</c> sont les arguments.</summary>
    Expression,

    /// <summary>La destruction de l'instance : <c>delete</c> de l'objet que la valeur RGL désigne.</summary>
    Destroy,
}

/// <summary>
/// Le C++ d'un membre de descriptor, écrit dans le fichier forge au lieu d'un fichier <c>.rgl.cpp</c>. Les valeurs RGL d'un type décrit par un descriptor sont des molds
/// qui portent l'adresse de l'objet C++ (<c>ulong handle</c>) : le C++ ne les voit que par cette adresse.
/// </summary>
/// <param name="Kind">Une expression, ou la destruction.</param>
/// <param name="Expression">L'expression C++, avec <c>$self</c> et <c>$argN</c> ; vide pour une destruction.</param>
/// <param name="SelfIndex">Le rang du paramètre qui est l'instance (<c>$self</c>) ; -1 pour un constructeur ou une méthode statique.</param>
/// <param name="Classes">Le type C++ de chaque type décrit du fichier, par nom RGL.</param>
/// <param name="Headers">Les chemins complets des en-têtes que l'unité C++ inclut.</param>
internal sealed record NativeBody(NativeBodyKind Kind, string Expression, int SelfIndex, IReadOnlyDictionary<string, string> Classes, IReadOnlyList<string> Headers, string? PublicName = null);
