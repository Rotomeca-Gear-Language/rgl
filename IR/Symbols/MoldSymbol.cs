using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>
/// Un mold : son nom (aucun pour un mold anonyme) et ses champs, dans l'ordre de déclaration
/// (spreads dépliés). Comparé par identité : deux types anonymes de mêmes champs partagent le
/// même symbole, ce qui en fait le même type.
/// </summary>
/// <param name="name">Nom du mold ; <see langword="null"/> pour un mold anonyme.</param>
/// <param name="declaration">Son nom dans sa déclaration, ou le type anonyme qui l'a créé.</param>
internal sealed class MoldSymbol(string? name, SourceSpan declaration, string? qualifiedName = null)
{
    /// <summary>Nom du mold ; <see langword="null"/> pour un mold anonyme.</summary>
    public string? Name { get; } = name;

    /// <summary>Son nom unique dans le programme (<c>Geometry.Vec2</c> pour un mold d'un fichier importé), d'où vient son nom C++ ; <see langword="null"/> pour un mold anonyme.</summary>
    public string? QualifiedName { get; } = qualifiedName ?? name;

    /// <summary>Son nom dans sa déclaration, ou le type anonyme qui l'a créé.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>Son nom en C++ : son nom unique, sans les points d'un nom qualifié ; <see langword="null"/> pour un mold anonyme.</summary>
    public string? CppName => QualifiedName?.Replace(".", "_d_");

    /// <summary>Ses champs, dans l'ordre (alphabétique pour un mold anonyme) ; fixés une fois la déclaration liée.</summary>
    public IReadOnlyList<MoldFieldSymbol> Fields { get; set; } = [];

    /// <summary>Le champ de ce nom, ou <see langword="null"/>.</summary>
    public MoldFieldSymbol? this[string field] => Fields.FirstOrDefault(f => f.Name == field);

    /// <summary>
    /// Vrai pour un type d'erreur : <c>Exception</c>, <c>CastError</c> ou une <c>error</c> déclarée, dont
    /// les deux premiers champs sont <c>message</c> et <c>where</c>. Il s'écrit en texte « message (where) ».
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>Vrai pour un <c>soa mold</c> : un layout en colonnes écrit tel quel, dont chaque champ est un tableau dynamique.</summary>
    public bool IsSoa { get; init; }

    /// <summary>Ses opérateurs et ses conversions déclarés dans son corps.</summary>
    public List<MoldOverload> Overloads { get; } = [];

    /// <summary>Vrai si la déclaration écrit un gear de fin (<c>mold Handle -&gt; fermer</c>), même avant qu'il soit trouvé : le type a déjà un cycle de vie.</summary>
    public bool DeclaresDestructor { get; set; }

    /// <summary>Son gear de fin, appelé quand une valeur du mold sort de portée ; <see langword="null"/> sans cycle de vie, ou tant qu'il n'est pas résolu.</summary>
    public GearSymbol? Destructor { get; set; }

    /// <summary>Vrai si une valeur de ce mold a un cycle de vie : un gear de fin, ou un champ qui en a un.</summary>
    public bool HasLifecycle => Types.LifecycleRules.HasMold(this);

    /// <summary>Vrai pour un mold anonyme (<c>{int x, int y}</c>).</summary>
    public bool IsAnonymous => Name is null;

    /// <summary>Classe C++ correspondant à une instance de descriptor générique.</summary>
    public string? NativeCppType { get; set; }

    public IReadOnlyList<IrType> NativeTypeArguments { get; set; } = [];

    /// <inheritdoc/>
    public override string ToString() => Name ?? "{" + string.Join(", ", Fields.Select(f => $"{f.Type} {f.Name}")) + "}";
}

/// <summary>
/// Un champ de mold. Sa valeur par défaut n'est pas dans l'IR : le binder la recopie dans chaque
/// valeur qui ne donne pas le champ.
/// </summary>
/// <param name="Name">Son nom.</param>
/// <param name="Type">Son type.</param>
/// <param name="HasDefault">Vrai s'il a une valeur par défaut (une constante connue à la compilation).</param>
/// <param name="Declaration">Son nom dans la déclaration.</param>
internal sealed record MoldFieldSymbol(string Name, IrType Type, bool HasDefault, SourceSpan Declaration);
