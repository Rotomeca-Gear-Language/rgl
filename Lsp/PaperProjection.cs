using System.Text;
using System.Text.RegularExpressions;
using Rgl.Paper;

namespace Rgl.Lsp;

/// <summary>
/// Un fichier <c>.paper</c> vu comme un fichier RGL : les blocs <c>@rgl { … }</c> et les interpolations <c>@@ … @@</c> en sont la partie RGL, le reste est du
/// HTML. La projection écrit un fichier <c>forge: gears</c> « virtuel » qui reprend le texte des blocs tel quel et fait de chaque interpolation un gear
/// (<c>escapeHtml((expression).toString())</c>, sans gabarit : un gabarit est un seul jeton, dont l'éditeur ne verrait pas les noms), puis retient quel morceau du texte virtuel vient de quel endroit du <c>.paper</c> : c'est ce qui permet à
/// l'éditeur de proposer, survoler et diagnostiquer le RGL écrit dans une page, comme dans un fichier <c>.forge</c>. Ce n'est pas ce que compile
/// <see cref="PaperCompiler"/> (qui réécrit aussi l'état réactif) : ce texte ne sert qu'à l'éditeur.
/// </summary>
internal sealed partial class PaperProjection
{
    /// <summary>La description du texte virtuel : elle le distingue d'un vrai fichier Paper à la lecture des imports.</summary>
    public const string Marker = PaperCompiler.VirtualMarker;

    private readonly List<(int Virtual, int Paper, int Length)> _segments = [];

    /// <summary>Le texte du <c>.paper</c>.</summary>
    public string PaperText { get; }

    /// <summary>Le fichier RGL virtuel.</summary>
    public string VirtualText { get; private set; } = "";

    /// <summary>Les noms des états <c>reactive</c> : le texte virtuel les déclare comme constantes, ce que le compilateur réécrit en champs de <c>State</c>.</summary>
    public IReadOnlyList<string> Reactives { get; private set; } = [];

    private PaperProjection(string paperText) => PaperText = paperText;

    /// <summary>Vrai pour un fichier <c>.paper</c>.</summary>
    public static bool IsPaper(string path) => path.EndsWith(PaperCompiler.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Vrai pour le texte virtuel d'un <c>.paper</c> (celui que le chargeur ne doit pas compiler comme une page).</summary>
    public static bool IsVirtual(string text) => text.StartsWith("forge: gears\ndescription: \"" + Marker + "\"", StringComparison.Ordinal);

    public static PaperProjection Build(string paper)
    {
        var projection = new PaperProjection(paper);
        var virtualText = new StringBuilder("forge: gears\ndescription: \"" + Marker + "\"\n\n"
            + "use <Http> only {HttpRequest, HttpResponse, HttpServer};\nuse <Paper> only {html, escapeHtml, escapeJson, DomEvent};\n\n");
        var reactives = new List<string>();

        var expressions = new List<(int Start, int End)>();
        foreach (var region in PaperRegions.Find(paper))
        {
            if (region.IsBlock) projection._AddBlock(virtualText, region.Start, region.End, reactives);
            else expressions.Add((region.Start, region.End));
        }

        var count = 0;
        foreach (var (start, end) in expressions)
        {
            if (paper.AsSpan(start, end - start).Trim().StartsWith("import", StringComparison.Ordinal)) continue;
            virtualText.Append("gear __paper_fragment_").Append(count++).Append("(): string => escapeHtml((");
            projection._Map(virtualText, start, end);
            virtualText.Append(").toString());\n");
        }

        projection.VirtualText = virtualText.ToString();
        projection.Reactives = reactives;
        return projection;
    }

    /// <summary>Ajoute l'intérieur d'un bloc <c>@rgl</c> ; une déclaration <c>reactive</c> devient une constante.</summary>
    private void _AddBlock(StringBuilder virtualText, int start, int end, List<string> reactives)
    {
        var position = start;
        foreach (Match match in ReactiveDeclaration().Matches(paperSpan(start, end)))
        {
            var from = start + match.Index;
            _Map(virtualText, position, from);
            virtualText.Append("const ");
            _Map(virtualText, start + match.Groups[2].Index, start + match.Index + match.Length);
            reactives.Add(match.Groups[3].Value);
            position = start + match.Index + match.Length;
        }
        _Map(virtualText, position, end);
        virtualText.Append('\n');

        string paperSpan(int from, int to) => PaperText.Substring(from, to - from);
    }

    /// <summary>Ajoute <c>PaperText[start..end]</c> au texte virtuel et retient d'où il vient.</summary>
    private void _Map(StringBuilder virtualText, int start, int end)
    {
        if (end <= start) return;
        _segments.Add((virtualText.Length, start, end - start));
        virtualText.Append(PaperText, start, end - start);
    }

    /// <summary>La place du texte virtuel qui correspond à cette place du <c>.paper</c> ; <see langword="null"/> hors du RGL. La fin d'un morceau compte (le curseur juste après un mot).</summary>
    public int? ToVirtual(int paperOffset)
    {
        foreach (var (virtualStart, paperStart, length) in _segments)
        {
            if (paperOffset >= paperStart && paperOffset <= paperStart + length) return virtualStart + paperOffset - paperStart;
        }
        return null;
    }

    /// <summary>La place du <c>.paper</c> qui correspond à cette place du texte virtuel ; <see langword="null"/> pour du code que la projection a ajouté.</summary>
    public int? ToPaper(int virtualOffset)
    {
        foreach (var (virtualStart, paperStart, length) in _segments)
        {
            if (virtualOffset >= virtualStart && virtualOffset <= virtualStart + length) return paperStart + virtualOffset - virtualStart;
        }
        return null;
    }

    /// <summary>Le morceau du <c>.paper</c> qui correspond à ce morceau du texte virtuel, entièrement contenu dans du RGL du <c>.paper</c> ; <see langword="null"/> sinon.</summary>
    public (int Start, int End)? ToPaper(int virtualStart, int virtualEnd)
    {
        foreach (var (segmentStart, paperStart, length) in _segments)
        {
            if (virtualStart >= segmentStart && virtualEnd <= segmentStart + length && virtualStart <= virtualEnd)
                return (paperStart + virtualStart - segmentStart, paperStart + virtualEnd - segmentStart);
        }
        return null;
    }

    [GeneratedRegex(@"\breactive\s+(?:(mut)\s+)?(.+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([^;]+);", RegexOptions.Singleline)]
    private static partial Regex ReactiveDeclaration();
}
