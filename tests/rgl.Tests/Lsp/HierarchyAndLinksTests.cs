using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Hiérarchie d'appels (qui appelle, ce qui est appelé) et liens des imports.</summary>
public class HierarchyAndLinksTests
{
    private const string Maths = "forge: gears\ndescription: ''\n\ngear deux(int n): int => n * 2;\ngear quatre(int n): int => deux(deux(n));\n";
    private const string Main = "import Maths only {deux, quatre};\n\ngear a(int n): int => deux(n);\ngear b(int n): int => quatre(n) + deux(n);\n\n!train |> done;\n";

    private static (SemanticDocument Document, TempDirectory Directory) Open(string main)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Maths.forge", Maths);
        var path = directory.File("main.rgl", main);
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, main, project), directory);
    }

    [Fact]
    public void Les_appelants_sont_regroupes_par_gear_avec_chaque_appel()
    {
        var (document, directory) = Open(Main);
        using (directory)
        {
            var item = CallHierarchyProvider.Prepare(document, Main.IndexOf("deux(n);", StringComparison.Ordinal))!;
            Assert.Equal("deux", item.Name);
            Assert.Equal("Maths", item.Detail);

            var maths = SemanticDocument.Create(item.Path, item.Source.Content, new ProjectIndex().Open(item.Path, _ => null));
            var callers = CallHierarchyProvider.Incoming(maths, item).ToDictionary(e => e.Item.Name, e => e.Calls.Count);

            Assert.Equal(1, callers["a"]);
            Assert.Equal(1, callers["b"]);
            Assert.Equal(2, callers["quatre"]);
        }
    }

    [Fact]
    public void Les_appels_sortants_listent_ce_que_le_gear_appelle()
    {
        var (document, directory) = Open(Main);
        using (directory)
        {
            var item = CallHierarchyProvider.Prepare(document, Main.IndexOf("gear b", StringComparison.Ordinal) + 5)!;

            var callees = CallHierarchyProvider.Outgoing(document, item).ToDictionary(e => e.Item.Name, e => e.Calls.Count);

            Assert.Equal(new Dictionary<string, int> { ["quatre"] = 1, ["deux"] = 1 }, callees);
        }
    }

    [Fact]
    public void Un_mot_qui_n_est_pas_un_gear_n_a_pas_de_hierarchie()
    {
        var (document, directory) = Open(Main);
        using (directory)
        {
            Assert.Null(CallHierarchyProvider.Prepare(document, Main.IndexOf("int n", StringComparison.Ordinal) + 4));
            Assert.Null(CallHierarchyProvider.Prepare(document, 0));
        }
    }

    [Fact]
    public void Un_import_est_un_lien_vers_son_fichier()
    {
        var (document, directory) = Open(Main);
        using (directory)
        {
            var link = Assert.Single(DocumentLinkProvider.Of(document));

            Assert.Equal("Maths", link.Range.Text);
            Assert.EndsWith("Maths.forge", link.Target);
        }
    }

    [Fact]
    public void Un_use_mene_a_la_std_et_un_import_json_au_fichier_de_donnees()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("data/config.json", "{\"a\": 1}");
        var source = "import json config;\n!train |> done;\n";
        var path = directory.File("main.rgl", source);
        var document = SemanticDocument.Create(path, source, new ProjectIndex().Open(path, _ => null));

        var link = Assert.Single(DocumentLinkProvider.Of(document));

        Assert.EndsWith("config.json", link.Target);
    }
}
