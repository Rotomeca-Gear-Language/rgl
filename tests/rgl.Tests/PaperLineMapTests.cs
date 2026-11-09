using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Paper;
using Rgl.Project;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>
/// Le débogueur d'un programme Paper parle de la page, pas du module que le compilateur en engendre : les <c>#line</c> du C++ (et les positions des erreurs)
/// portent les numéros de ligne de la page.
/// </summary>
public class PaperLineMapTests
{
    private static readonly string Page = string.Join("\n",
        "<h1>Titre</h1>",
        "@rgl {",
        "    const string title: 'T';",
        "    reactive int count: 0;",
        "",
        "    gear increase(): void {",
        "        count +<- 1;",
        "    }",
        "}",
        "<p>@@ title @@</p>",
        "");

    private static (SourceText Source, int[] Lines) Generated(string page)
    {
        using var directory = new TempDirectory();
        var path = directory.File("Home.paper", page);
        var diagnostics = new DiagnosticBag();
        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, directory.Path, diagnostics)!;
        return (document.Source, [.. document.Source.LineMap!.Select(l => (int)l)]);
    }

    private static int LineContaining(SourceText source, string text) =>
        Array.FindIndex(source.Content.Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1;

    [Fact]
    public void Les_lignes_d_un_bloc_et_d_une_interpolation_retrouvent_leur_ligne_de_la_page()
    {
        var (source, map) = Generated(Page);

        Assert.Equal(3, map[LineContaining(source, "const string title") - 1]);
        Assert.Equal(7, map[LineContaining(source, "count +<- 1") - 1]);
        Assert.Equal(6, map[LineContaining(source, "gear increase") - 1]);
        Assert.Equal(10, map[LineContaining(source, "gear __paper_fragment_0") - 1]);
    }

    [Fact]
    public void Le_code_que_la_page_n_ecrit_pas_n_a_pas_de_ligne_et_prend_la_ligne_1()
    {
        var (source, map) = Generated(Page);
        var render = LineContaining(source, "export gear render(");

        Assert.Equal(0, map[render - 1]);
        Assert.Equal(1u, (uint)source.OriginalLine(Rotomeca.Utils.Types.RUint.CreateChecked((uint)render)).Value);
        // Un fichier ordinaire n'a pas de correspondance : la ligne reste la même.
        Assert.Equal(5u, new SourceText("a.rgl", "x").OriginalLine(Rotomeca.Utils.Types.RUint.CreateChecked(5u)).Value);
    }

    [Fact]
    public void Une_declaration_reactive_sur_plusieurs_lignes_ne_decale_pas_la_suite()
    {
        var page = string.Join("\n", "@rgl {", "    reactive int", "        count: 0;", "    const string apres: 'x';", "}", "");

        var (source, map) = Generated(page);

        Assert.Equal(4, map[LineContaining(source, "const string apres") - 1]);
    }

    [Fact]
    public void Le_cpp_engendre_pointe_les_lignes_de_la_page()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        ProjectInitializer.Init(project.Path, new DiagnosticBag(), web: true);
        var pageLines = File.ReadAllText(Path.Combine(project.Path, "src", "Home.paper")).ReplaceLineEndings("\n").Split('\n');
        var increment = Array.FindIndex(pageLines, l => l.Contains("count +<- 1", StringComparison.Ordinal)) + 1;
        var title = Array.FindIndex(pageLines, l => l.Contains("const string title", StringComparison.Ordinal)) + 1;

        var exit = CliRunner.Run(["compile", "-d", ".", "--emit-cpp"], new CliContext(new StringWriter(), new StringWriter(), project.Path, cache.Path));

        Assert.Equal(CliRunner.Success, exit);
        var cpp = File.ReadAllLines(Assert.Single(Directory.GetFiles(cache.Path, "main.cpp", SearchOption.AllDirectories)));
        // La ligne de C++ qui suit un #line vers la page porte le code de la ligne de la page.
        var home = cpp.Select((text, index) => (text, index)).Where(l => l.text.StartsWith("#line ", StringComparison.Ordinal) && l.text.Contains("Home.paper", StringComparison.Ordinal)).ToArray();
        Assert.Contains(home, l => l.text.StartsWith($"#line {increment} ", StringComparison.Ordinal) && cpp[l.index + 1].Contains("rgl_f_count", StringComparison.Ordinal));
        Assert.Contains(home, l => l.text.StartsWith($"#line {title} ", StringComparison.Ordinal) && cpp[l.index + 1].Contains("rgl_v_title", StringComparison.Ordinal));
        // Aucune directive ne dépasse la page.
        Assert.All(home, l => Assert.InRange(int.Parse(l.text.Split(' ')[1]), 1, pageLines.Length));
    }
}
