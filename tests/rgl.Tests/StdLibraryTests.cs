using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>La bibliothèque standard embarquée : <c>use &lt;Nom&gt;;</c> désigne un de ses fichiers ou une de ses boxes, sans dire lequel.</summary>
public class StdLibraryTests
{
    private static (IReadOnlyList<Rgl.Binding.SourceModule>? Modules, DiagnosticBag Diagnostics) Load(string main, params (string Name, string Content)[] files)
    {
        using var directory = new TempDirectory();
        foreach (var (name, content) in files) File.WriteAllText(Path.Combine(directory.Path, name), content);
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(directory.Path, "main.rgl"), main), directory.Path, diagnostics);
        return (modules, diagnostics);
    }

    [Fact]
    public void Use_designe_une_box_de_la_std_sans_la_nommer_box()
    {
        var (modules, diagnostics) = Load("use <Math> only {abs};\n!train |> done;");

        Assert.NotNull(modules);
        Assert.Contains(modules, m => m.Name == "std.Math.box.index");
    }

    [Fact]
    public void Use_designe_un_fichier_de_la_std()
    {
        var (modules, _) = Load("use <Console> only {writeLine};\n!train |> done;");

        Assert.NotNull(modules);
        Assert.Contains(modules, m => m.Name == "std.Console");
    }

    [Fact]
    public void Use_ne_voit_pas_un_fichier_a_l_interieur_d_une_box()
    {
        var (modules, diagnostics) = Load("use <Private>;\n!train |> done;");

        Assert.Null(modules);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.ImportNotFound && d.Message.Contains("Private"));
    }

    [Fact]
    public void Use_d_un_nom_inconnu_liste_ce_que_la_std_contient()
    {
        var (modules, diagnostics) = Load("use <Inconnu>;\n!train |> done;");

        Assert.Null(modules);
        Assert.Contains(diagnostics.Items, d => d.Help?.Any(h => h.Contains("Math")) == true);
    }

    [Fact]
    public void Import_ne_cherche_plus_dans_la_std_et_dit_d_ecrire_use()
    {
        var (modules, diagnostics) = Load("import Math;\n!train |> done;");

        Assert.Null(modules);
        Assert.Contains(diagnostics.Items, d => d.Help?.Any(h => h.Contains("use <Math>")) == true);
    }

    [Fact]
    public void Un_fichier_du_projet_et_un_fichier_de_la_std_ne_se_melangent_pas()
    {
        var (modules, _) = Load("import Math;\n!train |> done;", ("Math.forge", "forge: gears\ndescription: \"local\"\ngear pure twice(int x): int => x * 2;\n"));

        Assert.NotNull(modules);
        // L'import désigne le fichier du projet, jamais celui de la std (liée aussi, pour std.nom).
        Assert.Equal(["Math"], modules[^1].Imports!.Values.Single());
    }
}
