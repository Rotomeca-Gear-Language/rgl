using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary><c>import file "nom.ext" as contenu;</c> lit un fichier UTF-8 à la compilation.</summary>
public class ImportFileTests
{
    private static (CppProgram? Program, string[] Errors) Build(TempDirectory directory)
    {
        var diagnostics = new DiagnosticBag();
        var path = Path.Combine(directory.Path, "main.rgl");
        var source = new SourceText(path, File.ReadAllText(path));
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        var errors = diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code} {d.Message}").ToArray();
        return (program is null ? null : CppEmitter.Emit(program, "main.rgl"), errors);
    }

    [ZigFact]
    public void Le_contenu_devient_une_constante_string_et_conserve_ses_caracteres()
    {
        using var directory = new TempDirectory();
        const string content = "<h1>Héros \\\"RGL\\\"</h1>\n\\chemin\tfin";
        directory.File("main.rgl", "import file \"page.html\" as page;\ngear afficher(): void { page -> console; }\nemit fin(): uint => 0;\n!train |> afficher |> fin;");
        directory.File("assets/page.html", content);

        var (program, errors) = Build(directory);

        Assert.True(program is not null, string.Join(", ", errors));
        var cpp = Path.Combine(directory.Path, "main.cpp");
        File.WriteAllText(cpp, program!.Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), cpp);

        Assert.Equal((0, content), ZigBuild.Run(executable));
    }

    [Fact]
    public void Le_premier_fichier_de_meme_nom_est_choisi_dans_un_ordre_deterministe()
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", "import file \"message.txt\" as message;\ngear contenu(): string => message;\n!train |> done;");
        directory.File("b/message.txt", "second");
        directory.File("a/message.txt", "premier");

        var (program, errors) = Build(directory);

        Assert.True(program is not null, string.Join(", ", errors));
        Assert.Contains("premier", program!.Code);
        Assert.DoesNotContain("second", program.Code);
    }

    [Fact]
    public void Une_box_est_opaque_et_les_crochets_cherchent_dans_le_projet()
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", "import box Site;\n!train |> done;");
        directory.File("racine.txt", "dehors");
        directory.File("Site.box/index.forge", "forge: index\ndescription: \"site\"\n\nimport Page;\n");
        directory.File("Site.box/Page.forge", "forge: gears\ndescription: \"page\"\n\nimport file \"interne.txt\" as interne;\nimport [..] file \"racine.txt\" as racine;\ngear textes(): string => `{interne}|{racine}`;\n");
        directory.File("Site.box/assets/interne.txt", "dedans");

        var (_, errors) = Build(directory);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("import file \"absent.txt\" as texte;\n!train |> done;")]
    [InlineData("import file \"Page.html\" as page;\n!train |> done;")]
    public void Un_fichier_absent_ou_de_mauvaise_casse_est_signale(string main)
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", main);
        directory.File("page.html", "x");

        Assert.Contains(Build(directory).Errors, e => e.StartsWith("E107", StringComparison.Ordinal));
    }
}
