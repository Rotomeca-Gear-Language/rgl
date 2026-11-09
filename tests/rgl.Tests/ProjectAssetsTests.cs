using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

public class ProjectAssetsTests
{
    [ZigFact]
    public void Un_build_copie_les_ressources_a_cote_de_l_executable()
    {
        using var directory = new TempDirectory();
        using var cache = new TempDirectory();
        var entry = directory.File("main.rgl", "!train |> done;");
        directory.File("front/html/index.html", "page");
        var project = new RglProject(directory.Path, entry,
            new ProjectManifest("web", "1", null, "0", Assets: ["front/html/**"]));
        var diagnostics = new DiagnosticBag();

        var built = BuildPipeline.Run(project, cache.Path, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);

        Assert.NotNull(built);
        Assert.DoesNotContain(diagnostics.Items, d => d.Level == DiagnosticLevel.Error);
        Assert.Equal("page", File.ReadAllText(Path.Combine(directory.Path, "out/debug/front/html/index.html")));
    }

    [Fact]
    public void Les_motifs_copient_les_fichiers_en_conservant_leur_chemin()
    {
        using var directory = new TempDirectory();
        directory.File("front/html/index.html", "index");
        directory.File("front/html/base/layout.html", "layout");
        directory.File("front/css/site.css", "css");
        directory.File("out/debug/ancien.html", "ancien");
        var manifest = new ProjectManifest("web", "1", null, "0", Assets: ["front/html/**"]);
        var project = new RglProject(directory.Path, directory.File("main.rgl", "!train |> done;"), manifest);
        var diagnostics = new DiagnosticBag();

        var assets = ProjectAssets.Resolve(project, diagnostics);
        var executable = directory.File("out/debug/web", "bin");
        Assert.True(ProjectAssets.Copy(assets!, executable, diagnostics));

        Assert.Empty(diagnostics.Items);
        Assert.Equal("index", File.ReadAllText(Path.Combine(directory.Path, "out/debug/front/html/index.html")));
        Assert.Equal("layout", File.ReadAllText(Path.Combine(directory.Path, "out/debug/front/html/base/layout.html")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "out/debug/front/css/site.css")));
    }

    [Fact]
    public void Un_motif_sans_fichier_est_une_erreur()
    {
        using var directory = new TempDirectory();
        var project = new RglProject(directory.Path, directory.File("main.rgl", "!train |> done;"),
            new ProjectManifest("web", "1", null, "0", Assets: ["front/html/**"]));
        var diagnostics = new DiagnosticBag();

        Assert.Null(ProjectAssets.Resolve(project, diagnostics));
        Assert.True(diagnostics.HasErrors);
    }
}
