using System.Text;
using System.Text.Json.Nodes;
using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Layouts et composants d'une page <c>.paper</c> dans l'éditeur : noms du layout, diagnostics du markup, complétion, survol et définition.</summary>
public class PaperMarkupEditorTests
{
    private const string Main = "@rgl { reactive int visits: 10; gear load(): void { page.title <- 'm'; } }<header>@@ visits @@</header><slot />";
    private const string Card = "@rgl {\n    param string titre;\n    param int niveau: 1;\n}\n<section>@@ titre @@<slot /></section>";

    private static readonly string Home = """
        @layout Main;
        @rgl {
            reactive int count: 0;
            gear load(): void { visits <- visits + 1; page.title <- 'x'; }
        }
        <Carte titre="a" niveau=@@ count @@>@@ count @@ @@ visits @@</Carte>
        """.ReplaceLineEndings("\n");

    private static (TempDirectory Directory, string Uri) Project(string home)
    {
        var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        directory.File("Main.paper", Main);
        directory.File("Carte.paper", Card);
        var path = directory.File("Home.paper", home);
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

    private static JsonObject At(string uri, string text, string needle, int shift = 0)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal) + shift;
        var line = text[..index].Count(c => c == '\n');
        var character = index - (text[..index].LastIndexOf('\n') + 1);
        return new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } };
    }

    private static JsonNode? Result(List<JsonObject> messages, int id) => messages.Single(m => (int?)m["id"] == id)["result"];

    private static JsonArray Diagnostics(List<JsonObject> messages) =>
        messages.Last(m => (string?)m["method"] == "textDocument/publishDiagnostics" && ((string?)m["params"]!["uri"])!.EndsWith("Home.paper"))["params"]!["diagnostics"]!.AsArray();

    private static string[] Labels(JsonNode? completion) => [.. completion!.AsArray().Select(i => (string)i!["label"]!)];

    [Fact]
    public void Les_noms_du_layout_se_lisent_dans_la_page_et_un_composant_valide_n_a_pas_d_erreur()
    {
        var (directory, uri) = Project(Home);
        using (directory)
        {
            var messages = Serve(Open(uri, Home));

            var errors = Diagnostics(messages).Where(d => (int?)d!["severity"] == 1).Select(d => (string)d!["message"]!).ToArray();
            Assert.True(errors.Length == 0, string.Join(" | ", errors));
        }
    }

    [Fact]
    public void Un_composant_inconnu_un_attribut_inconnu_un_param_manquant_et_un_layout_inconnu_sont_signales_sur_leur_nom()
    {
        var home = "@layout Absent;\n<Carte inconnu=\"x\" />\n<Fantome />\n<Carte titre=\"a\">du contenu</Carte>\n<Main />";
        var (directory, uri) = Project(home);
        using (directory)
        {
            var messages = Serve(Open(uri, home));

            var errors = Diagnostics(messages).Where(d => (int?)d!["severity"] == 1).Select(d => (string)d!["message"]!).ToArray();
            Assert.Contains(errors, m => m.Contains("layout « Absent » introuvable"));
            Assert.Contains(errors, m => m.Contains("n'a pas de paramètre « inconnu »"));
            Assert.Contains(errors, m => m.Contains("demande le paramètre « titre »"));
            Assert.Contains(errors, m => m.Contains("composant « Fantome » introuvable"));
            Assert.DoesNotContain(errors, m => m.Contains("pas de <slot />"));
        }
    }

    [Fact]
    public void La_completion_propose_les_composants_leurs_param_et_les_layouts()
    {
        var home = "@layout \n<Ca\n<Carte titre=\"a\" \n";
        var (directory, uri) = Project(home);
        using (directory)
        {
            var messages = Serve(
                Open(uri, home),
                Request(1, "textDocument/completion", At(uri, home, "@layout ", "@layout ".Length)),
                Request(2, "textDocument/completion", At(uri, home, "<Ca", 3)),
                Request(3, "textDocument/completion", At(uri, home, "titre=\"a\" ", "titre=\"a\" ".Length)));

            Assert.Contains("Main", Labels(Result(messages, 1)));
            var components = Result(messages, 2)!.AsArray();
            var card = Assert.Single(components, i => (string)i!["label"]! == "Carte")!;
            Assert.Equal("Carte titre=\"$1\">$0</Carte>", (string)card["insertText"]!);
            Assert.Contains("niveau: int = 1", (string)card["detail"]!);
            Assert.Equal(["niveau"], Labels(Result(messages, 3)));
        }
    }

    [Fact]
    public void Page_propose_title_script_et_env_dans_un_bloc()
    {
        var home = "@rgl { gear load(): void { page. } }";
        var (directory, uri) = Project(home);
        using (directory)
        {
            var messages = Serve(Open(uri, home), Request(1, "textDocument/completion", At(uri, home, "page.", 5)));

            var labels = Labels(Result(messages, 1));
            Assert.Contains("title", labels);
            Assert.Contains("script", labels);
            Assert.Contains("env", labels);
        }
    }

    [Fact]
    public void Le_survol_decrit_le_composant_son_param_et_le_layout()
    {
        var (directory, uri) = Project(Home);
        using (directory)
        {
            var messages = Serve(
                Open(uri, Home),
                Request(1, "textDocument/hover", At(uri, Home, "<Carte", 2)),
                Request(2, "textDocument/hover", At(uri, Home, "niveau=", 2)),
                Request(3, "textDocument/hover", At(uri, Home, "@layout Main", 10)));

            var component = (string)Result(messages, 1)!["contents"]!["value"]!;
            Assert.Contains("<Carte(titre: string, niveau: int = 1)>", component);
            Assert.Contains("<slot />", component);
            Assert.Contains("param int niveau: 1", (string)Result(messages, 2)!["contents"]!["value"]!);
            Assert.Contains("layout", (string)Result(messages, 3)!["contents"]!["value"]!);
        }
    }

    [Fact]
    public void La_definition_mene_au_fichier_du_composant_a_son_param_et_au_layout()
    {
        var (directory, uri) = Project(Home);
        using (directory)
        {
            var messages = Serve(
                Open(uri, Home),
                Request(1, "textDocument/definition", At(uri, Home, "<Carte", 2)),
                Request(2, "textDocument/definition", At(uri, Home, "niveau=", 2)),
                Request(3, "textDocument/definition", At(uri, Home, "@layout Main", 10)));

            Assert.EndsWith("Carte.paper", (string)Result(messages, 1)!["uri"]!);
            Assert.EndsWith("Carte.paper", (string)Result(messages, 2)!["uri"]!);
            var range = Result(messages, 2)!["range"]!;
            Assert.Equal(2, (int)range["start"]!["line"]!);
            Assert.Equal("param int ".Length + 4, (int)range["start"]!["character"]!);
            Assert.EndsWith("Main.paper", (string)Result(messages, 3)!["uri"]!);
        }
    }
}
