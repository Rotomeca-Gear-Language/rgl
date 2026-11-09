using Rgl.AST.Datasets;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;

namespace Rgl.Lsp;

/// <summary>
/// Les datasets pour l'éditeur : <c>Moves.</c> propose ses entrées (<c>attaquer</c>, <c>soin</c>…), le type <c>id</c> de ses identifiants,
/// <c>length</c> et <c>find</c> ; un identifiant (<c>Moves.id</c>) a <c>get()</c> et <c>name()</c>. Tout se lit dans l'arbre, comme le reste de la
/// complétion : le dataset peut être écrit dans ce document ou dans un fichier importé.
/// </summary>
internal sealed partial class SemanticDocument
{
    /// <summary>Le dataset que désigne <paramref name="expression"/> (<c>Moves</c>, <c>bdd.Moves</c>) ; <see langword="null"/> si ce n'est pas un dataset.</summary>
    public RglSymbol? DatasetOf(IExpression expression, int offset) =>
        ResolveExpression(expression, offset) is { Kind: RglSymbolKind.Dataset, Node: DatasetDeclaration } dataset ? dataset : null;

    /// <summary>
    /// Le dataset dont <paramref name="typeText"/> est le type des identifiants (<c>Moves.id</c>, <c>bdd.Moves.id</c>) ; <see langword="null"/> sinon.
    /// </summary>
    public RglSymbol? DatasetOfIdType(string typeText, int offset)
    {
        var text = StripNullable(typeText).Trim();
        if (!text.EndsWith(".id", StringComparison.Ordinal)) return null;

        var prefix = text[..^3];
        var dot = prefix.LastIndexOf('.');
        RglSymbol? symbol = dot < 0
            ? ResolveName(prefix, offset)
            : QualifiedNames(prefix[..dot]) is { } names && names.TryGetValue(prefix[(dot + 1)..], out var found) ? found : null;
        return symbol is { Kind: RglSymbolKind.Dataset } ? symbol : _FindAnywhere(prefix, s => s.Kind == RglSymbolKind.Dataset);
    }

    /// <summary>Les entrées d'un dataset, puis <c>id</c>, <c>length</c> et <c>find</c>.</summary>
    public IReadOnlyList<RglSymbol> DatasetMembers(RglSymbol dataset)
    {
        var declaration = (DatasetDeclaration)dataset.Node!;
        var members = new List<RglSymbol>();
        foreach (var entry in declaration.Entries.Where(e => !e.Name.IsMissing))
        {
            members.Add(new RglSymbol(entry.Name.Name, RglSymbolKind.Variant, entry.Name.Span, entry, dataset.Module, null, Container: dataset.Name,
                Documentation: $"Identifiant de l'entrée, de type `{dataset.Name}.id`.\n\n```rgl\n{entry.Name.Name}: {Signatures.Truncate(Signatures.Collapse(entry.Value.Span.Text))}\n```"));
        }

        members.Add(new RglSymbol("id", RglSymbolKind.Ingot, default, null, dataset.Module, null, Container: dataset.Name,
            Documentation: $"Le type des identifiants de `{dataset.Name}` : un identifiant ne se confond pas avec celui d'un autre dataset."));
        members.Add(new RglSymbol("length", RglSymbolKind.Field, default, null, dataset.Module, "uint", Container: dataset.Name, Documentation: "Nombre d'entrées."));
        members.Add(new RglSymbol("find", RglSymbolKind.Method, default, null, dataset.Module, $"find(nom: string): {dataset.Name}.id?", Container: dataset.Name,
            Documentation: "L'identifiant de l'entrée de ce nom, ou `null` s'il n'y en a pas : de quoi relire une sauvegarde."));
        members.Add(new RglSymbol("table", RglSymbolKind.Method, default, null, dataset.Module, $"table(): [{declaration.Mold.Name}]", Container: dataset.Name,
            Documentation: "Une copie de la table, un tableau ordinaire où l'on ajoute des entrées : une table mutable. Les identifiants des entrées du dataset ne changent jamais, les ajouts reçoivent les positions suivantes."));
        members.Add(new RglSymbol("at", RglSymbolKind.Method, default, null, dataset.Module, $"at(position: uint): {dataset.Name}.id", Container: dataset.Name,
            Documentation: "L'identifiant de cette position (celui d'une entrée ajoutée à une table mutable) ; il n'est vérifié qu'à la lecture, par `get()` ou `get(table)`."));
        return members;
    }

    /// <summary><c>get()</c> et <c>name()</c> : ce qu'un identifiant de ce dataset offre.</summary>
    public IReadOnlyList<RglSymbol> DatasetIdMembers(RglSymbol dataset)
    {
        var declaration = (DatasetDeclaration)dataset.Node!;
        return
        [
            new RglSymbol("get", RglSymbolKind.Method, default, null, dataset.Module, $"get(): {declaration.Mold.Name}", Container: dataset.Name + ".id",
                Documentation: $"L'entrée de `{dataset.Name}` que désigne l'identifiant : un `{declaration.Mold.Name}`. L'accès est vérifié."),
            new RglSymbol("name", RglSymbolKind.Method, default, null, dataset.Module, "name(): string", Container: dataset.Name + ".id",
                Documentation: "Le nom de l'entrée : de quoi écrire une sauvegarde sans dépendre des positions."),
        ];
    }
}
