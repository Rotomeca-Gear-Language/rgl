using Rgl.AST.Interfaces;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Une zone repliable : de la première à la dernière ligne (à partir de 0), et sa sorte (<c>comment</c>, <c>imports</c>) ou rien.</summary>
internal sealed record FoldingEntry(int StartLine, int EndLine, string? Kind);

/// <summary>
/// Les zones que l'éditeur sait replier : chaque paire d'accolades ou de crochets sur plusieurs lignes (corps de gear, mold, enum, bloc, tableau),
/// les commentaires de bloc, les suites de commentaires <c>//</c> ou <c>///</c>, les imports consécutifs, et les textes qui couvrent plusieurs lignes
/// (gabarits, blocs C++).
/// </summary>
internal static class FoldingProvider
{
    public static IReadOnlyList<FoldingEntry> Of(SemanticDocument document)
    {
        var source = document.Source;
        int LineOfStart(SourceSpan span) => (int)source[span.Start].Line.Value - 1;
        int LineOfEnd(SourceSpan span) => (int)source[Rotomeca.Utils.Types.RUint.CreateChecked(Math.Max(span.End.Value, 1u) - 1)].Line.Value - 1;
        var found = new List<FoldingEntry>();

        var stack = new Stack<Token>();
        foreach (var token in document.Self.Tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.OpenBrace or TokenKind.OpenBracket:
                    stack.Push(token);
                    break;

                case TokenKind.CloseBrace or TokenKind.CloseBracket when stack.Count > 0:
                    var open = stack.Pop();
                    // La ligne du fermant reste visible : on replie jusqu'à la ligne qui la précède.
                    if (LineOfStart(token.Span) > LineOfStart(open.Span))
                        found.Add(new FoldingEntry(LineOfStart(open.Span), LineOfStart(token.Span) - 1, null));
                    break;

                case TokenKind.CppBlock or TokenKind.TemplateStringLiteral or TokenKind.StringLiteral or TokenKind.MutableStringLiteral or TokenKind.RawStringLiteral:
                    if (LineOfEnd(token.Span) > LineOfStart(token.Span)) found.Add(new FoldingEntry(LineOfStart(token.Span), LineOfEnd(token.Span), null));
                    break;
            }

            // Commentaires : un bloc /* */ seul, ou une suite de lignes // (ou ///) qui se suivent.
            int? runStart = null, runEnd = null;
            foreach (var trivia in token.Before.Concat(token.After))
            {
                if (trivia.Kind == TriviaKind.BlockComment)
                {
                    if (LineOfEnd(trivia.Span) > LineOfStart(trivia.Span)) found.Add(new FoldingEntry(LineOfStart(trivia.Span), LineOfEnd(trivia.Span), "comment"));
                }
                else if (trivia.Kind is TriviaKind.SingleComment or TriviaKind.DocComment)
                {
                    var line = LineOfStart(trivia.Span);
                    if (runEnd is { } last && line == last + 1) runEnd = line;
                    else
                    {
                        if (runStart is { } first && runEnd > first) found.Add(new FoldingEntry(first, runEnd.Value, "comment"));
                        (runStart, runEnd) = (line, line);
                    }
                }
            }
            if (runStart is { } start && runEnd > start) found.Add(new FoldingEntry(start, runEnd.Value, "comment"));
        }

        // Les imports d'une même suite.
        var imports = document.Unit.Imports.ToList();
        if (imports.Count > 1)
        {
            var first = 0;
            for (var i = 1; i <= imports.Count; ++i)
            {
                if (i < imports.Count && LineOfStart(imports[i].Span) <= LineOfEnd(imports[i - 1].Span) + 1) continue;
                if (i - 1 > first) found.Add(new FoldingEntry(LineOfStart(imports[first].Span), LineOfEnd(imports[i - 1].Span), "imports"));
                first = i;
            }
        }
        return [.. found.Where(f => f.EndLine > f.StartLine).DistinctBy(f => (f.StartLine, f.EndLine)).OrderBy(f => f.StartLine).ThenByDescending(f => f.EndLine)];
    }
}

/// <summary>
/// L'extension de sélection (Maj+Alt+→) : du mot sous le curseur, la sélection grandit nœud après nœud de l'arbre, jusqu'au fichier entier.
/// </summary>
internal static class SelectionProvider
{
    /// <summary>Les étendues qui contiennent <paramref name="offset"/>, de la plus petite (le mot) à la plus grande, sans doublon.</summary>
    public static IReadOnlyList<SourceSpan> At(SemanticDocument document, int offset)
    {
        var spans = new List<SourceSpan>();
        if (document.TokenContaining(offset) is { Kind: not TokenKind.EndOfFile } word) spans.Add(word.Span);
        foreach (var node in document.PathAt(offset, inclusiveEnd: true).AsEnumerable().Reverse())
        {
            var span = node.Span;
            if (span.Length.Value == 0) continue;
            if (spans.Count > 0 && spans[^1].Start == span.Start && spans[^1].End == span.End) continue;
            spans.Add(span);
        }
        return spans;
    }
}

/// <summary>Les noms déclarés dans tout le projet, pour « Aller au symbole dans l'espace de travail » (Ctrl+T).</summary>
internal static class WorkspaceSymbolProvider
{
    /// <summary>Un nom trouvé : sa déclaration, le fichier et la sorte du protocole.</summary>
    public sealed record Entry(RglSymbol Symbol, ModuleInfo Module, int Kind);

    /// <summary>
    /// Les déclarations de premier niveau des <paramref name="modules"/> dont le nom contient les lettres de <paramref name="query"/> dans l'ordre,
    /// celles qui la contiennent d'un bloc d'abord ; au plus <paramref name="limit"/>.
    /// </summary>
    public static IReadOnlyList<Entry> Search(IEnumerable<ModuleInfo> modules, string query, int limit = 200)
    {
        var results = new List<(int Rank, Entry Entry)>();
        foreach (var module in modules)
        {
            foreach (var symbol in module.Symbols.Values.Where(s => s.IsTopLevel && s.NameSpan.Length.Value > 0))
            {
                if (_Rank(symbol.Name, query) is { } rank) results.Add((rank, new Entry(symbol, module, DocumentKind(symbol.Kind))));
            }
        }
        return [.. results.OrderBy(r => r.Rank).ThenBy(r => r.Entry.Symbol.Name.Length).ThenBy(r => r.Entry.Symbol.Name, StringComparer.Ordinal).Take(limit).Select(r => r.Entry)];
    }

    /// <summary>0 si le nom commence par la requête, 1 s'il la contient, 2 si ses lettres s'y retrouvent dans l'ordre, 3 pour une requête vide ; <see langword="null"/> sinon.</summary>
    private static int? _Rank(string name, string query)
    {
        if (query.Length == 0) return 3;
        var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index == 0) return 0;
        if (index > 0) return 1;

        var next = 0;
        foreach (var letter in name)
        {
            if (char.ToLowerInvariant(letter) == char.ToLowerInvariant(query[next]) && ++next == query.Length) return 2;
        }
        return null;
    }

    /// <summary>La sorte du protocole d'une déclaration : celle du plan du document.</summary>
    public static int DocumentKind(RglSymbolKind kind) => kind switch
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
