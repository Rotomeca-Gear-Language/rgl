using Rgl.Binding;
using Rgl.Build.Icons;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Native;
using Rgl.Parsing;
using Rgl.Paper;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Build;

/// <summary>Ce que produit un build : où il s'arrête.</summary>
internal enum BuildOutput
{
    /// <summary>Jusqu'à l'exécutable.</summary>
    Executable,

    /// <summary>Seulement le C++ généré (<c>--emit-cpp</c>), sans appel à Clang.</summary>
    Cpp,

    /// <summary>Le C++ généré et son assembleur (<c>--emit-asm</c>), sans édition de liens.</summary>
    Assembly,

    /// <summary>Le C++ compilé avec optimisations, et les remarques de Clang sur la vectorisation des boucles (<c>--explain-vectorization</c>) rendues en diagnostics.</summary>
    Vectorization,

    /// <summary>Seulement la transpilation, et ce que le compilateur a optimisé ou refusé d'optimiser (<c>--explain-optimizations</c>), rendu en diagnostics.</summary>
    Optimizations,
}

/// <summary>Résultat d'un build réussi.</summary>
/// <param name="CppDirectory">Dossier du cache où le C++ généré est écrit.</param>
/// <param name="Executable">Binaire produit ; absent avec <c>--emit-cpp</c> et <c>--emit-asm</c>.</param>
/// <param name="Assembly">Assembleur produit avec <c>--emit-asm</c> ; absent sinon.</param>
internal sealed record BuildResult(string CppDirectory, string? Executable, string? Assembly = null);

