namespace Rgl.Docs;

/// <summary>
/// Les codes des diagnostics de la documentation. Ils ne s'affichent que pour <c>rgl doc check</c> et dans l'éditeur : la compilation ne
/// lit jamais la documentation, une documentation fausse ne la fait pas échouer.
/// </summary>
internal static class DocCodes
{
    /// <summary>Le XML du commentaire est invalide.</summary>
    public const string InvalidXml = "DOC001";

    /// <summary><c>&lt;param&gt;</c> nomme un paramètre qui n'existe pas (ou n'a pas de nom).</summary>
    public const string UnknownParam = "DOC002";

    /// <summary>Un paramètre ou un paramètre générique d'un nom public n'est pas décrit.</summary>
    public const string UndocumentedParam = "DOC003";

    /// <summary>Un <c>cref</c> ne désigne aucun symbole connu.</summary>
    public const string Cref = "DOC004";

    /// <summary><c>&lt;returns&gt;</c> sur une déclaration qui ne produit aucune valeur.</summary>
    public const string Returns = "DOC005";

    /// <summary>Une balise ou un texte que la documentation ne connaît pas.</summary>
    public const string UnknownTag = "DOC006";

    /// <summary><c>&lt;typeparam&gt;</c> nomme un paramètre générique qui n'existe pas.</summary>
    public const string UnknownTypeParam = "DOC007";

    /// <summary>Une balise écrite deux fois, dont le résumé écrit à la fois sans balise et avec <c>&lt;summary&gt;</c>.</summary>
    public const string Duplicate = "DOC008";

    /// <summary>Les échecs documentés ne correspondent pas à la déclaration.</summary>
    public const string Fails = "DOC010";

    /// <summary>Un nom public n'a pas de résumé.</summary>
    public const string MissingSummary = "DOC011";

    /// <summary>Un bloc de langue apparaît ailleurs qu’à la racine du commentaire.</summary>
    public const string LanguagePlacement = "DOC013";

    /// <summary>Un attribut obligatoire manque (<c>type</c> de <c>&lt;fails&gt;</c>).</summary>
    public const string MissingAttribute = "DOC012";
}
