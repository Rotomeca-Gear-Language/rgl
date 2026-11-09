using Rgl.AST.Datasets;
using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Molds;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Une valeur qu'une cellule propose : son libellé, son écriture dans le source RGL (<c>Element.Feu</c>) et dans un fichier JSON (<c>"Feu"</c>).</summary>
internal sealed record DatasetChoice(string Label, string Inline, string Json);

/// <summary>Une colonne de l'éditeur : un champ du mold, son type écrit, s'il est obligatoire (ni valeur par défaut, ni nullable) et ce qu'on y propose.</summary>
internal sealed record DatasetField(string Name, string? Type, bool Required, IReadOnlyList<DatasetChoice> Choices);

/// <summary>Une ligne : une entrée, ses valeurs écrites par champ ; <see cref="Raw"/> quand la valeur n'est pas un <c>{champ: valeur}</c> qu'on sait découper.</summary>
internal sealed record DatasetRow(string Name, IReadOnlyDictionary<string, string> Values, string? Raw);

/// <summary>
/// Ce que l'éditeur de dataset de l'extension affiche et réécrit : le dataset sous le curseur (nom, mold, entrées écrites ou fichier de
/// données), ses colonnes (les champs de son mold, avec leur type, leurs valeurs possibles pour un enum, un identifiant d'un autre dataset ou un
/// booléen) et l'endroit de ses entrées à remplacer. Lu dans l'arbre, comme le reste de l'éditeur : il marche tant que le fichier est
/// analysable, même s'il contient des erreurs de typage.
/// </summary>
/// <param name="Name">Le nom du dataset.</param>
/// <param name="Mold">Le nom de son mold.</param>
/// <param name="CreatesMold">Vrai pour <c>as</c> : les colonnes viennent des entrées.</param>
/// <param name="Source">Le fichier de données écrit après <c>from</c> (sans guillemets) ; <see langword="null"/> pour des entrées écrites.</param>
/// <param name="Body">Les accolades et ce qu'elles contiennent, à remplacer ; <see langword="null"/> pour un fichier de données.</param>
/// <param name="Fields">Les colonnes.</param>
/// <param name="Rows">Les entrées écrites ; vides pour un fichier de données, que l'extension lit elle-même.</param>
internal sealed record DatasetEditorModel(string Name, string Mold, bool CreatesMold, string? Source, SourceSpan? Body, IReadOnlyList<DatasetField> Fields, IReadOnlyList<DatasetRow> Rows)
{
    /// <summary>Le dataset déclaré à la ligne <paramref name="line"/> (à partir de 0), ou le premier qui la contient ; <see langword="null"/> s'il n'y en a pas.</summary>
    public static DatasetEditorModel? At(SemanticDocument document, uint line)
    {
        var declarations = document.Unit.Statements.OfType<DatasetDeclaration>().Where(d => !d.Name.IsMissing).ToArray();
        var chosen = declarations.FirstOrDefault(d => d.Span.StartPosition.Line.Value - 1 == line)
            ?? declarations.FirstOrDefault(d => d.Span.StartPosition.Line.Value - 1 <= line && line <= d.Span.EndPosition.Line.Value - 1);
        return chosen is null ? null : Of(document, chosen);
    }

    /// <summary>Le modèle de <paramref name="dataset"/>.</summary>
    public static DatasetEditorModel Of(SemanticDocument document, DatasetDeclaration dataset)
    {
        var offset = (int)dataset.Span.Start.Value;
        var rows = dataset.Entries.Where(e => !e.Name.IsMissing).Select(_Row).ToList();

        var fields = new List<DatasetField>();
        if (!dataset.CreatesMold && document.ResolveType(dataset.Mold.Name, offset) is { Kind: RglSymbolKind.Mold } mold)
        {
            foreach (var member in document.MembersOf(mold.Name, offset).Where(m => m.Kind == RglSymbolKind.Field))
            {
                var type = member.TypeText;
                var required = member.Node is MoldField { Default: null } && type is not null && !type.EndsWith('?');
                fields.Add(new DatasetField(member.Name, type, required, type is null ? [] : _Choices(document, type, offset)));
            }
        }

        // Un mold créé par le dataset, ou introuvable : les colonnes sont les champs que les entrées écrivent.
        foreach (var name in rows.SelectMany(r => r.Values.Keys).Distinct(StringComparer.Ordinal))
        {
            if (fields.All(f => f.Name != name)) fields.Add(new DatasetField(name, null, false, []));
        }

        var source = dataset.Source is { } literal ? literal.Span.Text.Trim('"', '\'') : null;
        return new DatasetEditorModel(dataset.Name.Name, dataset.Mold.Name, dataset.CreatesMold, source, source is null ? _Body(document, dataset) : null, fields, rows);
    }

    private static DatasetRow _Row(DatasetEntry entry)
    {
        if (entry.Value is not MoldExpression { Type: null } mold || mold.Entries.Any(e => e is not MoldFieldValue))
            return new DatasetRow(entry.Name.Name, new Dictionary<string, string>(), Signatures.Collapse(entry.Value.Span.Text));

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in mold.Entries.OfType<MoldFieldValue>().Where(f => !f.Name.IsMissing))
            values[field.Name.Name] = field.IsShorthand ? field.Name.Name : Signatures.Collapse(field.Value.Span.Text);
        return new DatasetRow(entry.Name.Name, values, null);
    }

    /// <summary>De l'accolade ouvrante des entrées à la fermante : ce que l'éditeur remplace.</summary>
    private static SourceSpan? _Body(SemanticDocument document, DatasetDeclaration dataset)
    {
        var open = document.Self.Tokens.FirstOrDefault(t => t.Kind == TokenKind.OpenBrace && t.Span.Start.Value >= dataset.Mold.Span.End.Value && t.Span.Start.Value < dataset.Span.End.Value);
        return open is null ? null : new SourceSpan(open.Span.Source, open.Span.Start, dataset.Span.End.Value - open.Span.Start.Value);
    }

    /// <summary>Les valeurs qu'un type offre : les variantes d'un enum, les entrées d'un dataset pour son identifiant, <c>true</c> et <c>false</c>.</summary>
    private static IReadOnlyList<DatasetChoice> _Choices(SemanticDocument document, string type, int offset)
    {
        var text = SemanticDocument.StripNullable(type).Trim();
        if (text == "bool") return [new("true", "true", "true"), new("false", "false", "false")];

        if (document.DatasetOfIdType(text, offset) is { Node: DatasetDeclaration dataset })
        {
            var prefix = text[..^3];
            return [.. dataset.Entries.Where(e => !e.Name.IsMissing).Select(e => new DatasetChoice(e.Name.Name, $"{prefix}.{e.Name.Name}", $"\"{e.Name.Name}\""))];
        }

        if (document.ResolveType(text, offset) is { Kind: RglSymbolKind.Enum, Node: EnumDeclaration enumeration })
            return [.. enumeration.Variants.Where(v => !v.Name.IsMissing).Select(v => new DatasetChoice(v.Name.Name, $"{text}.{v.Name.Name}", $"\"{v.Name.Name}\""))];

        return [];
    }
}
