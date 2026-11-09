using Rgl.AST.Gears;
using Rgl.AST.Train;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Ce qui s'appelle dans la hiérarchie d'appels : un gear, un guard, un emit ou le train d'un fichier.</summary>
/// <param name="Name">Son nom (« train » pour un train).</param>
/// <param name="Detail">Le fichier qui le déclare.</param>
/// <param name="Path">Le fichier.</param>
/// <param name="Source">Son texte, pour convertir les plages.</param>
/// <param name="Range">La déclaration entière.</param>
/// <param name="Selection">Son nom.</param>
internal sealed record CallItem(string Name, string Detail, string Path, SourceText Source, SourceSpan Range, SourceSpan Selection);

/// <summary>Un appel entre deux éléments : l'autre élément et les endroits où l'appel s'écrit.</summary>
/// <param name="Item">Celui qui appelle (appels entrants) ou celui qui est appelé (appels sortants).</param>
/// <param name="Source">Le texte où sont écrits les appels.</param>
/// <param name="Calls">Les endroits de l'appel.</param>
internal sealed record CallEdge(CallItem Item, SourceText Source, IReadOnlyList<SourceSpan> Calls);

/// <summary>
/// La hiérarchie d'appels : qui appelle ce gear (les emplois de son nom, regroupés par gear englobant, dans tout le projet) et ce qu'il appelle (les noms de
/// gears écrits dans son corps). Lue sur la même résolution des noms que le survol, donc elle répond aussi quand le fichier ne compile pas.
/// </summary>
internal static class CallHierarchyProvider
{
    /// <summary>L'élément appelable désigné par le mot en <paramref name="offset"/> ; <see langword="null"/> si ce n'est pas un gear, un guard, un emit ou un train déclaré.</summary>
    public static CallItem? Prepare(SemanticDocument document, int offset)
    {
        var target = document.TargetAt(ReferenceFinder.WordOffset(document, offset));
        return target?.Symbol is { IsCallable: true, Module: not null } symbol && ReferenceFinder.KeyOf(symbol) is not null ? _ItemOf(symbol) : null;
    }

    /// <summary>Les éléments qui appellent <paramref name="item"/>, avec les endroits de leurs appels.</summary>
    public static IReadOnlyList<CallEdge> Incoming(SemanticDocument document, CallItem item)
    {
        if (ReferenceFinder.Find(document, (int)item.Selection.Start.Value) is not { } found) return [];

        var callers = new Dictionary<string, (CallItem Item, SourceText Source, List<SourceSpan> Calls)>(StringComparer.Ordinal);
        foreach (var occurrence in found.Occurrences.Where(o => !o.IsDeclaration))
        {
            var where = string.Equals(occurrence.Path, document.Self.Path, StringComparison.OrdinalIgnoreCase)
                ? document : SemanticDocument.Create(occurrence.Path, occurrence.Source.Content, document.Project.SeenFrom(
                    document.Project.Sources.First(m => string.Equals(m.Path, occurrence.Path, StringComparison.OrdinalIgnoreCase)), document.Self));
            if (_EnclosingOf(where, (int)occurrence.Span.Start.Value) is not { } caller) continue;

            var key = $"{caller.Path.ToUpperInvariant()}|{caller.Selection.Start.Value}";
            if (!callers.TryGetValue(key, out var entry)) callers[key] = entry = (caller, occurrence.Source, []);
            entry.Calls.Add(occurrence.Span);
        }
        return [.. callers.Values.Select(c => new CallEdge(c.Item, c.Source, c.Calls))];
    }

    /// <summary>Les éléments que <paramref name="item"/> appelle, avec les endroits de ses appels.</summary>
    public static IReadOnlyList<CallEdge> Outgoing(SemanticDocument document, CallItem item)
    {
        var callees = new Dictionary<string, (CallItem Item, List<SourceSpan> Calls)>(StringComparer.Ordinal);
        foreach (var token in document.Self.Tokens)
        {
            if (token.Kind != TokenKind.Identifier || token.Span.Start.Value < item.Range.Start.Value || token.Span.End.Value > item.Range.End.Value) continue;
            if (token.Span.Start == item.Selection.Start) continue;

            if (document.TargetAt((int)token.Span.Start.Value)?.Symbol is not { IsCallable: true, Module: not null } symbol || ReferenceFinder.KeyOf(symbol) is not { } key) continue;
            if (!callees.TryGetValue(key, out var entry)) callees[key] = entry = (_ItemOf(symbol), []);
            entry.Calls.Add(token.Span);
        }
        return [.. callees.Values.Select(c => new CallEdge(c.Item, document.Source, c.Calls))];
    }

    private static CallItem _ItemOf(RglSymbol symbol)
    {
        var module = symbol.Module!;
        return new CallItem(symbol.Name, module.Name, module.Path, module.Source, symbol.Node?.Span ?? symbol.NameSpan, symbol.NameSpan);
    }

    /// <summary>Le gear (ou le train) qui contient la place <paramref name="offset"/>.</summary>
    private static CallItem? _EnclosingOf(SemanticDocument document, int offset)
    {
        foreach (var node in document.PathAt(offset, inclusiveEnd: true).AsEnumerable().Reverse())
        {
            if (node is GearDeclaration gear)
                return new CallItem(gear.Name.Name, document.Self.Name, document.Self.Path, document.Source, gear.Span, gear.Name.Span);
            if (node is TrainDeclaration train)
                return new CallItem("train", document.Self.Name, document.Self.Path, document.Source, train.Span, new SourceSpan(train.Span.Source, train.Span.Start, Rotomeca.Utils.Types.RUint.CreateChecked(Math.Min(5, (int)train.Span.Length.Value))));
        }
        return null;
    }
}
