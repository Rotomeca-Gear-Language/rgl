using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>L'éditeur et les datasets : complétion de <c>Moves.</c> et de <c>id.</c>, en-tête <c>dataset Nom …</c>, coloration, définition.</summary>
public class DatasetEditorTests
{
    private const string Bdd = """
        forge: gears
        description: "Données"

        mold Move { string name; int power; }

        dataset Moves of Move {
            attaquer: {name: 'Attaquer', power: 5},
            soin: {name: 'Soin', power: -20},
        }
        """;

    private static (SemanticDocument Document, TempDirectory Directory, string Text, int Offset) Open(string main)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("bdd.forge", Bdd);
        var cursor = System.Text.RegularExpressions.Regex.Match(main, @"\|(?!>)");
        var text = cursor.Success ? main.Remove(cursor.Index, 1) : main;
        var path = directory.File("main.rgl", text);
        var project = new ProjectIndex().Open(path, _ => null);
        return (SemanticDocument.Create(path, text, project), directory, text, cursor.Success ? cursor.Index : 0);
    }

    private static IReadOnlyList<CompletionItem> Complete(string main)
    {
        var (document, directory, text, offset) = Open(main);
        using (directory)
        {
            var before = text[..offset];
            var line = (uint)before.Count(c => c == '\n');
            var character = (uint)(offset - (before.LastIndexOf('\n') + 1));
            return CompletionProvider.Complete(document.Self.Path, text, line, character, document.Project);
        }
    }

    private static List<(string Text, string Type)> Tokens(string main)
    {
        var (document, directory, text, _) = Open(main);
        using (directory)
        {
            var data = SemanticTokensProvider.Encode(document);
            var tokens = new List<(string, string)>();
            var (line, column) = (0, 0);
            var lines = text.Split('\n');
            for (var i = 0; i < data.Length; i += 5)
            {
                line += data[i];
                column = data[i] == 0 ? column + data[i + 1] : data[i + 1];
                tokens.Add((lines[line].Substring(column, data[i + 2]), SemanticTokensProvider.Types[data[i + 3]]));
            }
            return tokens;
        }
    }

    private const string Local = "mold Move { string name; int power; }\ndataset Moves of Move {\n    attaquer: {name: 'A', power: 5},\n    soin: {name: 'S', power: 1},\n}\n";

    [Fact]
    public void Apres_le_nom_d_un_dataset_on_propose_ses_entrees_son_id_sa_longueur_et_find()
    {
        var items = Complete(Local + "gear f(): void { var x: Moves.|\n}\n!train |> done;");
        var labels = items.Select(i => i.Label).ToArray();

        Assert.Equal(["attaquer", "soin", "id", "length", "find"], labels.Take(5));
        Assert.Equal(CompletionKind.EnumMember, items.First(i => i.Label == "attaquer").Kind);
        Assert.Equal(CompletionKind.Method, items.First(i => i.Label == "find").Kind);
    }

    [Fact]
    public void Un_dataset_d_un_autre_fichier_se_complete_de_la_meme_facon()
    {
        var labels = Complete("import bdd only {Moves};\ngear f(): void { var x: Moves.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("attaquer", labels);
        Assert.Contains("soin", labels);
    }

    [Fact]
    public void Un_dataset_d_un_fichier_importe_en_entier_se_complete_apres_le_nom_qualifie()
    {
        var labels = Complete("import bdd;\ngear f(): void { var x: bdd.Moves.|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("attaquer", labels);
    }

    [Fact]
    public void Un_identifiant_propose_get_et_name()
    {
        var items = Complete(Local + "gear f(Moves.id m): void { m.|\n}\n!train |> done;");
        var labels = items.Select(i => i.Label).ToArray();

        Assert.Equal(["get", "name"], labels.Take(2));
    }

    [Fact]
    public void Une_entree_a_le_type_des_identifiants_et_get_donne_les_champs_du_mold()
    {
        var labels = Complete(Local + "gear f(): void { var m: Moves.soin;\n    m.get().|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("name", labels);
        Assert.Contains("power", labels);
    }

    [Fact]
    public void Get_directement_sur_une_entree_donne_les_champs_du_mold()
    {
        var labels = Complete(Local + "gear f(): void { Moves.attaquer.get().|\n}\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("power", labels);
    }

    [Fact]
    public void Dans_un_type_le_dataset_se_propose_puis_son_id()
    {
        var first = Complete(Local + "mold P { Mo| }\n!train |> done;").Select(i => i.Label).ToArray();
        var members = Complete(Local + "mold P { Moves.| }\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Contains("Moves", first);
        Assert.Contains("id", members);
    }

    [Fact]
    public void L_en_tete_d_un_dataset_propose_of_as_from_puis_les_molds()
    {
        var header = Complete(Local + "dataset Autres |\n!train |> done;").Select(i => i.Label).ToArray();
        var afterFrom = Complete(Local + "dataset Autres from \"x.json\" |\n!train |> done;").Select(i => i.Label).ToArray();
        var molds = Complete(Local + "dataset Autres of |\n!train |> done;").Select(i => i.Label).ToArray();

        Assert.Equal(["of", "as", "from"], header);
        Assert.Equal(["of", "as"], afterFrom);
        Assert.Contains("Move", molds);
    }

    [Fact]
    public void Un_dataset_d_un_autre_fichier_s_importe_en_le_choisissant()
    {
        var item = Complete("gear f(): void { Mov| }\n!train |> done;").FirstOrDefault(i => i.Label == "Moves");

        Assert.NotNull(item);
        Assert.NotNull(item!.Edits);
    }

    [Fact]
    public void Un_dataset_et_ses_entrees_ont_leur_couleur()
    {
        var tokens = Tokens(Local + "gear f(): void { var m: Moves.soin;\n    var t: Moves.id: m; }\n!train |> done;");

        Assert.Equal("enum", tokens.First(t => t.Text == "Moves").Type);
        Assert.Equal("enumMember", tokens.First(t => t.Text == "soin" && t.Type != "property").Type);
        Assert.Contains(tokens, t => t.Text == "id" && t.Type == "ingot");
    }

    [Fact]
    public void La_definition_d_une_entree_mene_a_sa_ligne_dans_le_dataset()
    {
        var (document, directory, text, offset) = Open(Local + "gear f(): void { var m: Moves.so|in;\n}\n!train |> done;");
        using (directory)
        {
            var target = document.TargetAt(offset);

            Assert.NotNull(target?.Symbol);
            Assert.Equal("soin", target!.Symbol!.Name);
            Assert.Equal(text.IndexOf("soin:", StringComparison.Ordinal), (int)target.Symbol.NameSpan.Start.Value);
        }
    }
}
