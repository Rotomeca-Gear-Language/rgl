using System.Text;
using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Text;

namespace Rgl.Paper;

internal static partial class PaperCompiler
{
    /// <summary>
    /// Enveloppe la page dans ses layouts : chaque directive <c>@layout Nom;</c> cherche <c>Nom.paper</c> dans le projet, et le markup de la page prend la place de son
    /// <c>&lt;slot /&gt;</c>. Les blocs <c>@rgl</c> du layout précèdent ceux de la page : la fusion donne une seule page, avec un seul état, une seule numérotation de fragments et un seul
    /// jeu de handlers. Un layout peut avoir son propre layout. Le gear <c>load</c> d'un layout est renommé (<c>__layout_load_N</c>) et s'exécute avant celui de la page, du layout le
    /// plus extérieur au plus proche.
    /// </summary>
    /// <param name="layoutLoads">Reçoit les gears de chargement des layouts, dans l'ordre d'exécution.</param>
    /// <param name="layers">Reçoit le nombre de layouts appliqués.</param>
    private static bool ApplyLayouts(SourceText source, string projectRoot, DiagnosticBag diagnostics, StringBuilder blocks, StringBuilder markup,
        List<(int PaperLine, int Lines)> blockRanges, List<string> layoutLoads, out int layers)
    {
        layers = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(source.Path) };
        var currentMarkup = markup.ToString();
        var currentBlocks = blocks.ToString();
        var ranges = new List<(int PaperLine, int Lines)>(blockRanges);
        while (LayoutDirective().Match(currentMarkup) is { Success: true } directive)
        {
            var name = directive.Groups[1].Value;
            currentMarkup = currentMarkup.Remove(directive.Index, directive.Length);
            var path = FindPaperFile(name, "layout", source.Path, projectRoot, diagnostics);
            if (path is null) return false;
            if (!visited.Add(Path.GetFullPath(path)))
            {
                diagnostics.Error($"le layout « {name} » se contient lui-même : une chaîne de @layout ne peut pas boucler", source.Path);
                return false;
            }
            var layout = SourceText.FromPath(path, diagnostics);
            if (layout is null) return false;
            var layoutBlocks = new StringBuilder();
            var layoutMarkup = new StringBuilder();
            List<(int PaperLine, int Lines)> layoutRanges = [];
            if (!Split(layout, layoutBlocks, layoutMarkup, diagnostics, layoutRanges)) return false;
            var slots = SlotTag().Matches(layoutMarkup.ToString()).Count;
            if (slots != 1)
            {
                diagnostics.Error(slots == 0
                    ? $"le layout « {name} » n'a pas de <slot /> : il n'y a nulle part où mettre la page"
                    : $"le layout « {name} » a {slots} <slot /> : il en faut un seul", layout.Path);
                return false;
            }

            layers++;
            var layoutText = layoutBlocks.ToString();
            if (LayoutLoad().IsMatch(layoutText))
            {
                if (GearSignature().Matches(layoutText).Any(m => m.Groups[1].Value == LoadGear && m.Groups[2].Value.Trim().Length > 0))
                {
                    diagnostics.Error($"le gear « {LoadGear} » du layout « {name} » ne prend aucun paramètre", layout.Path);
                    return false;
                }
                var renamed = $"__layout_load_{layers}";
                layoutText = LayoutLoad().Replace(layoutText, $"gear {renamed}(");
                layoutLoads.Insert(0, renamed);
            }

            var shared = GearSignature().Matches(layoutText).Select(m => m.Groups[1].Value)
                .Intersect(GearSignature().Matches(currentBlocks).Select(m => m.Groups[1].Value), StringComparer.Ordinal)
                .FirstOrDefault(gear => !gear.StartsWith("__layout_load_", StringComparison.Ordinal));
            if (shared is not null)
            {
                diagnostics.Error($"le layout « {name} » et la page déclarent tous deux le gear « {shared} » : la page fusionnée n'a qu'une portée, renomme l'un des deux", source.Path);
                return false;
            }

            var page = currentMarkup;
            currentMarkup = SlotTag().Replace(layoutMarkup.ToString(), _ => page);
            currentBlocks = layoutText + currentBlocks;
            // Les lignes d'un layout ne sont pas celles de la page : le débogueur ne les rattache à rien (ligne 0).
            ranges.InsertRange(0, layoutRanges.Select(range => (0, range.Lines)));
        }

        // Une page ou un layout sans layout au-dessus reste une page à part entière : sa place est vide.
        currentMarkup = SlotTag().Replace(currentMarkup, "");
        if (currentMarkup.Contains("@layout", StringComparison.Ordinal))
        {
            diagnostics.Error("directive @layout mal formée ; écris : @layout Nom; sur sa propre ligne", source.Path);
            return false;
        }
        blocks.Clear().Append(currentBlocks);
        markup.Clear().Append(currentMarkup);
        blockRanges.Clear();
        blockRanges.AddRange(ranges);
        return true;
    }

    internal static string? FindPaperFile(string name, string label, string pagePath, string projectRoot, DiagnosticBag diagnostics)
    {
        var pageDirectory = Path.GetDirectoryName(Path.GetFullPath(pagePath))!;
        var box = AncestorBox(pageDirectory, projectRoot);
        var root = box ?? projectRoot;
        var fileName = name + Extension;
        var candidates = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) == fileName && (box is not null || !InsideBox(file, projectRoot))
                && !Path.GetRelativePath(root, file).Replace('\\', '/').StartsWith("out/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 1) return candidates[0];
        diagnostics.Error(candidates.Length == 0
            ? $"{label} « {name} » introuvable : aucun fichier {fileName} dans le projet"
            : $"{label} « {name} » ambigu : {candidates.Length} fichiers {fileName} dans le projet", pagePath);
        return null;
    }

    [GeneratedRegex(@"^[ \t]*@layout[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]*;[ \t]*\r?\n?", RegexOptions.Multiline)]
    private static partial Regex LayoutDirective();

    [GeneratedRegex(@"<slot\s*/>|<slot\s*>\s*</slot\s*>")]
    private static partial Regex SlotTag();

    [GeneratedRegex(@"\bgear\s+load\s*\(")]
    private static partial Regex LayoutLoad();
}
