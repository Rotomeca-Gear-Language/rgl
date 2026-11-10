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

    /// <param name="paper">Le texte de la page.</param>
    /// <param name="layouts">Les textes des layouts de la page (le plus proche d'abord) : leurs blocs <c>@rgl</c> rejoignent ceux de la page à la compilation, donc leurs noms se lisent dans la page. Ils entrent dans le texte virtuel sans correspondance avec la page.</param>
    public static PaperProjection Build(string paper, IReadOnlyList<string>? layouts = null)
    {
        var projection = new PaperProjection(paper);
        var virtualText = new StringBuilder("forge: gears\ndescription: \"" + Marker + "\"\n\n"
            + "use <Http> only {HttpRequest, HttpResponse, HttpServer};\nuse <Paper> only {html, escapeHtml, escapeJson, DomEvent, PageHead};\n\n");
        var reactives = new List<string>();
        // La tête de page implicite : une constante pour l'éditeur (les écritures qu'elle reçoit ne sont pas des erreurs de la page).
        if (PageWord().IsMatch(paper) || (layouts ?? []).Any(layout => PageWord().IsMatch(layout)))
        {
            virtualText.Append("const PageHead page: {};\n\n");
            reactives.Add(PaperCompiler.PageName);
        }

        // Les constantes du layout s'écrivent avant celles de la page, ses gears après ses blocs : une constante ne vient jamais après un gear.
        var layoutNumber = 0;
        var layoutRest = new StringBuilder();
        foreach (var layoutText in layouts ?? [])
        {
            var nested = new PaperProjection(layoutText);
            var piece = new StringBuilder();
            foreach (var region in PaperRegions.Find(layoutText).Where(r => r.IsBlock)) nested._AddBlock(piece, region.Start, region.End, reactives);
            // Le load d'un layout est renommé à la compilation : il ne fait pas doublon avec celui de la page.
            var (constants, rest) = PaperCompiler.SplitConstants(LayoutLoad().Replace(piece.ToString(), "gear __layout_load_" + ++layoutNumber + "("));
            virtualText.Append(constants);
            layoutRest.Append(rest).Append('\n');
        }

        var expressions = new List<(int Start, int End)>();
        foreach (var region in PaperRegions.Find(paper))
        {
            if (region.IsBlock) projection._AddBlock(virtualText, region.Start, region.End, reactives);
            else expressions.Add((region.Start, region.End));
        }

        virtualText.Append(layoutRest);

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
        var block = paperSpan(start, end);
        var declarations = ParamDeclaration().Matches(block).Select(m => (Match: m, IsParam: true))
            .Concat(ReactiveDeclaration().Matches(block).Select(m => (Match: m, IsParam: false)))
            .OrderBy(d => d.Match.Index);
        foreach (var (match, isParam) in declarations)
        {
            if (!isParam)
            {
                var from = start + match.Index;
                _Map(virtualText, position, from);
                virtualText.Append("const ");
                _Map(virtualText, start + match.Groups[2].Index, start + match.Index + match.Length);
                reactives.Add(match.Groups[3].Value);
                position = start + match.Index + match.Length;
                continue;
            }
            // Un param est une constante ; sans valeur par défaut, le texte virtuel lui en donne une inconnue (son type reste celui qu'écrit la page).
            _Map(virtualText, position, start + match.Index);
            virtualText.Append("const ");
            _Map(virtualText, start + match.Groups[1].Index, start + match.Groups[2].Index + match.Groups[2].Length);
            if (match.Groups[3].Success)
            {
                virtualText.Append(": ");
                _Map(virtualText, start + match.Groups[3].Index, start + match.Groups[3].Index + match.Groups[3].Length);
            }
            else virtualText.Append(": __param__()");
            virtualText.Append(';');
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

    [GeneratedRegex(@"(?:^|(?<=[;{}]))[ \t]*param\s+(.+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*([^;]+))?;", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex ParamDeclaration();

    [GeneratedRegex(@"\bgear\s+load\s*\(")]
    private static partial Regex LayoutLoad();

    [GeneratedRegex(@"\bpage\b")]
    private static partial Regex PageWord();

    [GeneratedRegex(@"\breactive\s+(?:(mut)\s+)?(.+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([^;]+);", RegexOptions.Singleline)]
    private static partial Regex ReactiveDeclaration();
}
