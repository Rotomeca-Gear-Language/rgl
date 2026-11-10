using System.Text;
using System.Text.Json.Nodes;
using Rgl.Lsp;
using Rgl.Paper;

namespace Rgl.Tests.Lsp;

/// <summary>Le RGL d'une page <c>.paper</c> dans l'éditeur : diagnostics, complétion et survol, aux positions de la page.</summary>
public class PaperEditorTests
{
    private static readonly string Page = """
        <h1>@@ titre @@</h1>
        @rgl {
            reactive int count: 0;
            const string titre: 'Bonjour';
            gear inc(): void { count <- count + 1; }
        }
        <p>@@ inconnu @@</p>
        <button @click="inc">+</button>
        """.ReplaceLineEndings("\n");

    #region Projection

    [Fact]
    public void Les_blocs_et_les_interpolations_sont_les_morceaux_de_rgl_de_la_page()
    {
        var regions = PaperRegions.Find(Page);

        Assert.Equal([" titre ", "\n    reactive int count: 0;\n    const string titre: 'Bonjour';\n    gear inc(): void { count <- count + 1; }\n", " inconnu "],
            regions.Select(r => Page[r.Start..r.End]));
        Assert.Equal([false, true, false], regions.Select(r => r.IsBlock));
    }

    [Fact]
    public void Une_accolade_dans_un_texte_ne_ferme_pas_le_bloc_et_un_nom_collant_n_ouvre_rien()
    {
        var text = "@rgl { const string a: '}'; } <p>x@rgl { 1 }</p>";

        var block = Assert.Single(PaperRegions.Find(text));

        Assert.Equal(" const string a: '}'; ", text[block.Start..block.End]);
    }

    [Fact]
    public void Les_positions_vont_et_viennent_entre_la_page_et_le_texte_virtuel()
    {
        var projection = PaperProjection.Build(Page);
        var word = Page.IndexOf("titre @@", StringComparison.Ordinal);

        var inVirtual = projection.ToVirtual(word)!.Value;

        Assert.Equal("titre", projection.VirtualText.Substring(inVirtual, 5));
        Assert.Equal(word, projection.ToPaper(inVirtual));
        Assert.Null(projection.ToVirtual(Page.IndexOf("<h1>", StringComparison.Ordinal)));
        Assert.Equal(["count"], projection.Reactives);
        Assert.Contains("const int count: 0;", projection.VirtualText);
        Assert.True(PaperProjection.IsVirtual(projection.VirtualText));
    }

    #endregion

    #region Serveur

