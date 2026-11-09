using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Le modèle que l'éditeur de dataset de l'extension affiche : colonnes, valeurs possibles, entrées et endroit à remplacer.</summary>
public class DatasetEditorModelTests
{
    private const string Source = """
        enum Element { Neutre, Feu }
        mold Class { int<1..100> atk; int<1..100> def: 10; }
        dataset Classes of Class {
            guerrier: {atk: 50, def: 40},
            mage: {atk: 20},
        }
        mold Move { string name; Element element; Classes.id classe; bool actif: true; int? bonus; }
        dataset Moves of Move {
            braise: {name: 'Braise', element: Element.Feu, classe: Classes.mage},
        }
        dataset Items as Item {
            epee: {nom: 'Épée', prix: 10},
            arc: {nom: 'Arc', prix: 25, rare: true},
        }
        dataset Heros from "heros.json" of Class;
        """;

    private static DatasetEditorModel Model(uint line)
    {
        var directory = new TempDirectory();
        using (directory)
        {
            var path = directory.File("main.rgl", Source);
            var project = new ProjectIndex().Open(path, _ => null);
            var document = SemanticDocument.Create(path, Source, project);
            return DatasetEditorModel.At(document, line)!;
        }
    }

    [Fact]
    public void Un_dataset_of_a_pour_colonnes_les_champs_de_son_mold_avec_leur_obligation()
    {
        var model = Model(2);

        Assert.Equal("Classes", model.Name);
        Assert.Equal(["atk", "def"], model.Fields.Select(f => f.Name));
        Assert.True(model.Fields[0].Required);
        Assert.False(model.Fields[1].Required);
        Assert.Equal("int<1..100>", model.Fields[0].Type);
    }

    [Fact]
    public void Les_entrees_donnent_leurs_valeurs_par_champ()
    {
        var model = Model(2);

        Assert.Equal(["guerrier", "mage"], model.Rows.Select(r => r.Name));
        Assert.Equal("50", model.Rows[0].Values["atk"]);
        Assert.Equal("40", model.Rows[0].Values["def"]);
        Assert.False(model.Rows[1].Values.ContainsKey("def"));
    }

    [Fact]
    public void Le_corps_a_remplacer_va_de_l_accolade_ouvrante_a_la_fermante()
    {
        var model = Model(2);

        Assert.StartsWith("{", model.Body!.Value.Text);
        Assert.EndsWith("}", model.Body!.Value.Text);
        Assert.Contains("guerrier", model.Body!.Value.Text);
    }

    [Fact]
    public void Un_enum_un_identifiant_et_un_booleen_proposent_leurs_valeurs()
    {
        var fields = Model(7).Fields.ToDictionary(f => f.Name);

        Assert.Equal(["Neutre", "Feu"], fields["element"].Choices.Select(c => c.Label));
        Assert.Equal("Element.Feu", fields["element"].Choices[1].Inline);
        Assert.Equal("\"Feu\"", fields["element"].Choices[1].Json);
        Assert.Equal(["guerrier", "mage"], fields["classe"].Choices.Select(c => c.Label));
        Assert.Equal("Classes.mage", fields["classe"].Choices[1].Inline);
        Assert.Equal(["true", "false"], fields["actif"].Choices.Select(c => c.Label));
        Assert.True(fields["name"].Required);
        Assert.False(fields["actif"].Required);
        Assert.False(fields["bonus"].Required);
    }

    [Fact]
    public void Un_dataset_as_a_pour_colonnes_les_champs_ecrits_par_ses_entrees()
    {
        var model = Model(10);

        Assert.True(model.CreatesMold);
        Assert.Equal(["nom", "prix", "rare"], model.Fields.Select(f => f.Name));
        Assert.Equal("true", model.Rows[1].Values["rare"]);
    }

    [Fact]
    public void Un_dataset_from_donne_son_fichier_et_ses_colonnes_sans_entrees()
    {
        var model = Model(14);

        Assert.Equal("heros.json", model.Source);
        Assert.Null(model.Body);
        Assert.Empty(model.Rows);
        Assert.Equal(["atk", "def"], model.Fields.Select(f => f.Name));
    }

    [Fact]
    public void Une_ligne_sans_dataset_ne_donne_rien()
    {
        var directory = new TempDirectory();
        using (directory)
        {
            var path = directory.File("main.rgl", "gear f(): int => 1;\n!train |> done;");
            var document = SemanticDocument.Create(path, "gear f(): int => 1;\n!train |> done;", new ProjectIndex().Open(path, _ => null));

            Assert.Null(DatasetEditorModel.At(document, 0));
        }
    }
}
