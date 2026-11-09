using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Références, surlignage, renommage, pliage, extension de sélection et symboles de l'espace de travail.</summary>
public class NavigationTests
{
    private const string Shapes = "forge: gears\ndescription: \"Formes\"\n\nmold Vec { double x; double y; }\n\ngear norme(Vec v): double => v.x + v.y;\n";

    private static (SemanticDocument Document, TempDirectory Directory, int Offset) Open(string main, params (string Name, string Content)[] files)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        foreach (var (name, content) in files) directory.File(name, content);
        var offset = main.IndexOf('|');
        var text = main.Remove(offset, 1);
        var path = directory.File("main.rgl", text);
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, text, project), directory, offset);
    }

    private static string[] Lines(ReferenceSet set) => [.. set.Occurrences.Select(o => Path.GetFileName(o.Path) + ":" + o.Source[o.Span.Start].Line.Value)];

    [Fact]
    public void Une_variable_ne_se_melange_pas_avec_celle_d_un_autre_gear()
    {
        var (document, directory, offset) = Open("gear a(): int {\n    int n|: 1;\n    return n + n;\n}\ngear b(): int {\n    int n: 2;\n    return n;\n}\n!train |> done;");
        using (directory)
        {
            var set = ReferenceFinder.Find(document, offset)!;

            Assert.Equal(3, set.Occurrences.Count);
            Assert.Equal(1, set.Occurrences.Count(o => o.IsDeclaration));
            Assert.All(set.Occurrences, o => Assert.True(o.Source[o.Span.Start].Line.Value <= 3));
        }
    }

    [Fact]
    public void Un_gear_importe_a_ses_emplois_dans_tous_les_fichiers_du_projet()
    {
        var other = "import Shapes only {norme};\n\ngear dedans(): double => norme({x: 1.0, y: 2.0});\n";
        var (document, directory, offset) = Open("import Shapes only {norme};\ngear f(): double => no|rme({x: 3.0, y: 4.0});\n!train |> done;", ("Shapes.forge", Shapes), ("Other.forge", "forge: gears\ndescription: \"Autre\"\n\n" + other));
        using (directory)
        {
            var set = ReferenceFinder.Find(document, offset)!;
            var places = Lines(set);

            Assert.Contains("Shapes.forge:6", places);
            Assert.Contains("Other.forge:4", places);
            Assert.Contains("main.rgl:1", places);
            Assert.Contains("main.rgl:2", places);
            Assert.Equal(5, set.Occurrences.Count);
        }
    }

    [Fact]
    public void Un_champ_se_retrouve_dans_sa_declaration_et_ses_acces()
    {
        var (document, directory, offset) = Open("import Shapes only {Vec};\ngear f(Vec v): double => v.x| + v.y;\n!train |> done;", ("Shapes.forge", Shapes));
        using (directory)
        {
            var set = ReferenceFinder.Find(document, offset)!;
            var places = Lines(set);

            Assert.Contains("Shapes.forge:4", places);
            Assert.Contains("Shapes.forge:6", places);
            Assert.Contains("main.rgl:2", places);
            Assert.Equal(RglSymbolKind.Field, set.Symbol.Kind);
        }
    }

    [Fact]
    public void Le_surlignage_ne_lit_que_le_document()
    {
        var (document, directory, offset) = Open("import Shapes only {norme};\ngear f(): double => no|rme({x: 3.0, y: 4.0});\n!train |> done;", ("Shapes.forge", Shapes));
        using (directory)
        {
            var set = ReferenceFinder.Find(document, offset, wholeProject: false)!;

            Assert.All(set.Occurrences, o => Assert.EndsWith("main.rgl", o.Path));
        }
    }

    [Theory]
    [InlineData("ok", null)]
    [InlineData("_a1", null)]
    [InlineData("1a", "pas un nom valide")]
    [InlineData("a-b", "pas un nom valide")]
    [InlineData("gear", "mot du langage")]
    public void Un_nom_invalide_dit_pourquoi(string name, string? expected)
    {
        var why = ReferenceFinder.WhyInvalidName(name);
        if (expected is null) Assert.Null(why); else Assert.Contains(expected, why);
    }

    [Fact]
    public void Ce_qui_vient_de_la_std_ne_se_renomme_pas()
    {
        var directory = new TempDirectory();
        var std = directory.File("Std.forge", "forge: gears\ndescription: \"x\"\n\ngear pure doubler(int n): int => n * 2;\n");
        using (directory)
        {
            Assert.Null(ReferenceFinder.WhyNotRenamable(new RglSymbol("a", RglSymbolKind.Gear, default, null, null)));
            Assert.False(string.IsNullOrEmpty(std));
        }
    }

    [Fact]
    public void Le_pliage_couvre_les_corps_les_commentaires_et_les_imports_sans_la_ligne_fermante()
    {
        var text = "// un\n// deux\n// trois\nimport A;\nimport B;\n\ngear f(): int {\n    int a: 1;\n    return a;\n}\n!train |> done;";
        var document = SemanticDocument.Create("document.rgl", text, new ProjectContext("", "", []));

        var folds = FoldingProvider.Of(document);

        Assert.Contains(new FoldingEntry(0, 2, "comment"), folds);
        Assert.Contains(new FoldingEntry(3, 4, "imports"), folds);
        Assert.Contains(new FoldingEntry(6, 8, null), folds);
    }

    [Fact]
    public void L_extension_de_selection_grandit_du_mot_au_fichier()
    {
        var text = "gear f(): int {\n    return 1 + 2;\n}\n!train |> done;";
        var document = SemanticDocument.Create("document.rgl", text, new ProjectContext("", "", []));

        var spans = SelectionProvider.At(document, text.IndexOf("2;", StringComparison.Ordinal));

        Assert.Equal("2", spans[0].Text);
        Assert.Contains(spans, s => s.Text == "1 + 2");
        Assert.Contains(spans, s => s.Text.StartsWith("return", StringComparison.Ordinal));
        Assert.True(spans.Zip(spans.Skip(1)).All(p => p.Second.Length.Value > p.First.Length.Value));
    }

    [Fact]
    public void La_recherche_de_symboles_classe_le_debut_du_nom_puis_le_contenu_puis_les_lettres()
    {
        var text = "gear norme(): int => 1;\ngear enorme(): int => 1;\ngear nomRegle(): int => 1;\ngear zzz(): int => 1;\n!train |> done;";
        var document = SemanticDocument.Create("document.rgl", text, new ProjectContext("", "", []));

        var found = WorkspaceSymbolProvider.Search([document.Self], "nor").Select(e => e.Symbol.Name).ToArray();

        Assert.Equal(["norme", "enorme", "nomRegle"], found);
        Assert.Equal(["zzz"], WorkspaceSymbolProvider.Search([document.Self], "zz").Select(e => e.Symbol.Name).ToArray());
    }
}
