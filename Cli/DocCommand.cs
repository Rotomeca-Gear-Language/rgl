using Rgl.Diagnostics;
using Rgl.Docs;
using Rgl.Project;
using Rgl.Text;

namespace Rgl.Cli;

/// <summary>
/// <c>rgl doc build</c> et <c>rgl doc check</c> : lisent les commentaires <c>///</c> du projet, de ses fichiers importés et de la std
/// (pour résoudre les références), puis vérifient ceux du projet ou écrivent la référence de son API. La documentation n'est
/// jamais lue par <c>compile</c> : elle ne fait pas échouer un build.
/// </summary>
internal static class DocCommand
{
    /// <summary>Dossier, dans <c>out</c>, où <c>rgl doc build</c> écrit.</summary>
    private const string OutputFolder = "docs";

    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var load = new DiagnosticBag();
        var documents = new DiagnosticBag();
        var code = Load(commandLine, context, load, documents, out var project, out var entries, out var own);
        _Report(load, context);
        if (code != CliRunner.Success) return code;

        _Report(documents, context);
        var documented = entries.Where(e => own.Contains(e.Module) && e.Comment is not null).ToList();
        var ownEntries = entries.Where(e => own.Contains(e.Module)).ToList();

        if (commandLine.Doc == DocAction.Check)
        {
            var warnings = documents.Items.Count(d => d.Level == DiagnosticLevel.Warning);
            context.Out.WriteLine($"Documentation : {documented.Count} élément(s) documenté(s) sur {ownEntries.Count}, {warnings} avertissement(s).");
            return commandLine.Strict && warnings > 0 ? CliRunner.Failure : CliRunner.Success;
        }

        var folder = Path.Combine(project!.RootDirectory, "out", OutputFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "api.json"), DocExporter.Json(project.Name, ownEntries));
        File.WriteAllText(Path.Combine(folder, "api.xml"), DocExporter.Xml(project.Name, ownEntries));
        context.Out.WriteLine($"Documentation écrite dans {Path.GetRelativePath(context.WorkingDirectory, folder)} (api.json, api.xml) : {documented.Count} élément(s) documenté(s) sur {ownEntries.Count}.");
        return CliRunner.Success;
    }

    /// <summary>
    /// Charge le projet et lit la documentation de tous ses fichiers. Les erreurs du projet vont à <paramref name="load"/>, les
    /// problèmes de documentation à <paramref name="documents"/>.
    /// </summary>
    private static int Load(CommandLine commandLine, CliContext context, DiagnosticBag load, DiagnosticBag documents,
        out RglProject? project, out IReadOnlyList<DocEntry> entries, out HashSet<string> own)
    {
        entries = [];
        own = [];
        project = RglProject.Load(commandLine.File, commandLine.Directory, context.WorkingDirectory, load);
        if (project is null) return CliRunner.Failure;

        var source = SourceText.FromPath(project.EntryFile, load);
        if (source is null) return CliRunner.Failure;

        var modules = SourceGraph.Load(source, project.RootDirectory, load, project.Manifest?.Exclude);
        if (modules is null) return CliRunner.Failure;

        var all = new List<DocEntry>();
        foreach (var module in modules)
        {
            // Les commentaires de la std ne se vérifient pas ici : ils ne servent qu'à résoudre les références.
            var isOwn = !StdLibrary.Contains(module.Unit.Span.Source.Path);
            all.AddRange(DocCollector.Collect(module.Name, module.Unit, isOwn ? documents : null));
            if (isOwn) own.Add(module.Name);
        }

        DocValidator.Validate(all, documents, own);
        entries = all;
        return CliRunner.Success;
    }

    private static void _Report(DiagnosticBag diagnostics, CliContext context) =>
        new DiagnosticRenderer(context.WorkingDirectory, context.Color).Write(context.Error, diagnostics.Items);
}
