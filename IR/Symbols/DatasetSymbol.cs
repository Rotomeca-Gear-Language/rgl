using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>
/// Un dataset : une table de molds dont chaque entrée a un nom. Sa table est une constante du fichier (<c>Moves</c>), son identifiant
/// est un ingot de <c>uint</c> propre à lui (<c>Moves.id</c>) et chaque entrée est un identifiant constant (<c>Moves.attaquer</c>).
/// Comparé par identité.
/// </summary>
/// <param name="name">Son nom, tel qu'écrit.</param>
/// <param name="declaration">Son nom dans sa déclaration.</param>
/// <param name="entries">Les noms de ses entrées, dans l'ordre : la position d'un nom est la valeur de son identifiant.</param>
internal sealed class DatasetSymbol(string name, SourceSpan declaration, IReadOnlyList<string> entries)
{
    /// <summary>Son nom, tel qu'écrit.</summary>
    public string Name { get; } = name;

    /// <summary>Son nom unique dans le programme (<c>bdd.Moves</c> pour un dataset d'un fichier importé) ; fixé une fois déclaré.</summary>
    public string QualifiedName { get; set; } = name;

    /// <summary>Son nom dans sa déclaration.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>Les noms de ses entrées, dans l'ordre.</summary>
    public IReadOnlyList<string> Entries { get; } = entries;

    /// <summary>Le mold de ses entrées ; fixé une fois les molds déclarés.</summary>
    public MoldSymbol? Mold { get; set; }

    /// <summary>L'ingot de ses identifiants, <c>Moves.id</c> : un <c>uint</c> qui désigne une entrée.</summary>
    public IngotSymbol? IdIngot { get; set; }

    /// <summary>La constante qui porte ses entrées, de type <c>[Mold]</c> ; fixée une fois la table liée.</summary>
    public VariableSymbol? Table { get; set; }

    /// <summary>La constante qui porte les noms de ses entrées, de type <c>[string;N]</c> : de quoi écrire un identifiant (<c>name()</c>) ou le retrouver (<c>find</c>).</summary>
    public VariableSymbol? Names { get; set; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
