using Rgl.IR;

namespace Rgl.Build.Toolchain;

/// <summary>La famille d'un profil de build : ce que l'exécutable privilégie.</summary>
internal enum ProfileKind
{
    /// <summary>
    /// Le profil par défaut : le plus d'informations possible pour déboguer. Pas d'optimisation,
    /// informations de débogage complètes, détection des comportements indéfinis (débordement
    /// d'entier, décalage hors limites…) avec un message qui pointe vers la ligne RGL, et mode « durci » de la
    /// bibliothèque C++ (un index hors limites ou un pointeur nul dans un conteneur arrête le programme).
    /// </summary>
    Debug,

    /// <summary>
    /// <c>--release</c> : le plus rapide d'abord, puis le plus léger, sans jamais sacrifier la
    /// vitesse à la taille.
    /// </summary>
    Release,

    /// <summary><c>--release=size</c> : le plus petit exécutable, au prix de la vitesse (<c>-Oz</c>, sections inutilisées retirées).</summary>
    Size,
}

/// <summary>
/// Un profil de build : sa famille, un nom (le dossier <c>out/&lt;nom&gt;</c>) et, pour un profil du manifeste,
/// les options en plus de celles de sa famille. <c>debug</c>, <c>release</c> et <c>size</c> sont fournis ; les autres s'écrivent dans la
/// clé <c>profiles</c> de <c>rgl.json</c> et se demandent par <c>--profile nom</c>.
/// </summary>
/// <param name="Name">Le nom du profil, en minuscules : celui du dossier de sortie et de ceux du cache.</param>
/// <param name="Kind">La famille dont il part.</param>
/// <param name="CompileFlags">Les options de compilation en plus, après celles de la famille.</param>
/// <param name="LinkFlags">Les options d'édition de liens en plus.</param>
/// <param name="Overflow">Le mode de débordement des entiers ; <see langword="null"/> pour celui du manifeste.</param>
internal sealed record BuildProfile(string Name, ProfileKind Kind, IReadOnlyList<string> CompileFlags, IReadOnlyList<string> LinkFlags, OverflowMode? Overflow = null)
{
    /// <summary>Le profil par défaut (<c>--debug</c>).</summary>
    public static readonly BuildProfile Debug = new("debug", ProfileKind.Debug, [], []);

    /// <summary><c>--release</c>.</summary>
    public static readonly BuildProfile Release = new("release", ProfileKind.Release, [], []);

    /// <summary><c>--release=size</c>.</summary>
    public static readonly BuildProfile Size = new("size", ProfileKind.Size, [], []);

    /// <summary>Vrai pour tout profil qui optimise (tout sauf <c>debug</c>) : le mode de débordement de release, les sections inutilisées retirées.</summary>
    public bool IsOptimized => Kind != ProfileKind.Debug;

    /// <summary>
    /// Le même profil avec les compteurs de mémoire du runtime (<c>-DRGL_COUNT_ALLOCATIONS</c>), dans son propre dossier de sortie et son propre cache :
    /// un programme ordinaire ne les paie jamais.
    /// </summary>
    public BuildProfile WithCounters() => this with { Name = Name + "-compteurs", CompileFlags = [.. CompileFlags, "-DRGL_COUNT_ALLOCATIONS"] };

    /// <summary>Le profil fourni de ce nom (<c>debug</c>, <c>release</c>, <c>size</c>), ou <see langword="null"/>.</summary>
    public static BuildProfile? BuiltIn(string name) => name switch
    {
        "debug" => Debug,
        "release" => Release,
        "size" => Size,
        _ => null,
    };

    /// <summary>
    /// Identifie le profil pour le cache : le nom et toutes les options. Deux profils de même nom mais d'options différentes
    /// (le manifeste a changé) ne partagent pas leurs objets.
    /// </summary>
    public string Key => $"{Name}|{Kind}|{string.Join(' ', CompileFlags)}|{string.Join(' ', LinkFlags)}";

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <inheritdoc/>
    public bool Equals(BuildProfile? other) => other is not null && Key == other.Key && Overflow == other.Overflow;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Key, Overflow);
}
