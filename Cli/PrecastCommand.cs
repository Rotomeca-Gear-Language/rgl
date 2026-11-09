using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.Project;
using Rgl.Text;

namespace Rgl.Cli;

/// <summary>
/// <c>rgl precast export [fichier.rgl | -d dossier] [-o fichier]</c> : lie le projet et écrit ses precasts, datasets et constantes de données en JSON
/// (<see cref="PrecastExporter"/>), dans <c>out/precasts.json</c> par défaut. Ceux que le programme ne lit pas y sont aussi : un outil veut tout le jeu de données.
/// </summary>
internal static class PrecastCommand
{
    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var project = RglProject.Load(commandLine.File, commandLine.Directory, context.WorkingDirectory, diagnostics);
        if (project is null) return CliRunner.Failure;

        var source = SourceText.FromPath(project.EntryFile, diagnostics);
        if (source is null) return CliRunner.Failure;
        var manifest = project.Manifest;
        var policy = new NativePolicy(manifest, project.RootDirectory);
        var modules = SourceGraph.Load(source, project.RootDirectory, diagnostics, manifest?.Exclude, toolchain: context.Toolchain, nativeCache: Path.Combine(context.CacheRoot, "native"), nativePolicy: policy);
        if (modules is null) return CliRunner.Failure;
        var program = new Binder(diagnostics, manifest?.Overflow ?? OverflowMode.Checked,
            manifest is null ? ProjectManifest.DefaultRecursionLimit : manifest.RecursionLimit, manifest?.Safe ?? false, manifest?.BoundedLoops ?? false, policy).Bind(modules);
        if (program is null) return CliRunner.Failure;

        var (json, skipped) = PrecastExporter.Export(project.Name, project.RootDirectory, program.Precasts);
        var target = Path.GetFullPath(commandLine.Output ?? Path.Combine("out", "precasts.json"), commandLine.Output is null ? project.RootDirectory : context.WorkingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, json);
        foreach (var name in skipped) context.Error.WriteLine($"« {name} » : valeur non exportable, écrite null");
        context.Out.WriteLine($"{program.Precasts.Count} precast(s) écrit(s) dans {Path.GetRelativePath(context.WorkingDirectory, target)}.");
        return CliRunner.Success;
    }
}
