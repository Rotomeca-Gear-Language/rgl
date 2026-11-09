using System.Text;
using System.Text.Json.Nodes;
using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Tests du serveur de langage : diagnostics publiés et autocomplétion, par ses messages JSON-RPC.</summary>
public class LanguageServerTests
{
    private const string Uri = "file:///C:/projet/main.rgl";

    /// <summary>Envoie ces messages au serveur, puis renvoie le code de sortie et les messages qu'il a écrits.</summary>
    private static (int Code, List<JsonObject> Messages) Serve(params JsonObject[] messages)
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
        var code = LanguageServer.Run(input, output);

        output.Position = 0;
        var channel = new JsonRpcChannel(output, Stream.Null);
        var written = new List<JsonObject>();
        while (channel.Read() is { } message) written.Add(message);
        return (code, written);
    }

    private static JsonObject Request(int id, string method, JsonObject? parameters = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    private static JsonObject Notification(string method, JsonObject? parameters = null) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };

    private static JsonObject Open(string text) =>
        Notification("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri, ["languageId"] = "rgl", ["version"] = 1, ["text"] = text } });

    /// <summary>Les diagnostics publiés en dernier.</summary>
    private static JsonArray LastDiagnostics(List<JsonObject> messages) =>
        messages.Last(m => (string?)m["method"] == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray();

    [Fact]
    public async Task Une_std_absente_suspend_l_analyse_et_se_restaure_sans_crash()
    {
        using var directory = new TempDirectory();
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(LanguageServer).Assembly.Location);
        start.ArgumentList.Add("lsp");
        start.Environment["XDG_CACHE_HOME"] = directory.Path;
        start.Environment["LOCALAPPDATA"] = directory.Path;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var messages = new[]
        {
            Request(1, "initialize", new JsonObject { ["initializationOptions"] = new JsonObject { ["manageStandardLibrary"] = true } }),
            Request(2, "rgl/standardLibrary", new JsonObject()),
            Open("!train |> $ 0;"), Open("!train |> $ 0;"),
            Request(3, "textDocument/hover", new JsonObject()),
            Request(4, "rgl/standardLibrary", new JsonObject { ["restore"] = true }),
            Open("!train |> $ 0;"),
            Request(5, "shutdown"), Notification("exit"),
        };
        foreach (var message in messages)
        {
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            process.StandardInput.BaseStream.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            process.StandardInput.BaseStream.Write(body);
        }
        process.StandardInput.Close();
        if (!process.WaitForExit(30000)) { process.Kill(); Assert.Fail("Le serveur ne répond plus."); }
        Assert.True(process.ExitCode == 0, await errors);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(await output));
        var channel = new JsonRpcChannel(stream, Stream.Null);
        var replies = new List<JsonObject>();
        while (channel.Read() is { } reply) replies.Add(reply);
        Assert.False((bool)replies.Single(m => (int?)m["id"] == 2)["result"]!["available"]!);
        Assert.True((bool)replies.Single(m => (int?)m["id"] == 4)["result"]!["available"]!);
        Assert.Equal(2, replies.Count(m => m["params"]?["diagnostics"] is JsonArray diagnostics
            && diagnostics.Any(d => (string?)d?["code"] == "RGL001")));
        Assert.Empty(LastDiagnostics(replies));
        var std = Path.Combine(directory.Path, "rgl", "std");
        Assert.True(Directory.Exists(std));
        var extracted = Directory.GetDirectories(std).Single();
        File.Delete(Path.Combine(extracted, "Errors.forge"));
        Assert.False(Rgl.Project.StdLibrary.IsAvailable(extracted));
    }

    [Fact]
    public void Le_serveur_s_initialise_et_s_arrete_proprement()
    {
        var (code, messages) = Serve(Request(1, "initialize", new JsonObject()), Notification("initialized"), Request(2, "shutdown"), Notification("exit"));

        Assert.Equal(0, code);
        Assert.Equal(1, (int)messages[0]["result"]!["capabilities"]!["textDocumentSync"]!["change"]!);
        Assert.Equal(".", (string?)messages[0]["result"]!["capabilities"]!["completionProvider"]!["triggerCharacters"]![0]);
        Assert.True((bool)messages[0]["result"]!["capabilities"]!["hoverProvider"]!);
        Assert.True((bool)messages[0]["result"]!["capabilities"]!["definitionProvider"]!);
        Assert.Equal("(", (string?)messages[0]["result"]!["capabilities"]!["signatureHelpProvider"]!["triggerCharacters"]![0]);
        Assert.Equal(2, (int)messages[1]["id"]!);
    }

    [Fact]
    public void La_coloration_semantique_et_le_plan_du_document_sont_servis()
    {
        static JsonObject Document() => new() { ["textDocument"] = new JsonObject { ["uri"] = Uri } };
        var (_, messages) = Serve(Request(1, "initialize", new JsonObject()), Open("ingot score: int;\nmold P { score s; }\n!train |> done;"),
            Request(2, "textDocument/semanticTokens/full", Document()), Request(3, "textDocument/documentSymbol", Document()));

        var legend = messages.First(m => (int?)m["id"] == 1)["result"]!["capabilities"]!["semanticTokensProvider"]!["legend"]!["tokenTypes"]!.AsArray().Select(t => (string?)t).ToArray();
        Assert.Contains("ingot", legend);

        var data = messages.First(m => (int?)m["id"] == 2)["result"]!["data"]!.AsArray();
        Assert.NotEmpty(data);
        Assert.Equal(0, data.Count % 5);
        Assert.Equal(Array.IndexOf(legend, "ingot"), (int)data[3]!);   // le premier jeton : le nom de l'ingot

        var symbols = messages.First(m => (int?)m["id"] == 3)["result"]!.AsArray();
        Assert.Equal(["score", "P"], symbols.Select(s => (string?)s!["name"]));
        Assert.Equal("s", (string?)symbols[1]!["children"]![0]!["name"]);
    }

    [Theory]
    [InlineData("file:///e%3A/Rotomeca/Projets/RGL%20V3/demo/main.rgl", @"e:\Rotomeca\Projets\RGL V3\demo\main.rgl", "/e:/Rotomeca/Projets/RGL V3/demo/main.rgl")]
    [InlineData("file:///C:/projet/main.rgl", @"C:\projet\main.rgl", "/C:/projet/main.rgl")]
    [InlineData("file:///home/tommy/projet/main.rgl", @"\home\tommy\projet\main.rgl", "/home/tommy/projet/main.rgl")]
    [InlineData("untitled:Untitled-1", "untitled:Untitled-1", "untitled:Untitled-1")]
    public void Le_chemin_d_un_document_decode_le_lecteur_encode_par_l_editeur(string uri, string windows, string other)
    {
        // « file:///e%3A/… » : Uri.LocalPath rend « /e:/… », qui n'est le chemin d'aucun fichier ; le projet n'était alors pas trouvé.
        Assert.Equal(OperatingSystem.IsWindows() ? windows : other, LanguageServer.PathOf(uri));
    }

    [Fact]
    public void Une_erreur_du_binder_est_publiee_avec_son_code_et_sa_position()
    {
        var (_, messages) = Serve(Open("gear g(): int => \"texte\";\n!train |> done;"));

        var diagnostic = Assert.Single(LastDiagnostics(messages))!;
        Assert.Equal("E003", (string?)diagnostic["code"]);
        Assert.Equal(1, (int)diagnostic["severity"]!);
        Assert.Equal(0, (int)diagnostic["range"]!["start"]!["line"]!);
        Assert.Equal(17, (int)diagnostic["range"]!["start"]!["character"]!);
        Assert.Equal(24, (int)diagnostic["range"]!["end"]!["character"]!);
    }

    [Fact]
    public void Une_erreur_de_syntaxe_arrete_l_analyse_et_un_avertissement_est_publie()
    {
        var (_, syntax) = Serve(Open("train |> ;"));
        Assert.Equal("E031", (string?)LastDiagnostics(syntax)[0]!["code"]);

        var (_, warning) = Serve(Open("gear g(int n): int => n;\n!train 1 |> g !> [$ 2] |> done;"));
        var diagnostic = Assert.Single(LastDiagnostics(warning))!;
        Assert.Equal("W004", (string?)diagnostic["code"]);
        Assert.Equal(2, (int)diagnostic["severity"]!);
        Assert.Contains("aide :", (string?)diagnostic["message"]);
    }

    [Fact]
    public void Une_modification_republie_les_diagnostics_et_une_fermeture_les_efface()
    {
        var change = Notification("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "train |> done;" }),
        });
        var close = Notification("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } });

        var (_, messages) = Serve(Open("train |> ;"), change);

        var published = messages.Where(m => (string?)m["method"] == "textDocument/publishDiagnostics").ToArray();
        Assert.Equal(2, published.Length);
        Assert.NotEmpty(published[0]["params"]!["diagnostics"]!.AsArray());
        Assert.Empty(published[1]["params"]!["diagnostics"]!.AsArray());

        // Fermé aussitôt après une modification, le document n'est plus analysé : seule l'ouverture a publié, puis la fermeture efface.
        var (_, closed) = Serve(Open("train |> ;"), change, close);
        var afterClose = closed.Where(m => (string?)m["method"] == "textDocument/publishDiagnostics").ToArray();
        Assert.Equal(2, afterClose.Length);
        Assert.Empty(afterClose[^1]["params"]!["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Une_demande_n_attend_pas_l_analyse_d_une_modification_deja_arrivee()
    {
        var change = Notification("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "train |> done;" }),
        });

        var (_, messages) = Serve(Open("train |> ;"), change, Request(7, "shutdown"));

        var response = Array.FindIndex(messages.ToArray(), m => (int?)m["id"] == 7);
        var republished = Array.FindLastIndex(messages.ToArray(), m => (string?)m["method"] == "textDocument/publishDiagnostics");
        Assert.True(response >= 0 && response < republished);
    }

    [Fact]
    public void Une_demande_inconnue_recoit_une_erreur_sans_arreter_le_serveur()
    {
        var (_, messages) = Serve(Request(1, "textDocument/inconnue", new JsonObject()), Notification("inconnue"), Request(2, "shutdown"));

        Assert.Equal(-32601, (int)messages[0]["error"]!["code"]!);
        Assert.Equal(2, (int)messages[1]["id"]!);
    }

    private const string Program = """
        enum Direction { Nord, Sud }
        mold Vec2 { double x; double y; }
        const int LIMITE: 3;
        gear pure double2(int n): int {
            var resultat: n * 2;
            return resultat;
        }
        train |> done;
        """;

    [Fact]
    public void L_autocompletion_propose_les_declarations_du_fichier_et_les_mots_cles()
    {
        var items = CompletionProvider.Complete(Program, 5, 4);
        var labels = items.Select(i => i.Label).ToArray();

        Assert.Contains("double2", labels);
        Assert.Contains("Vec2", labels);
        Assert.Contains("Direction", labels);
        Assert.Contains("LIMITE", labels);
        Assert.Contains("resultat", labels);   // variable du gear où se trouve le curseur
        Assert.Contains("n", labels);
        Assert.DoesNotContain("resultat", CompletionProvider.Complete(Program, 7, 0).Select(i => i.Label));   // hors du gear
        Assert.Contains("guard", labels);
        Assert.Contains("done", labels);
        Assert.Contains("LoopError", labels);
        Assert.Equal("gear pure double2(int n): int", items.First(i => i.Label == "double2").Detail);
    }

    [Fact]
    public void Apres_un_point_l_autocompletion_propose_les_variantes_ou_les_champs()
    {
        var variants = CompletionProvider.Complete(Program + "\nDirection.N", 8, 11);
        Assert.Equal(["Nord", "Sud"], variants.Select(i => i.Label));

        // Un nom dont le type est inconnu ne propose rien : pas les champs de tous les molds, comme un éditeur de C# ou de TypeScript.
        var fields = CompletionProvider.Complete(Program + "\ninconnu.", 8, 8).Select(i => i.Label).ToArray();
        Assert.Empty(fields);
    }

    [Fact]
    public void L_autocompletion_passe_par_le_serveur()
    {
        var completion = Request(2, "textDocument/completion", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["position"] = new JsonObject { ["line"] = 7, ["character"] = 0 },
        });

        var (_, messages) = Serve(Open(Program), completion);

        var result = messages.Single(m => (int?)m["id"] == 2)["result"]!.AsArray();
        Assert.Contains(result, i => (string?)i!["label"] == "double2" && (int)i["kind"]! == 3);
    }

    [Fact]
    public void Un_argument_passe_a_un_parametre_mut_recoit_l_indication_mut()
    {
        const string text = "gear ajouter(mut [int] liste, int n): void => liste.push(n);\n"
            + "gear g(): void {\n    mut [int] nombres: [];\n    ajouter(nombres, 1);\n    nombres |> ajouter(2);\n}\n!train |> done;";
        var hints = Request(2, "textDocument/inlayHint", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 4, ["character"] = 0 } },
        });

        var (_, messages) = Serve(Request(1, "initialize", new JsonObject()), Open(text), hints);

        Assert.True((bool)messages[0]["result"]!["capabilities"]!["inlayHintProvider"]!);
        var result = messages.Single(m => (int?)m["id"] == 2)["result"]!.AsArray();
        Assert.Equal([(3, 12), (4, 4)], result.Select(h => ((int)h!["position"]!["line"]!, (int)h["position"]!["character"]!)));   // l'argument 1 n'en reçoit pas
        Assert.All(result, h => Assert.Equal("mut", (string?)h!["label"]));
    }

    [Fact]
    public void Le_survol_et_la_signature_passent_par_le_serveur()
    {
        var hover = Request(2, "textDocument/hover", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["position"] = new JsonObject { ["line"] = 3, ["character"] = 5 },
        });
        var signature = Request(3, "textDocument/signatureHelp", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["position"] = new JsonObject { ["line"] = 5, ["character"] = 25 },
        });
        const string text = "// Double un entier.\ngear double2(int n): int => n * 2;\n!train |> done;\ngear g(): int => 0;\n\ngear h(): int => double2(1);";

        var (_, messages) = Serve(Open(text), hover, signature);

        var contents = (string?)messages.Single(m => (int?)m["id"] == 2)["result"]!["contents"]!["value"];
        Assert.Contains("gear g(): int", contents);
        var help = messages.Single(m => (int?)m["id"] == 3)["result"]!;
        Assert.Equal("double2(int n): int", (string?)help["signatures"]![0]!["label"]);
        Assert.Equal(0, (int)help["activeParameter"]!);
    }
    [Theory]
    [InlineData("fr-CA", "en", "FR", "EN")]
    [InlineData("de-DE", "fr", "EN", "FR")]
    public void La_langue_du_systeme_selectionne_toute_la_documentation_malgre_la_locale_du_client(string systemCulture, string locale, string selected, string other)
    {
        const string text = """
            /// <lang name="fr"><summary>Résumé_FR</summary><param name="x">Param_FR</param><returns>Retour_FR</returns><attention>Important_FR</attention></lang>
            /// <lang name="en"><summary>Summary_EN</summary><param name="x">Param_EN</param><returns>Return_EN</returns><attention>Important_EN</attention></lang>
            gear localise(int x): int => x;
            !train |> done;
            gear h(): int => localise(1);
            """;
        static JsonObject At(int character) => new()
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["position"] = new JsonObject { ["line"] = 4, ["character"] = character },
        };
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(systemCulture);
            var (_, messages) = Serve(Request(1, "initialize", new JsonObject { ["locale"] = locale }), Open(text),
                Request(2, "textDocument/hover", At(20)), Request(3, "textDocument/completion", At(20)),
                Request(4, "textDocument/signatureHelp", At("gear h(): int => localise(".Length)));
            var hover = (string)messages.Single(m => (int?)m["id"] == 2)["result"]!["contents"]!["value"]!;
            var completion = messages.Single(m => (int?)m["id"] == 3)["result"]!.AsArray()
                .Single(item => (string?)item!["label"] == "localise");
            var signature = messages.Single(m => (int?)m["id"] == 4)["result"]!["signatures"]![0]!;
            Assert.Equal("Param_" + selected, (string)signature["parameters"]![0]!["documentation"]!["value"]!);
            foreach (var markdown in new[] { hover, (string)completion!["documentation"]!["value"]!, (string)signature["documentation"]!["value"]! })
            {
                Assert.Contains("Important_" + selected, markdown);
                Assert.Contains("Param_" + selected, markdown);
                Assert.DoesNotContain("_" + other, markdown);
                Assert.DoesNotContain("<lang", markdown);
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("@invalid!")]
    [InlineData("en")]
    public void La_locale_du_client_ne_remplace_pas_la_langue_du_systeme(string? locale)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new("fr-FR");
            var (_, messages) = Serve(Request(1, "initialize", new JsonObject { ["locale"] = locale }),
                Open("/// <lang name=\"fr\"><summary>Bonjour</summary></lang>\n/// <lang name=\"en\"><summary>Hello</summary></lang>\ngear f(): int => 1;"),
                Request(2, "textDocument/hover", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = Uri },
                    ["position"] = new JsonObject { ["line"] = 2, ["character"] = 5 },
                }));
            Assert.DoesNotContain(messages, m => m.ContainsKey("error"));
            Assert.Contains("Bonjour", (string)messages.Single(m => (int?)m["id"] == 2)["result"]!["contents"]!["value"]!);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Un_changement_de_header_republie_les_diagnostics_des_documents_ouverts()
    {
        var (_, messages) = Serve(Open("gear g(): int => \"texte\";\n!train |> done;"),
            Notification("workspace/didChangeWatchedFiles", new JsonObject
            {
                ["changes"] = new JsonArray(new JsonObject { ["uri"] = "file:///C:/projet/api.h", ["type"] = 2 }),
            }));
        var publications = messages.Where(m => (string?)m["method"] == "textDocument/publishDiagnostics").ToArray();
        Assert.Equal(2, publications.Length);
        Assert.All(publications, m => Assert.Contains(m["params"]!["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "E003"));
    }

    private static JsonObject At(int line, int character) => new() { ["textDocument"] = new JsonObject { ["uri"] = Uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } };

    [Fact]
    public void Le_serveur_annonce_les_fonctions_de_navigation()
    {
        var (_, messages) = Serve(Request(1, "initialize", new JsonObject()));

        var capabilities = messages.Single(m => (int?)m["id"] == 1)["result"]!["capabilities"]!;
        foreach (var name in new[] { "referencesProvider", "documentHighlightProvider", "foldingRangeProvider", "selectionRangeProvider", "typeDefinitionProvider", "workspaceSymbolProvider", "documentFormattingProvider", "documentRangeFormattingProvider" })
            Assert.True((bool)capabilities[name]!, name);
        Assert.True((bool)capabilities["renameProvider"]!["prepareProvider"]!);
    }

    [Fact]
    public void Renommer_une_variable_modifie_chaque_emploi_et_refuse_un_mot_du_langage()
    {
        const string text = "gear f(): int {\n    int n: 1;\n    return n + n;\n}\n!train |> done;";
        var rename = At(1, 8); rename["newName"] = "total";
        var reserved = At(1, 8); reserved["newName"] = "gear";

        var (_, messages) = Serve(Open(text), Request(2, "textDocument/prepareRename", At(1, 8)), Request(3, "textDocument/rename", rename), Request(4, "textDocument/rename", reserved),
            Request(5, "textDocument/references", At(2, 11)), Request(6, "textDocument/documentHighlight", At(2, 11)));

        Assert.Equal("n", (string?)messages.Single(m => (int?)m["id"] == 2)["result"]!["placeholder"]);
        var edits = messages.Single(m => (int?)m["id"] == 3)["result"]!["changes"]![Uri]!.AsArray();
        Assert.Equal(3, edits.Count);
        Assert.All(edits, e => Assert.Equal("total", (string?)e!["newText"]));
        Assert.Contains("mot du langage", (string?)messages.Single(m => (int?)m["id"] == 4)["error"]!["message"]);
        Assert.Equal(3, messages.Single(m => (int?)m["id"] == 5)["result"]!.AsArray().Count);
        Assert.Equal(3, messages.Single(m => (int?)m["id"] == 6)["result"]!.AsArray().Count);
    }

    [Fact]
    public void Le_formatage_et_les_actions_rapides_passent_par_le_serveur()
    {
        const string text = "gear f(): int {\nreturn 1;   \n}\n!train |> done;\n";
        var format = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["options"] = new JsonObject { ["tabSize"] = 2, ["insertSpaces"] = true },
        };
        var actions = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = 0 } },
            ["context"] = new JsonObject { ["diagnostics"] = new JsonArray() },
        };

        var (_, messages) = Serve(Request(1, "initialize", new JsonObject()), Open(text), Request(2, "textDocument/formatting", format), Request(3, "textDocument/codeAction", actions));

        var capabilities = messages.Single(m => (int?)m["id"] == 1)["result"]!["capabilities"]!;
        Assert.Contains("source.organizeImports", capabilities["codeActionProvider"]!["codeActionKinds"]!.AsArray().Select(k => (string?)k));
        var edit = messages.Single(m => (int?)m["id"] == 2)["result"]!.AsArray().Single()!;
        // Le formateur n'a aucun réglage : la largeur demandée par l'éditeur (2) est ignorée, c'est toujours quatre espaces.
        Assert.Equal("gear f(): int {\n    return 1;\n}\n!train |> done;\n", (string?)edit["newText"]);
        Assert.Equal(0, (int)edit["range"]!["start"]!["line"]!);
        Assert.Empty(messages.Single(m => (int?)m["id"] == 3)["result"]!.AsArray());
    }

    [Fact]
    public void Un_emploi_obsolete_est_publie_avec_l_etiquette_barree()
    {
        const string text = "@deprecated(\"autre\")\ngear ancien(): int => 1;\ngear f(): int => ancien();\n!train |> done;";

        var (_, messages) = Serve(Open(text));

        var warning = LastDiagnostics(messages).Single(d => (string?)d!["code"] == "W061")!;
        Assert.Equal(2, (int)warning["tags"]![0]!);
    }
}
