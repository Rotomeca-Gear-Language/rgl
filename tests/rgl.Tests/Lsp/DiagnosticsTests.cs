using System.Text;
using System.Text.Json.Nodes;
using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>
/// Les erreurs et les avertissements dans l'éditeur : ceux d'un fichier <c>.forge</c> (lié dans son projet), et ceux d'un fichier importé,
/// publiés sur ce fichier avec ses propres positions.
/// </summary>
public class DiagnosticsTests
{
    private const string Manifest = "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }";

    [Fact]
    public void Un_fichier_forge_a_ses_erreurs_de_sens()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", Manifest);
        var path = directory.File("Outils.forge", "forge: gears\ndescription: \"t\"\n\ngear f(): int => \"texte\";\ngear g(): void { inconnu(); }\n");

        var report = DocumentAnalysis.Analyze(path, File.ReadAllText(path));

        Assert.Equal(["E003", "E035"], report.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void Un_fichier_forge_sans_erreur_n_a_rien_a_signaler_et_une_description_manquante_est_signalee_une_fois()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", Manifest);
        var clean = directory.File("Propre.forge", "forge: gears\ndescription: \"t\"\n\ngear f(): int => 1;\n");
        var bare = directory.File("Nue.forge", "forge: gears\n\ngear f(): int => 1;\n");

        Assert.Empty(DocumentAnalysis.Analyze(clean, File.ReadAllText(clean)).Diagnostics);
        Assert.Equal(["W005"], DocumentAnalysis.Analyze(bare, File.ReadAllText(bare)).Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void Un_fichier_forge_est_lu_comme_l_editeur_le_montre()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", Manifest);
        var path = directory.File("Outils.forge", "forge: gears\ndescription: \"t\"\n\ngear f(): int => 1;\n");

        // Le texte de l'éditeur (une erreur) l'emporte sur celui du disque.
        var open = new Dictionary<string, string> { [Path.GetFullPath(path)] = "forge: gears\ndescription: \"t\"\n\ngear f(): int => \"x\";\n" };
        var report = DocumentAnalysis.Analyze(path, open[Path.GetFullPath(path)], open);

        Assert.Equal(["E003"], report.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void L_erreur_d_un_fichier_importe_est_publiee_sur_ce_fichier_et_pas_sur_le_document()
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", Manifest);
        var imported = directory.File("Outils.forge", "forge: gears\ndescription: \"t\"\n\ngear f(): int => \"texte\";\n");
        var main = directory.File("main.rgl", "import Outils;\n!train |> done;\n");

        var messages = Serve(new Uri(main).AbsoluteUri, File.ReadAllText(main));

        string[] Codes(string suffix) => messages.Where(m => ((string?)m["params"]!["uri"])!.EndsWith(suffix, StringComparison.Ordinal))
            .Last()["params"]!["diagnostics"]!.AsArray().Select(d => (string?)d!["code"] ?? "").ToArray();
        Assert.Empty(Codes("main.rgl"));
        Assert.Equal(["E003"], Codes("Outils.forge"));
        var diagnostic = messages.Last(m => ((string?)m["params"]!["uri"])!.EndsWith("Outils.forge", StringComparison.Ordinal))["params"]!["diagnostics"]![0]!;
        Assert.Equal(3, (int)diagnostic["range"]!["start"]!["line"]!);
    }

    [Theory]
    [InlineData("vscode-remote://ssh-remote+machine/home/u/main.rgl")]
    [InlineData("untitled:Untitled-1.rgl")]
    [InlineData("vscode-vfs://github/Rotomeca/rgl/main.rgl")]
    public void Un_document_dont_l_adresse_n_est_pas_un_fichier_local_est_quand_meme_analyse(string path)
    {
        var report = DocumentAnalysis.Analyze(path, "gear g(): int => \"texte\";\n!train |> done;\n");

        Assert.Equal(["E003"], report.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void Un_point_manquant_en_fin_de_ligne_est_souligne_sur_le_dernier_caractere_ecrit()
    {
        var messages = Serve("file:///C:/projet/main.rgl", "gear g(): int => 1,   \n!train |> done;\n");

        var range = messages.Last()["params"]!["diagnostics"]![0]!["range"]!;
        Assert.Equal(0, (int)range["start"]!["line"]!);
        Assert.Equal(17, (int)range["start"]!["character"]!);   // le dernier mot écrit avant le « ; » manquant
        Assert.Equal(18, (int)range["end"]!["character"]!);
    }

    /// <summary>Ouvre un document et renvoie les diagnostics publiés.</summary>
    private static List<JsonObject> Serve(string uri, string text)
    {
        var input = new MemoryStream();
        foreach (var message in new[]
        {
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() },
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/didOpen",
                ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "rgl", ["version"] = 1, ["text"] = text } },
            },
        })
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
        while (channel.Read() is { } message)
        {
            if ((string?)message["method"] == "textDocument/publishDiagnostics") written.Add(message);
        }
        return written;
    }
}
