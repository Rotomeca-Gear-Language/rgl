using Rgl.AST.Datasets;
using Rgl.AST.Enums;
using Rgl.AST.Extensions;
using Rgl.AST.Gears;
using Rgl.AST.Molds;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Un élément du plan d'un document : un nom, sa sorte (celle du protocole), son étendue et ce qu'il contient.</summary>
/// <param name="Name">Le nom affiché.</param>
/// <param name="Kind">La sorte du protocole : 12 fonction, 23 structure, 10 enum, 22 variante, 8 champ, 14 constante, 5 classe, 3 espace de noms, 6 méthode.</param>
/// <param name="Detail">Ce qui se lit à côté du nom : sa signature, son type.</param>
/// <param name="Range">Toute la déclaration.</param>
/// <param name="Selection">Le nom dans la déclaration.</param>
/// <param name="Children">Ses champs, variantes ou gears.</param>
internal sealed record DocumentSymbolEntry(string Name, int Kind, string? Detail, SourceSpan Range, SourceSpan Selection, IReadOnlyList<DocumentSymbolEntry> Children);

/// <summary>
/// Le plan d'un document, pour la vue « Plan », le fil d'Ariane et « Aller au symbole » (Ctrl+Maj+O) : ses gears, molds avec leurs champs,
/// enums avec leurs variantes, ingots, constantes, trains et extensions avec leurs gears, dans l'ordre du fichier.
/// </summary>
internal static class DocumentSymbolProvider
{
    /// <summary>Le plan de <paramref name="document"/>.</summary>
    public static IReadOnlyList<DocumentSymbolEntry> Of(SemanticDocument document)
    {
        var entries = new List<DocumentSymbolEntry>();
        // Une déclaration en cours d'écriture n'a pas encore de nom : l'éditeur refuse un élément sans nom, on le laisse de côté.
        foreach (var symbol in document.Self.Symbols.Values.Where(s => s.IsTopLevel && s.Node is not null && !string.IsNullOrWhiteSpace(s.Name)).OrderBy(s => s.NameSpan.Start.Value))
        {
            var children = symbol.Node switch
            {
                MoldDeclaration mold => [.. mold.Members.OfType<MoldField>().Where(f => !string.IsNullOrWhiteSpace(f.Name.Name)).Select(f => new DocumentSymbolEntry(f.Name.Name, 8, Signatures.Collapse(f.Type.Span.Text), f.Span, f.Name.Span, []))],
                DatasetDeclaration dataset => [.. dataset.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name.Name)).Select(e => new DocumentSymbolEntry(e.Name.Name, 22, null, e.Span, e.Name.Span, []))],
                EnumDeclaration enumeration => [.. enumeration.Variants.Where(v => !string.IsNullOrWhiteSpace(v.Name.Name)).Select(v => new DocumentSymbolEntry(v.Name.Name, 22, null, v.Span, v.Name.Span, []))],
                _ => (IReadOnlyList<DocumentSymbolEntry>)[],
            };
            entries.Add(new DocumentSymbolEntry(symbol.Name, _Kind(symbol.Kind), Signatures.Of(symbol), symbol.Node!.Span, symbol.NameSpan, children));
        }

        foreach (var annotation in document.Unit.Statements.OfType<AST.Notes.AnnotationDeclaration>().Where(a => !a.Name.IsMissing))
            entries.Add(new DocumentSymbolEntry("@" + annotation.Name.Name, 12, Signatures.Of(new RglSymbol(annotation.Name.Name, RglSymbolKind.Annotation, annotation.Name.Span, annotation, document.Self)), annotation.Span, annotation.Name.Span, []));

        foreach (var extend in document.Unit.Statements.OfType<ExtendDeclaration>())
        {
            var target = Signatures.Collapse(extend.Target.Span.Text);
            var gears = extend.Members.OfType<GearDeclaration>().Where(g => !string.IsNullOrWhiteSpace(g.Name.Name))
                .Select(g => new DocumentSymbolEntry(g.Name.Name, 6, Signatures.Of(g), g.Span, g.Name.Span, [])).ToArray();
            if (target.Length == 0) continue;
            entries.Add(new DocumentSymbolEntry("extend " + target, 5, null, extend.Span, extend.Target.Span, gears));
        }
        return [.. entries.OrderBy(e => e.Range.Start.Value)];
    }

    private static int _Kind(RglSymbolKind kind) => kind switch
    {
        RglSymbolKind.Gear or RglSymbolKind.Guard or RglSymbolKind.Emit or RglSymbolKind.Train => 12,
        RglSymbolKind.Mold => 23,
        RglSymbolKind.Error => 5,
        RglSymbolKind.Enum or RglSymbolKind.Dataset => 10,
        RglSymbolKind.Ingot => 26,
        RglSymbolKind.Constant => 14,
        _ => 13,
    };
}