/// <summary>Chaîne complète d'un build : source RGL → C++ dans le cache → binaire.</summary>
internal static class BuildPipeline
{
    /// <summary>Lance le build ; renvoie <c>null</c> si une erreur a été signalée.</summary>
    /// <param name="project">Projet à compiler.</param>
    /// <param name="cacheRoot">Racine du cache, où le C++ est écrit.</param>
    /// <param name="output">Où le build s'arrête : exécutable, C++ seul, ou assembleur.</param>
    /// <param name="profile">Debug (informations pour déboguer) ou release (vitesse, puis taille).</param>
    /// <param name="toolchain">Chaîne de compilation C++ ; <see langword="null"/> si elle est introuvable.</param>
    /// <param name="diagnostics">Reçoit les diagnostics du build.</param>
    public static BuildResult? Run(RglProject project, string cacheRoot, BuildOutput output, BuildProfile profile, ICompilerToolchain? toolchain, DiagnosticBag diagnostics)
    {
        var assets = ProjectAssets.Resolve(project, diagnostics);
        if (assets is null) return null;
        var projectDirectory = Cache.ProjectDirectory(cacheRoot, project);
        var cppDirectory = Path.Combine(projectDirectory, "cpp");
        Directory.CreateDirectory(cppDirectory);

        var source = SourceText.FromPath(project.EntryFile, diagnostics);
        if (source is null) return null;

        // Le fichier d'entrée et les fichiers qu'il importe, lus et analysés : le binder ne reçoit que des arbres complets.
        var modules = SourceGraph.Load(source, project.RootDirectory, diagnostics, project.Manifest?.Exclude, toolchain: toolchain, nativeCache: Path.Combine(cacheRoot, "native"), nativePolicy: new NativePolicy(project.Manifest, project.RootDirectory));
        if (modules is null) return null;

        var manifest = project.Manifest;
        var program = new Binder(diagnostics, manifest?.OverflowFor(profile) ?? OverflowMode.Checked,
            manifest is null ? ProjectManifest.DefaultRecursionLimit : manifest.RecursionLimit, manifest?.Safe ?? false, manifest?.BoundedLoops ?? false, new NativePolicy(manifest, project.RootDirectory)).Bind(modules);
        if (program is null) return null;

        var mainCpp = Path.Combine(cppDirectory, "main.cpp");
        var cpp = CppEmitter.Emit(program, project.EntryFile);
        File.WriteAllText(mainCpp, cpp.Code);
        foreach (var unit in cpp.NativeUnits ?? []) File.WriteAllText(Path.Combine(cppDirectory, unit.Name + ".rgl.gen.cpp"), unit.Code);
        if (output == BuildOutput.Optimizations)
        {
            foreach (var note in cpp.Notes ?? [])
            {
                if (note.Refused) diagnostics.Warning(note.Span, DiagnosticCodes.OptimizationRefused, note.Message, "non optimisé", note.Reasons?.ToArray());
                else diagnostics.Info(note.Span, DiagnosticCodes.OptimizationApplied, note.Message, "optimisé");
            }
            return new BuildResult(cppDirectory, null);
        }
        if (output == BuildOutput.Cpp) return new BuildResult(cppDirectory, null);

        if (toolchain is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, "chaîne de compilation introuvable : Zig n'est pas installé",
                Help: ["lance : rgl setup",
                       "« --emit-cpp » produit le C++ sans avoir besoin de Zig"]));
            return null;
        }

        var profileName = profile.ToString().ToLowerInvariant();
        var log = Path.Combine(projectDirectory, "build.log");
        ToolchainResult result;

        if (output == BuildOutput.Vectorization)
        {
            result = toolchain.ExplainVectorization(mainCpp, profile, cpp.Natives);
            File.WriteAllText(log, result.Log);
            if (result.Success)
            {
                VectorizationReport.Report(result.Log, File.ReadAllText(mainCpp), diagnostics);
                return new BuildResult(cppDirectory, null);
            }
        }
        else if (output == BuildOutput.Assembly)
        {
            // L'assembleur reste dans le cache, rangé par profil : ses optimisations sont celles du profil.
            var assembly = Path.Combine(projectDirectory, "asm", profileName, "main.s");
            result = toolchain.EmitAssembly(mainCpp, assembly, profile, cpp.Natives);
            File.WriteAllText(log, result.Log);
            if (result.Success) return new BuildResult(cppDirectory, null, assembly);
        }
        else
        {
            // L'exécutable va dans le projet (out/debug, out/release) ; le cache ne garde que les fichiers intermédiaires.
            var executable = Path.Combine(project.RootDirectory, "out", profileName, OperatingSystem.IsWindows() ? project.Name + ".exe" : project.Name);
            // L'icône du programme : celle du manifeste, ou celle de RGL par défaut.
            var icon = ProgramIcon.Prepare(project, Path.Combine(projectDirectory, "icon"), diagnostics);

            // Le C++ des composants écrits en C++ se compile d'abord, avec les exceptions : s'il ne compile pas, c'est l'erreur du fichier de l'utilisateur.
            List<CLibrary> natives = [.. cpp.Natives ?? []];
            foreach (var unit in cpp.NativeUnits ?? [])
            {
                var generated = Path.Combine(cppDirectory, unit.Name + ".rgl.gen.cpp");
                var objectFile = Path.Combine(cppDirectory, unit.Name + ".rgl.o");
                var compiled = toolchain.CompileNativeUnit(generated, objectFile, profile, [Path.GetDirectoryName(unit.SourcePath)!, .. unit.IncludeDirectories ?? []]);
                File.WriteAllText(log, compiled.Log);
                if (!compiled.Success)
                {
                    var lines = compiled.Log.Split(new[] { (char)10 }, StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("$ ", StringComparison.Ordinal)).Take(8).Select(l => l.TrimEnd()).ToArray();
                    diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, $"le C++ de {Path.GetFileName(unit.SourcePath)} ne compile pas", unit.SourcePath, Code: DiagnosticCodes.NativeCodeFailed, Help: lines));
                    return null;
                }
                natives.Add(new CLibrary([], [], objectFile));
            }

            result = toolchain.BuildExecutable(mainCpp, executable, profile, cpp.Components, icon, natives);
            File.WriteAllText(log, result.Log);
            if (result.Success)
            {
                if (!ProjectAssets.Copy(assets, executable, diagnostics)) return null;
                if (PaperCompiler.ProjectUsesPaper(project.RootDirectory))
                {
                    var outputDirectory = Path.GetDirectoryName(executable)!;
                    if (!PaperBrowserRuntime.CopyTo(outputDirectory, diagnostics)
                        || !PaperWebAssembly.Build(toolchain, cppDirectory, outputDirectory, profile, diagnostics)) return null;
                }
                return new BuildResult(cppDirectory, executable);
            }
        }

        // Le binder a tout vérifié : si le C++ ne compile pas, c'est un bug du transpileur, jamais
        // une erreur de l'utilisateur. La sortie brute de Clang n'est pas montrée, elle est dans le journal.
        diagnostics.Add(new Diagnostic(DiagnosticLevel.Error,
            $"erreur interne du compilateur : le C++ généré ne compile pas avec {toolchain.Name}", project.EntryFile,
            Help: ["c'est un bug du transpileur, pas une erreur dans ton programme",
                   $"le détail est dans {log}, et le C++ dans {mainCpp}"]));
        return null;
    }
}
