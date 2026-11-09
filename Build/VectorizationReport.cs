using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Build;

/// <summary>
/// Les remarques de Clang sur la vectorisation des boucles (<c>-Rpass=loop-vectorize</c>), relues et rendues en diagnostics RGL
/// à la position de la boucle dans le source : une notice quand elle est vectorisée (I003), un avertissement avec la raison quand
/// elle ne l'est pas (W069). Seules les positions qui tombent dans un fichier RGL comptent (les directives <c>#line</c> du C++
/// généré y ramènent) : le runtime et la bibliothèque standard de C++ ne sont pas du programme de l'utilisateur.
/// </summary>
internal static partial class VectorizationReport
{
    [GeneratedRegex(@"^(?<path>.+?):(?<line>\d+):(?<column>\d+): remark: (?<text>.*?) \[-Rpass(?<kind>-missed|-analysis)?=loop-vectorize\]\s*$")]
    private static partial Regex RemarkPattern();

    private sealed class Loop
    {
        public bool Vectorized;
        public string? Width;
        public List<string> Reasons = [];
    }

    [GeneratedRegex(@"^#line (?<line>\d+) ""(?<path>.*)""\s*$")]
    private static partial Regex LinePattern();

    /// <summary>
    /// Où chaque ligne du C++ généré revient dans le source : ses directives <c>#line</c>, dans l'ordre. Une ligne physique du
    /// C++ (à partir de 1) tombe sur la dernière directive qui la précède.
    /// </summary>
    private static List<(int Physical, int Line, string Path)> Directives(string generated)
    {
        var result = new List<(int, int, string)>();
        var lines = generated.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = LinePattern().Match(lines[i].TrimEnd('\r'));
            if (match.Success) result.Add((i + 1, int.Parse(match.Groups["line"].Value), match.Groups["path"].Value.Replace("\\\\", "\\")));
        }
        return result;
    }

    /// <summary>Rend en diagnostics les remarques du journal de Clang <paramref name="log"/>, positions du C++ généré <paramref name="generated"/> ramenées au source.</summary>
    public static void Report(string log, string generated, DiagnosticBag diagnostics)
    {
        var directives = Directives(generated);
        var lineCount = generated.Split('\n').Length;
        var loops = new Dictionary<(string Path, int Line, int Column), Loop>();
        var order = new List<(string Path, int Line, int Column)>();

        foreach (var line in log.Split('\n'))
        {
            var match = RemarkPattern().Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            // Seul le C++ généré compte (le runtime et la bibliothèque de C++ ne sont pas du programme de l'utilisateur) ; sa ligne revient au source.
            if (!Path.GetFileName(match.Groups["path"].Value).Equals("main.cpp", StringComparison.OrdinalIgnoreCase)) continue;
            var physical = int.Parse(match.Groups["line"].Value);
            if (physical > lineCount || directives.LastOrDefault(d => d.Physical < physical) is not { Path: not null } origin) continue;

            // Le C++ d'une instruction tient en plusieurs lignes : la boucle se rapporte à la ligne de l'instruction (sa directive #line).
            var key = (origin.Path, origin.Line, 0);
            if (!loops.TryGetValue(key, out var loop))
            {
                loops[key] = loop = new Loop();
                order.Add(key);
            }

            var text = match.Groups["text"].Value;
            if (text.StartsWith("vectorized loop", StringComparison.Ordinal))
            {
                loop.Vectorized = true;
                loop.Width = Regex.Match(text, @"vectorization width: (\d+)").Groups[1].Value;
            }
            else if (text != "loop not vectorized" && !loop.Reasons.Contains(text)) loop.Reasons.Add(text);
        }

        var sources = new Dictionary<string, SourceText?>();
        foreach (var key in order)
        {
            var loop = loops[key];
            if (!sources.TryGetValue(key.Path, out var source)) sources[key.Path] = source = SourceText.FromPath(key.Path, new DiagnosticBag());
            if (source is null || key.Line < 1 || key.Line > source.LineStart.Count) continue;

            // La colonne du C++ ne dit rien du source : la boucle se montre à son premier caractère écrit.
            var first = source.LineStart[key.Line - 1].Value;
            var start = first;
            while (start < source.Content.Length && source.Content[(int)start] is ' ' or '\t') ++start;
            if (start >= source.Content.Length) continue;
            var span = new SourceSpan(source, new RUint(start), new RUint(1));

            if (loop.Vectorized)
                diagnostics.Info(span, DiagnosticCodes.LoopVectorized, $"boucle vectorisée{(string.IsNullOrEmpty(loop.Width) ? "" : $" ({loop.Width} éléments à la fois)")}", "vectorisée");
            else
            {
                var reasons = loop.Reasons.Select(Translate).Distinct().ToArray();
                diagnostics.Warning(span, DiagnosticCodes.LoopNotVectorized, "cette boucle n'est pas vectorisée", "non vectorisée", reasons.Length == 0 ? null : reasons);
            }
        }
    }

    /// <summary>La raison que donne Clang, en français quand elle est connue ; sinon son texte.</summary>
    private static string Translate(string reason)
    {
        var text = reason.StartsWith("loop not vectorized: ", StringComparison.Ordinal) ? reason["loop not vectorized: ".Length..] : reason;
        return text switch
        {
            var t when t.Contains("floating-point operations", StringComparison.Ordinal) =>
                "des opérations sur des flottants ne peuvent pas être réordonnées sans changer le résultat (une somme de flottants s'accumule dans l'ordre)",
            var t when t.Contains("unsafe dependent memory operations", StringComparison.Ordinal) =>
                "deux accès mémoire de la boucle peuvent se recouvrir : une colonne lue et une colonne écrite ne sont pas prouvées distinctes",
            var t when t.Contains("cannot identify array bounds", StringComparison.Ordinal) =>
                "Clang ne retrouve pas la taille des tableaux parcourus",
            var t when t.Contains("could not determine number of loop iterations", StringComparison.Ordinal) =>
                "le nombre de tours de la boucle n'est pas connu avant de l'entamer",
            var t when t.Contains("call instruction cannot be vectorized", StringComparison.Ordinal) =>
                "la boucle appelle une fonction que Clang n'a pas pu intégrer au corps de la boucle",
            var t when t.Contains("control flow cannot be substituted for a select", StringComparison.Ordinal) || t.Contains("control flow is not understood", StringComparison.Ordinal) =>
                "le corps de la boucle contient des branches que Clang ne sait pas transformer en calcul sans saut",
            var t when t.Contains("instruction cannot be vectorized", StringComparison.Ordinal) =>
                "une instruction du corps de la boucle n'a pas d'équivalent vectoriel (une vérification de débordement, un arrêt du programme…)",
            var t when t.Contains("cost-model", StringComparison.Ordinal) => "Clang estime que vectoriser cette boucle ne gagnerait rien",
            var t when t.Contains("use -Rpass-analysis", StringComparison.Ordinal) => "Clang ne dit pas pourquoi",
            _ => text,
        };
    }
}
