using System.Text;
using Rgl.Diagnostics;
using Rgl.Lsp;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Cli;

/// <summary>
/// <c>rgl format [chemin…] [--check]</c> : met en forme les fichiers <c>.rgl</c> et <c>.forge</c> d'un projet (ou les fichiers et dossiers donnés), avec la mise en
/// forme officielle (<see cref="Formatter"/>) : elle n'a aucun réglage, c'est la même que celle de l'éditeur. Un fichier qui a une erreur de syntaxe n'est pas
/// touché. Avec <c>--check</c>, rien n'est écrit : la commande liste les fichiers à mettre en forme et échoue s'il y en a (pour un contrôle en intégration continue).
/// </summary>
internal static class FormatCommand
{
    /// <summary>Les dossiers où la recherche ne descend pas : cachés, sortie des builds, dépendances d'outils.</summary>
    private static readonly string[] _skippedFolders = ["out", "node_modules"];

    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var roots = commandLine.Paths.Count > 0 ? commandLine.Paths : (IReadOnlyList<string>)["."];
        var files = new List<string>();
        foreach (var written in roots)
        {
            var full = Path.GetFullPath(written, context.WorkingDirectory);
            if (File.Exists(full))
            {
                if (!_IsSource(full)) diagnostics.Error($"« {written} » n'est pas un fichier .rgl ou .forge");
                else files.Add(full);
            }
            else if (Directory.Exists(full)) files.AddRange(_SourcesIn(full));
            else diagnostics.Error($"« {written} » n'existe pas");
        }
        if (diagnostics.HasErrors) return CliRunner.Failure;

        var changed = 0;
        var unchanged = 0;
        var skipped = 0;
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(context.WorkingDirectory, file);
            switch (_Format(file, write: !commandLine.CheckOnly, out var problem))
            {
                case Outcome.Changed:
                    context.Out.WriteLine((commandLine.CheckOnly ? "À mettre en forme : " : "Mis en forme : ") + relative);
                    changed++;
                    break;
                case Outcome.Unchanged:
                    unchanged++;
                    break;
                default:
                    context.Error.WriteLine($"{relative} : non mis en forme ({problem})");
                    skipped++;
                    break;
            }
        }

        context.Out.WriteLine(commandLine.CheckOnly
            ? $"{changed} fichier(s) à mettre en forme, {unchanged} conforme(s)" + (skipped > 0 ? $", {skipped} ignoré(s)." : ".")
            : $"{changed} fichier(s) mis en forme, {unchanged} inchangé(s)" + (skipped > 0 ? $", {skipped} ignoré(s)." : "."));
        if (skipped > 0) return CliRunner.Failure;
        return commandLine.CheckOnly && changed > 0 ? CliRunner.Failure : CliRunner.Success;
    }

    private enum Outcome { Changed, Unchanged, Skipped }

    private static bool _IsSource(string path) =>
        Path.GetExtension(path).Equals(".rgl", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".forge", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> _SourcesIn(string folder)
    {
        foreach (var file in Directory.GetFiles(folder).Where(_IsSource)) yield return file;
        foreach (var child in Directory.GetDirectories(folder))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || _skippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in _SourcesIn(child)) yield return file;
        }
    }

    /// <summary>Met un fichier en forme (l'écrit si <paramref name="write"/>), en gardant sa marque BOM éventuelle.</summary>
    private static Outcome _Format(string path, bool write, out string problem)
    {
        problem = "";
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        string content;
        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            problem = "ce n'est pas de l'UTF-8 valide";
            return Outcome.Skipped;
        }

        // Un fichier qui ne se lit pas jusqu'au bout n'a pas de structure à laquelle se fier.
        var syntax = new DiagnosticBag();
        var source = new SourceText(path, content);
        var tokens = new Lexer(source, syntax).Tokenize();
        if (!syntax.HasErrors) new Parser(tokens, syntax).Parse();
        if (syntax.HasErrors)
        {
            problem = "erreur de syntaxe";
            return Outcome.Skipped;
        }

        var formatted = Formatter.FormatText(path, content);
        if (formatted == content) return Outcome.Unchanged;
        if (write) File.WriteAllBytes(path, [.. (hasBom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. Encoding.UTF8.GetBytes(formatted)]);
        return Outcome.Changed;
    }
}
