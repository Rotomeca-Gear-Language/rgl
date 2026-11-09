using Rgl.Text;

namespace Rgl.Docs;

/// <summary>
/// Le contenu d'une balise : son XML intérieur tel qu'il s'exporte, et son rendu en Markdown pour l'éditeur.
/// </summary>
/// <param name="Xml">Le XML intérieur de la balise.</param>
/// <param name="Markdown">Le même contenu en Markdown.</param>
/// <param name="Span">La balise ouvrante dans le source (le commentaire entier pour un résumé sans balise).</param>
internal sealed record DocSection(string Xml, string Markdown, SourceSpan Span);

/// <summary>Une balise qui porte un nom : <c>&lt;param name="x"&gt;</c> ou <c>&lt;typeparam name="T"&gt;</c>.</summary>
/// <param name="Name">Le nom décrit.</param>
/// <param name="Text">Sa description.</param>
/// <param name="Span">La balise ouvrante dans le source.</param>
internal sealed record DocNamed(string Name, DocSection Text, SourceSpan Span);

/// <summary>Un échec documenté : <c>&lt;fails type="OutOfBound"&gt;</c>.</summary>
/// <param name="Type">Le type de l'erreur.</param>
/// <param name="Text">Dans quel cas elle survient.</param>
/// <param name="Span">La balise ouvrante dans le source.</param>
internal sealed record DocFails(string Type, DocSection Text, SourceSpan Span);

/// <summary>Une référence à un symbole : <c>&lt;see cref="…"/&gt;</c> ou <c>&lt;seealso cref="…"/&gt;</c>.</summary>
/// <param name="Cref">Le nom visé, tel qu'écrit.</param>
/// <param name="Span">La balise dans le source.</param>
internal sealed record DocReference(string Cref, SourceSpan Span);

/// <summary>
/// La documentation d'une déclaration, lue de ses commentaires <c>///</c>. Elle n'a aucun effet sur le typage ni sur le C++ :
/// elle ne sert qu'à l'outillage (survol, aide à la saisie, référence de l'API).
/// </summary>
/// <param name="Span">Du premier <c>///</c> à la fin du dernier.</param>
/// <param name="Summary">Le résumé, explicite (<c>&lt;summary&gt;</c>) ou implicite (le texte libre du début).</param>
/// <param name="Remarks">Les explications détaillées.</param>
/// <param name="Params">Les paramètres décrits.</param>
/// <param name="TypeParams">Les paramètres génériques décrits.</param>
/// <param name="Returns">Le sens du résultat.</param>
/// <param name="Fails">Les échecs possibles.</param>
/// <param name="Examples">Les exemples.</param>
/// <param name="SeeAlso">Les renvois écrits à la racine du commentaire.</param>
/// <param name="References">Toutes les références du commentaire, renvois et références en ligne.</param>
/// <param name="HasImplicitSummary">Vrai quand le résumé est le texte libre du début, sans balise.</param>
/// <param name="IsValid">Faux quand le XML est invalide : le texte brut sert alors de résumé.</param>
internal sealed record DocComment(
    SourceSpan Span,
    DocSection? Summary,
    DocSection? Remarks,
    IReadOnlyList<DocNamed> Params,
    IReadOnlyList<DocNamed> TypeParams,
    DocSection? Returns,
    IReadOnlyList<DocFails> Fails,
    IReadOnlyList<DocSection> Examples,
    IReadOnlyList<DocReference> SeeAlso,
    IReadOnlyList<DocReference> References,
    bool HasImplicitSummary = false,
    bool IsValid = true)
{
    public IReadOnlyList<DocCustom> Custom { get; init; } = [];
    public string? LocalizedXml { get; init; }
}

/// <summary>Une section personnalisée, avec ses attributs et son contenu XML conservés.</summary>
internal sealed record DocCustom(string Name, string Xml, DocSection Text);

/// <summary>Ce qu'une déclaration documentable est.</summary>
internal enum DocKind
{
    Gear,
    Guard,
    Emit,
    Train,
    Mold,
    Error,
    Enum,
    Ingot,
    Constant,
    Extension,
    Field,
    Variant,
}

/// <summary>
/// Ce que la signature d'une déclaration permet de vérifier dans sa documentation.
/// </summary>
/// <param name="Parameters">Les noms de ses paramètres.</param>
/// <param name="TypeParameters">Les noms de ses paramètres génériques.</param>
/// <param name="ReturnsValue">Vrai si elle produit une valeur (un gear dont le type n'est pas <c>void</c>, un train).</param>
internal sealed record DocTarget(IReadOnlyList<string> Parameters, IReadOnlyList<string> TypeParameters, bool ReturnsValue)
{
    /// <summary>Une déclaration sans paramètre ni résultat : un mold, un champ, une constante…</summary>
    public static DocTarget None { get; } = new([], [], false);
}

/// <summary>Une déclaration, avec sa documentation s'il y en a.</summary>
/// <param name="Id">Son identifiant stable : <c>Fichier:nom</c>, ou <c>Fichier:Type.membre</c> pour un champ, une variante ou un gear d'extension.</param>
/// <param name="Kind">Ce qu'elle est.</param>
/// <param name="Name">Son nom.</param>
/// <param name="Container">Le type qui la contient (champ, variante, membre d'extension) ; <see langword="null"/> au premier niveau.</param>
/// <param name="Signature">Sa signature telle qu'écrite, sur une ligne.</param>
/// <param name="NameSpan">Son nom dans le source.</param>
/// <param name="Module">Le fichier qui la déclare.</param>
/// <param name="Comment">Sa documentation ; <see langword="null"/> s'il n'y en a pas.</param>
/// <param name="Target">Ce que sa signature permet de vérifier.</param>
/// <param name="IsPublic">Vrai si un autre fichier peut la voir : un <c>.forge</c> qui l'exporte (ou n'exporte rien).</param>
/// <param name="Declaration">Où commence la déclaration, là où se placent ses commentaires (l'<c>export</c> qui la précède, s'il y en a un).</param>
internal sealed record DocEntry(
    string Id,
    DocKind Kind,
    string Name,
    string? Container,
    string Signature,
    SourceSpan NameSpan,
    string Module,
    DocComment? Comment,
    DocTarget Target,
    bool IsPublic,
    SourceSpan Declaration);
