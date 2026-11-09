using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Actions rapides (importer un nom, en-tête, imports inutilisés) et mots d'en-tête d'un fichier forge.</summary>
public class CodeActionTests
{
    private const string Shapes = "forge: gears\ndescription: \"Formes\"\n\nmold Vec { double x; double y; }\n\ngear norme(Vec v): double => v.x + v.y;\n";
    private const string Ext = "forge: extend\ndescription: \"Ext\"\nnamespace Mon/Ext;\n\nextend int { gear pure double2(this int v): int => v * 2; }\n";

    private static (SemanticDocument Document, TempDirectory Directory, string Text) Open(string main, params (string Name, string Content)[] files)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        foreach (var (name, content) in files) directory.File(name, content);
        var path = directory.File("main.rgl", main);
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, main, project), directory, main);
    }

    /// <summary>Applique les changements d'une action au texte.</summary>
    private static string Apply(string text, CodeAction action)
    {
        var lines = text.Split('\n').ToList();
        foreach (var edit in action.Edits.OrderByDescending(e => e.StartLine).ThenByDescending(e => e.StartCharacter))
        {
            var offset = lines.Take((int)edit.StartLine).Sum(l => l.Length + 1) + (int)edit.StartCharacter;
            var endOffset = lines.Take((int)edit.EndLine).Sum(l => l.Length + 1) + (int)edit.EndCharacter;
            text = text[..offset] + edit.Text + text[Math.Min(endOffset, text.Length)..];
            lines = [.. text.Split('\n')];
        }
        return text;
    }

    [Fact]
    public void Un_nom_inconnu_que_le_projet_declare_propose_son_import()
    {
        var (document, directory, text) = Open("gear f(): double => norme({x: 1.0, y: 2.0});\n!train |> done;", ("Shapes.forge", Shapes));
        using (directory)
        {
            var offset = text.IndexOf("norme", StringComparison.Ordinal);
            var actions = CodeActionProvider.At(document, offset, offset + 5, [], [CodeActionProvider.QuickFix]);

            var action = Assert.Single(actions);
            Assert.Equal("Importer « norme » depuis Shapes", action.Title);
            Assert.StartsWith("import Shapes only {norme};", Apply(text, action));
        }
    }

    [Fact]
    public void Un_nom_deja_visible_ou_un_membre_ne_propose_rien()
    {
        var (document, directory, text) = Open("import Shapes only {norme, Vec};\ngear f(Vec v): double => norme(v) + v.x;\n!train |> done;", ("Shapes.forge", Shapes));
        using (directory)
        {
            Assert.DoesNotContain(CodeActionProvider.At(document, 0, text.Length, [], [CodeActionProvider.QuickFix]), a => a.Title.StartsWith("Importer", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Les_imports_inutilises_se_retirent_en_entier_ou_nom_par_nom()
    {
        var other = ("Other.forge", "forge: gears\ndescription: \"Autre\"\n\ngear plus(int n): int => n + 1;\ngear moins(int n): int => n - 1;\n");
        var (document, directory, text) = Open("import Shapes only {norme};\nimport Other only {plus, moins};\ngear f(int n): int => plus(n);\n!train |> done;", ("Shapes.forge", Shapes), other);
        using (directory)
        {
            var unused = CodeActionProvider.UnusedImports(document);
            Assert.Equal(["Shapes", "Other"], unused.Select(u => u.Declaration.Module.Name));
            Assert.True(unused[0].WholeImport);
            Assert.False(unused[1].WholeImport);

            var organize = Assert.Single(CodeActionProvider.At(document, 0, 0, [], [CodeActionProvider.OrganizeImports]));
            Assert.Equal("import Other only {plus};\ngear f(int n): int => plus(n);\n!train |> done;", Apply(text, organize));
        }
    }

    [Fact]
    public void Un_fichier_d_extensions_n_est_jamais_juge_inutilise()
    {
        var (document, directory, _) = Open("import Ext;\ngear f(): int => 2;\n!train |> done;", ("Ext.forge", Ext));
        using (directory)
        {
            Assert.Empty(CodeActionProvider.UnusedImports(document));
        }
    }

    [Fact]
    public void Une_description_manquante_s_ajoute_apres_le_genre()
    {
        var text = "forge: gears\n\ngear f(): int => 1;\n";
        var document = SemanticDocument.Create("x.forge", text, new ProjectContext("", "", []));

        var action = Assert.Single(CodeActionProvider.At(document, 0, 0, ["W005"], [CodeActionProvider.QuickFix]));

        Assert.Equal("forge: gears\ndescription: ''\n\ngear f(): int => 1;\n", Apply(text, action));
    }

    private static IReadOnlyList<CompletionItem> Complete(string path, string source)
    {
        var offset = source.IndexOf('|');
        var text = source.Remove(offset, 1);
        var before = text[..offset];
        return CompletionProvider.Complete(path, text, (uint)before.Count(c => c == '\n'), (uint)(offset - (before.LastIndexOf('\n') + 1)), new ProjectContext("", "", []));
    }

    [Fact]
    public void Au_debut_d_un_forge_forge_s_ecrit_avec_deux_points_et_rouvre_la_liste()
    {
        var item = Assert.Single(Complete("x.forge", "fo|"), i => i.Label == "forge");

        Assert.Equal("forge: ", item.InsertText);
        Assert.Equal("editor.action.triggerSuggest", item.Command);
        Assert.Contains(Complete("x.forge", "forge: |"), i => i.Label == "gears");
    }

    [Fact]
    public void Apres_le_genre_description_s_ecrit_avec_ses_guillemets()
    {
        var items = Complete("x.forge", "forge: gears\ndesc|");

        var item = Assert.Single(items, i => i.Label == "description");
        Assert.Equal("description: '$0'", item.InsertText);
        Assert.DoesNotContain(items, i => i.Label == "forge");
    }

    [Fact]
    public void Les_mots_d_en_tete_ne_sont_plus_proposes_une_fois_ecrits_ni_ailleurs_ni_dans_un_rgl()
    {
        Assert.DoesNotContain(Complete("x.forge", "forge: gears\ndescription: ''\n|"), i => i.Label is "forge" or "description");
        Assert.DoesNotContain(Complete("x.forge", "forge: gears\ndescription: ''\n\ngear f(): int {\n    de|\n}\n"), i => i.Command is not null);
        Assert.DoesNotContain(Complete("main.rgl", "fo|"), i => i.Command is not null);
    }
}
