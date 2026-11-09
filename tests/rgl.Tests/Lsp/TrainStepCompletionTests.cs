using Rgl.Lsp;
using Rgl.Project;

namespace Rgl.Tests.Lsp;

/// <summary>La complétion d'un nom qualifié (<c>Fichier.nom</c>) écrit comme étape d'un train.</summary>
public class TrainStepCompletionTests
{
    private const string Calculs = "forge: gears\ndescription: ''\n\ngear ajoute(int n): int => n + 1;\ngear retire(int n): int => n - 1;\n";

    private const string Page = "@rgl {\n    const string title: 'RGL';\n}\n<h1>@@ title @@</h1>\n";

    /// <summary>Le curseur est à la place de <c>¤</c> (le <c>|</c> est celui des étapes d'un train).</summary>
    private static IReadOnlyList<string> Complete(string file, string content, string main)
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File(file, content);
        var text = main.Replace("¤", "");
        var path = directory.File("main.rgl", text);
        var offset = main.IndexOf('¤');
        var before = main[..offset];
        var project = new ProjectIndex().Open(path, _ => null);
        return [.. CompletionProvider.Complete(path, text, (uint)before.Count(c => c == '\n'), (uint)(offset - (before.LastIndexOf('\n') + 1)), project).Select(i => i.Label)];
    }

    [Theory]
    [InlineData("import Calculs;\ntrain\n|> Calculs.¤\n|> done;\n")]
    [InlineData("import Calculs;\ntrain 1\n|> Calculs.a¤\n|> done;\n")]
    [InlineData("import Calculs;\ntrain\n|> Calculs.a¤\n|> Calculs.retire\n|> done;\n")]
    public void Un_fichier_forge_propose_ses_noms_dans_une_etape_de_train(string main)
    {
        var labels = Complete("Calculs.forge", Calculs, main);

        Assert.Contains("ajoute", labels);
        Assert.DoesNotContain("pack", labels);
    }

    [Theory]
    [InlineData("import Home;\ntrain\n|> Home.¤\n|> done;\n")]
    [InlineData("import Home;\ntrain\n|> Home.m¤\n|> done;\n")]
    public void Une_page_paper_propose_ses_noms_dans_une_etape_de_train(string main)
    {
        var labels = Complete("Home.paper", Page, main);

        Assert.Contains("mount", labels);
        Assert.Contains("render", labels);
        Assert.DoesNotContain("pack", labels);
    }

    [Fact]
    public void Le_projet_web_du_squelette_propose_les_noms_de_sa_page()
    {
        using var directory = new TempDirectory();
        ProjectInitializer.Init(directory.Path, new Rgl.Diagnostics.DiagnosticBag(), web: true);
        var path = Path.Combine(directory.Path, "main.rgl");
        var text = File.ReadAllText(path).ReplaceLineEndings("\n").Replace("Home.mount('/')", "Home.m");
        var offset = text.IndexOf("Home.m", StringComparison.Ordinal) + "Home.m".Length;
        var before = text[..offset];

        var items = CompletionProvider.Complete(path, text, (uint)before.Count(c => c == '\n'), (uint)(offset - (before.LastIndexOf('\n') + 1)),
            new ProjectIndex().Open(path, _ => null));

        Assert.Contains(items, i => i.Label == "mount");
    }

    [Fact]
    public void Un_nom_qualifie_dans_une_expression_garde_son_comportement()
    {
        var labels = Complete("Calculs.forge", Calculs, "import Calculs;\ngear f(int n): int => Calculs.r¤;\n!train |> done;\n");

        Assert.Contains("retire", labels);
    }
}