    private static (TempDirectory Directory, string Uri) Project()
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        var path = directory.File("Home.paper", Page);
        return (directory, new Uri(path).AbsoluteUri);
    }

    private static List<JsonObject> Serve(params JsonObject[] messages)
    {
        var input = new MemoryStream();
        foreach (var message in messages)
        {
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            input.Write(body);
        }
        input.Position = 0;
        var output = new MemoryStream();
        LanguageServer.Run(input, output);

        output.Position = 0;
        var channel = new JsonRpcChannel(output, Stream.Null);
        var written = new List<JsonObject>();
        while (channel.Read() is { } message) written.Add(message);
        return written;
    }

    private static JsonObject Request(int id, string method, JsonObject parameters) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    private static JsonObject Open(string uri, string text) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "textDocument/didOpen",
        ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "html", ["version"] = 1, ["text"] = text } },
    };

    private static JsonObject At(string uri, int line, int character) =>
        new() { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } };

    private static (int Line, int Character) PlaceOf(string needle, int shift = 0)
    {
        var index = Page.IndexOf(needle, StringComparison.Ordinal) + shift;
        var line = Page[..index].Count(c => c == '\n');
        return (line, index - (Page[..index].LastIndexOf('\n') + 1));
    }

    [Fact]
    public void Une_erreur_du_rgl_d_une_page_se_voit_sur_la_page_et_ecrire_dans_un_etat_n_en_est_pas_une()
    {
        var (directory, uri) = Project();
        using (directory)
        {
            var messages = Serve(Open(uri, Page));

            var diagnostics = messages.Last(m => (string?)m["method"] == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray();
            var error = Assert.Single(diagnostics, d => (int?)d!["severity"] == 1)!;
            var (line, character) = PlaceOf("inconnu");
            Assert.Equal(line, (int)error["range"]!["start"]!["line"]!);
            Assert.Equal(character, (int)error["range"]!["start"]!["character"]!);
        }
    }

    [Fact]
    public void La_tete_de_page_et_les_param_ne_sont_pas_des_erreurs_dans_l_editeur()
    {
        var text = """
            @rgl {
                param string who;
                param int times: 2;
                reactive int count: 0;
                gear load(): void {
                    page.title <- 'Salut ' + who;
                    page.script('/app.js');
                    page.env('n', times);
                }
            }
            <p>@@ who @@ @@ count @@ @@ inconnu @@</p>
            """.ReplaceLineEndings("\n");
        var (directory, uri) = Project();
        using (directory)
        {
            var messages = Serve(Open(uri, text));

            var diagnostics = messages.Last(m => (string?)m["method"] == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray();
            var error = Assert.Single(diagnostics, d => (int?)d!["severity"] == 1)!;
            Assert.Contains("inconnu", (string?)error["message"] ?? "");
        }
    }

    [Fact]
    public void Le_survol_et_la_completion_marchent_dans_une_interpolation_et_se_taisent_dans_le_html()
    {
        var (directory, uri) = Project();
        using (directory)
        {
            var (line, character) = PlaceOf("titre @@", 2);
            var (htmlLine, htmlCharacter) = PlaceOf("<h1>", 2);
            var messages = Serve(
                Open(uri, Page),
                Request(1, "textDocument/hover", At(uri, line, character)),
                Request(2, "textDocument/hover", At(uri, htmlLine, htmlCharacter)),
                Request(3, "textDocument/completion", At(uri, line, character)),
                Request(4, "textDocument/completion", At(uri, htmlLine, htmlCharacter)),
                Request(5, "textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }));

            JsonNode? Result(int id) => messages.Single(m => (int?)m["id"] == id)["result"];
            Assert.Contains("titre", (string?)Result(1)!["contents"]!["value"]);
            Assert.Equal(line, (int)Result(1)!["range"]!["start"]!["line"]!);
            Assert.Null(Result(2));
            Assert.Contains(Result(3)!.AsArray(), item => (string?)item!["label"] == "titre");
            Assert.Empty(Result(4)!.AsArray());
            Assert.Null(Result(5));
        }
    }

    #endregion

    #region Page importée

    private static IReadOnlyList<CompletionItem> CompleteIn(string main)
    {
        var (directory, _) = Project();
        using (directory)
        {
            var path = directory.File("main.rgl", main.Replace("|", ""));
            var offset = main.IndexOf('|');
            var before = main[..offset];
            var project = new ProjectIndex().Open(path, _ => null);
            return CompletionProvider.Complete(path, main.Replace("|", ""), (uint)before.Count(c => c == '\n'), (uint)(offset - (before.LastIndexOf('\n') + 1)), project);
        }
    }

    [Fact]
    public void Une_page_importee_donne_ses_noms_a_l_editeur_sans_ses_fragments_internes()
    {
        var items = CompleteIn("import Home;\ngear f(): int {\n    Home.|\n}\n!train |> done;\n");

        var labels = items.Select(i => i.Label).ToArray();
        Assert.Contains("mount", labels);
        Assert.Contains("render", labels);
        Assert.Contains("State", labels);
        Assert.DoesNotContain(labels, l => l.StartsWith("__paper_", StringComparison.Ordinal));
    }

    [Fact]
    public void Le_nom_d_une_page_se_propose_apres_import()
    {
        var items = CompleteIn("import Ho|\n!train |> done;\n");

        Assert.Contains(items, i => i.Label == "Home");
    }

    #endregion
}
