using Rgl.Diagnostics;
using Rgl.Project;

namespace Rgl.Tests;

/// <summary>Les fichiers <c>nom.test.rgl</c> : lancés seuls, jamais point d'entrée du dossier, avec leur propre nom de projet.</summary>
public class TestFileTests
{
    private const string Manifest = "{ \"name\": \"calc\", \"version\": \"1.0.0\", \"rgl\": \"0\" }";

    [Fact]
    public void Un_fichier_de_test_ne_compte_pas_comme_point_d_entree_du_dossier()
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", Manifest);
        dir.File("main.rgl", "train |> done;");
        dir.File("addition.test.rgl", "train |> done;");
        var diagnostics = new DiagnosticBag();

        var project = RglProject.Load(null, dir.Path, dir.Path, diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal("main.rgl", Path.GetFileName(project!.EntryFile));
        Assert.Equal("calc", project.Name);
    }

    [Fact]
    public void Un_fichier_de_test_donne_directement_a_son_propre_nom_de_projet()
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", Manifest);
        dir.File("main.rgl", "train |> done;");
        var test = dir.File("addition.test.rgl", "train |> done;");
        var diagnostics = new DiagnosticBag();

        var project = RglProject.Load(test, null, dir.Path, diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal("calc-addition", project!.Name);
        Assert.True(RglProject.IsTestFile(test));
        Assert.False(RglProject.IsTestFile(Path.Combine(dir.Path, "main.rgl")));
    }
}
