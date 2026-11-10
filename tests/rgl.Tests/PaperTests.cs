using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Rgl.Binding;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Paper;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

public class PaperTests
{
    [Fact]
    public void Les_blocs_partagent_un_module_et_chaque_interpolation_est_un_fragment()
    {
        using var project = new TempDirectory();
        var path = project.File("Home.paper", """
            @rgl { const string name: '<Tommy>'; }
            <h1>Bonjour @@ name @@</h1>
            @rgl { gear strong(string value): html => html(`<strong>{value}</strong>`); }
            @@ html('<strong>RGL</strong>') @@
            """);
        var diagnostics = new DiagnosticBag();

        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);

        Assert.NotNull(document);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(2, document!.Fragments.Count);
        Assert.Contains("gear __paper_fragment_0()", document.Source.Content);
        Assert.Contains("escapeHtml(`{name}`)", document.Source.Content);
        Assert.Contains("(html('<strong>RGL</strong>'))°", document.Source.Content);
        Assert.Contains("export gear render(): html", document.Source.Content);
        Assert.Contains("export gear renderFragment(uint id): string", document.Source.Content);
        Assert.Contains("export const uint fragmentCount: 2", document.Source.Content);
    }

    [Fact]
    public void Import_file_est_une_expression_reservee_a_paper_et_respecte_html()
    {
        using var project = new TempDirectory();
        project.File("fragment.html", "<b>brut</b>");
        var path = project.File("Home.paper", "@@ import file \"fragment.html\" @@|@@ html(import file \"fragment.html\") @@");
        var diagnostics = new DiagnosticBag();

        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);

        Assert.NotNull(document);
        Assert.Empty(diagnostics.Items);
        Assert.Contains("escapeHtml(\"<b>brut</b>\")", document!.Source.Content);
        Assert.Contains("gear __paper_fragment_1(): string => \"<b>brut</b>\";", document.Source.Content);
    }

    [Fact]
    public void Reactive_devient_un_etat_de_page_passe_aux_fragments_et_handlers()
    {
        using var project = new TempDirectory();
        var path = project.File("Counter.paper", """
            @rgl {
                reactive int count: 0;
                const string title: 'Compteur';
                gear increase(): void { count +<- 1; }
            }
            <title>@@ title @@</title><button>@@ count @@</button>
            """);
        var diagnostics = new DiagnosticBag();

        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);

        Assert.NotNull(document);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(new PaperReactive("int", "count", "0", false), Assert.Single(document!.Reactives));
        Assert.Contains("export mold State", document.Source.Content);
        Assert.Contains("gear increase(mut State state): void { state.count +<- 1; }", document.Source.Content);
        Assert.Contains("gear __paper_fragment_0(mut State state): string", document.Source.Content);
        Assert.Contains("escapeHtml(`{state.count}`)", document.Source.Content);
        Assert.Contains("export gear renderWithState(mut State state): html", document.Source.Content);
        Assert.Contains("export gear render(): html", document.Source.Content);
        Assert.True(document.Source.Content.IndexOf("const string title", StringComparison.Ordinal) < document.Source.Content.IndexOf("gear createState", StringComparison.Ordinal));
    }

    [Fact]
    public void Un_evenement_html_designe_un_handler_type_et_genere_dispatch()
    {
        using var project = new TempDirectory();
        var path = project.File("Counter.paper", """
            @rgl {
                reactive int count: 0;
                gear increase(DomEvent event): void { count +<- 1; }
            }
            <button id="plus" @click="increase">@@ count @@</button>
            """);
        var diagnostics = new DiagnosticBag();

        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);

        Assert.NotNull(document);
        Assert.Empty(diagnostics.Items);
        var handler = Assert.Single(document!.Handlers);
        Assert.Equal(("click", "increase", true), (handler.Event, handler.Gear, handler.ReceivesEvent));
        Assert.Equal([0], handler.Invalidates);
        Assert.Contains("data-rgl-on-click=\\\"increase\\\"", document.Source.Content);
        Assert.Contains("export gear dispatch(mut State state, string handler, DomEvent event): [uint]", document.Source.Content);
        Assert.Contains("increase(state, event); return [0];", document.Source.Content);
        Assert.Contains("export gear handleMessage(mut State state, string message): string", document.Source.Content);
        Assert.Contains("escapeJson(__paper_fragment_0(state))", document.Source.Content);
        Assert.Contains("/rgl-paper.js", document.Source.Content);
        Assert.Contains("export guard mount(HttpServer server, string path): HttpServer", document.Source.Content);
    }

    [Fact]
    public void Une_page_reactive_se_lie_comme_un_module_type()
    {
        using var project = new TempDirectory();
        project.File("Counter.paper", "@rgl { reactive int count: 0; gear increase(DomEvent event): void { count +<- 1; } }<button @click=\"increase\">@@ count @@</button>");
        var entry = project.File("main.rgl", """
            import Counter;
            gear page(): string => Counter.render()°;
            !train |> done;
            """);
        var diagnostics = new DiagnosticBag();
        var source = SourceText.FromPath(entry, diagnostics)!;
        var paper = PaperCompiler.Compile(SourceText.FromPath(Path.Combine(project.Path, "Counter.paper"), diagnostics)!, project.Path, diagnostics);

        var modules = SourceGraph.Load(source, project.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);

        Assert.True(program is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format(project.Path))) + "\n---\n" + paper?.Source.Content);
        Assert.DoesNotContain(diagnostics.Items, d => d.Level == DiagnosticLevel.Error);
    }

    private static string BindPage(string paper, out DiagnosticBag diagnostics, string? main = null, params (string Name, string Text)[] others)
    {
        var project = new TempDirectory();
        project.File("Home.paper", paper);
        foreach (var (name, content) in others) project.File(name, content);
        var entry = project.File("main.rgl", main ?? "import Home;\ngear page(): string => Home.render()°;\n!train |> done;\n");
        diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(SourceText.FromPath(entry, diagnostics)!, project.Path, diagnostics);
        if (modules is not null) new Binder(diagnostics).Bind(modules);
        var text = string.Join("\n", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format(project.Path).Split("\\std\\").Last()));
        project.Dispose();
        return text;
    }

    [Fact]
    public void La_tete_de_page_donne_titre_scripts_et_environnement()
    {
        var errors = BindPage("""
            @rgl {
                reactive int count: 0;
                gear load(): void {
                    page.title <- 'Accueil';
                    page.script('/app.js');
                    page.env('API', 'http://x');
                    page.env('debug', true);
                    page.env('max', 5);
                }
                gear increase(): void { count +<- 1; page.title <- `Clic {count}`; }
            }
            <p>la page</p><button @click="increase">@@ count @@</button>
            """, out _);

        Assert.True(errors == "", errors);
    }

    [Fact]
    public void Le_mot_page_dans_un_texte_ne_declenche_ni_ne_reecrit_rien()
    {
        using var project = new TempDirectory();
        var path = project.File("Counter.paper", """
            @rgl {
                reactive int count: 0;
                const string label: 'la page';
                gear increase(): void { count +<- 1; }
            }
            <p>cette page</p><button @click="increase">@@ count @@ @@ label @@</button>
            """);
        var diagnostics = new DiagnosticBag();

        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);

        Assert.NotNull(document);
        Assert.True(!document!.Source.Content.Contains("PageHead"), document.Source.Content);
        Assert.Contains("const string label: 'la page';", document.Source.Content);
    }

    [Fact]
    public void Une_page_reservee_comme_nom_ou_un_load_a_parametres_est_refuse()
    {
        using var project = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var reserved = project.File("A.paper", "@rgl { reactive string page: 'x'; }<p>@@ page @@</p>");
        var load = project.File("B.paper", "@rgl { gear load(int a): void { } }<p></p>");

        Assert.Null(PaperCompiler.Compile(SourceText.FromPath(reserved, diagnostics)!, project.Path, diagnostics));
        Assert.Null(PaperCompiler.Compile(SourceText.FromPath(load, diagnostics)!, project.Path, diagnostics));

        Assert.Equal(2, diagnostics.Items.Count(d => d.Level == DiagnosticLevel.Error));
    }

    private const string ParameterPage = """
        @rgl {
            param string who;
            param int times: 2;
            reactive int count: 0;
            gear load(): void { count <- who.length as int; }
            gear increase(): void { count +<- 1; }
        }
        <p>@@ who @@ x@@ times @@</p><button @click="increase">@@ count @@</button>
        """;

    [Fact]
    public void Un_param_devient_un_champ_de_props_et_de_state_avec_trois_mount()
    {
        using var project = new TempDirectory();
        var path = project.File("Home.paper", ParameterPage);
        var diagnostics = new DiagnosticBag();

        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);

        Assert.NotNull(document);
        var code = document!.Source.Content;
        Assert.Contains("export mold Props {\n    string who;\n    int times: 2;\n}", code);
        Assert.Contains("export mold State {\n    PageHead page: {};\n    string who;\n    int times: 2;\n    int count: 0;\n}", code);
        Assert.Contains("export gear createState(Props props): State {\n    mut State state: {who: props.who, times: props.times};", code);
        Assert.Contains("export guard mount(HttpServer server, string path, Props props): HttpServer", code);
        Assert.Contains("export guard mountWith(HttpServer server, string path, (HttpRequest) => Props provider): HttpServer", code);
        Assert.DoesNotContain("mount(HttpServer server, string path): HttpServer", code);
        Assert.Contains("+ location.search", code);
    }

    [Fact]
    public void Sans_valeur_obligatoire_mount_n_a_pas_besoin_de_props()
    {
        using var project = new TempDirectory();
        var path = project.File("Home.paper", "@rgl { param string who: 'x'; }<p>@@ who @@</p>");
        var diagnostics = new DiagnosticBag();

        var document = PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);

        Assert.Contains("export gear mount(HttpServer server, string path): HttpServer {\n    return _mountPage(server, path, (HttpRequest request) => new Props {});", document!.Source.Content);
    }

    [Fact]
    public void Un_param_ne_s_ecrit_pas_et_ne_se_declare_pas_deux_fois()
    {
        using var project = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var written = project.File("A.paper", "@rgl { param int n: 1; gear change(): void { n <- 2; } }<p>@@ n @@</p>");
        var twice = project.File("B.paper", "@rgl { param int n: 1; reactive int n: 0; }<p>@@ n @@</p>");
        var malformed = project.File("C.paper", "@rgl { param ; }<p></p>");

        Assert.Null(PaperCompiler.Compile(SourceText.FromPath(written, diagnostics)!, project.Path, diagnostics));
        Assert.Null(PaperCompiler.Compile(SourceText.FromPath(twice, diagnostics)!, project.Path, diagnostics));
        Assert.Null(PaperCompiler.Compile(SourceText.FromPath(malformed, diagnostics)!, project.Path, diagnostics));

        Assert.Equal(3, diagnostics.Items.Count(d => d.Level == DiagnosticLevel.Error));
        Assert.Contains("lecture seule", diagnostics.Items[0].Message);
    }

    [Fact]
    public void Mount_prend_des_parametres_en_valeur_fixe_et_mountWith_en_calcule_par_requete()
    {
        var errors = BindPage(ParameterPage, out _, """
            use <Web>;
            use <Http> only {HttpRequest};
            import Home;
            train
            |> Web.createServer
            |> Home.mount('/', {who: 'Ann'})
            |> Home.mountWith('/p', (HttpRequest request) => new Home.Props {who: request.path, times: 3})
            |> Web.listen
            !> [$ 1]
            |> $ 0;
            """);

        Assert.True(errors == "", errors);
    }

    [ZigFact]
    public void Render_recoit_les_parametres_et_mount_les_prend_en_valeur_fixe_ou_par_requete()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Home.paper", ParameterPage);
        var entry = project.File("main.rgl", """
            import Home;
            gear show(): void { Home.render({who: 'Bob'})° -> console; }
            emit finish(): uint => 0;
            !train |> show |> finish;
            """);
        var diagnostics = new DiagnosticBag();

        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-props", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);

        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));
        var rendered = ZigBuild.Run(built!.Executable!);
        Assert.Equal(0, rendered.Item1);
        Assert.StartsWith("<p><!--rgl:f0-->Bob<!--rgl:/f0--> x<!--rgl:f1-->2<!--rgl:/f1--></p><button", rendered.Item2);
        Assert.Contains("<!--rgl:f2-->3<!--rgl:/f2-->", rendered.Item2);
    }

    [ZigFact]
    public void Le_circuit_recalcule_les_parametres_depuis_sa_propre_requete()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var port = FreePort();
        project.File("Counter.paper", ParameterPage);
        var entry = project.File("main.rgl", $$"""
            use <Web>;
            use <Http> only {HttpRequest};
            import Counter;
            train
            |> Web.createServer
            |> Counter.mountWith('/', (HttpRequest request) => new Counter.Props {who: request.queryParameters.getOr('who', 'nobody')})
            |> Web.listenWith('127.0.0.1', {{port}} as ushort, 2, 2)
            !> [$ 1]
            |> $ 0;
            """);
        var diagnostics = new DiagnosticBag();
        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-props-ws", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));

        using var process = Process.Start(new ProcessStartInfo(built!.Executable!) { WorkingDirectory = project.Path, UseShellExecute = false })!;
        using var first = new ClientWebSocket();
        using var second = new ClientWebSocket();
        try
        {
            first.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/__rgl/paper/Counter?who=bob"), CancellationToken.None).GetAwaiter().GetResult();
            second.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/__rgl/paper/Counter"), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Contains("\"html\":\"4\"", Exchange(first, "increase\nclick\n\n\n"));
            Assert.Contains("\"html\":\"7\"", Exchange(second, "increase\nclick\n\n\n"));
            first.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            second.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(process.WaitForExit(10_000));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    #region Layouts

    private const string LayoutMain = """
        @rgl {
            reactive int visits: 10;
            gear load(): void { visits <- 20; }
            gear bump(): void { visits +<- 1; }
        }
        <header>@@ visits @@</header><slot /><footer @click="bump">F</footer>
        """;

    private const string LayoutHome = """
        @layout Main;
        @rgl {
            reactive int count: 0;
            gear load(): void { visits <- visits + 5; count <- visits; }
        }
        <main>@@ count @@</main>
        """;

    private static PaperDocument? CompileHome(TempDirectory project, DiagnosticBag diagnostics, string home = LayoutHome)
    {
        var path = project.File("Home.paper", home);
        return PaperCompiler.Compile(SourceText.FromPath(path, diagnostics)!, project.Path, diagnostics);
    }

    [Fact]
    public void Le_markup_de_la_page_prend_la_place_du_slot_et_les_blocs_se_fusionnent()
    {
        using var project = new TempDirectory();
        project.File("Main.paper", LayoutMain);
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics);

        Assert.NotNull(document);
        Assert.Empty(diagnostics.Items);
        var code = document!.Source.Content;
        Assert.Equal(2, document.Fragments.Count);
        Assert.Equal(["visits", "count"], document.Fragments.Select(f => f.Expression == "visits" ? "visits" : "count"));
        Assert.Contains("PageHead page: {};\n    int visits: 10;\n    int count: 0;", code);
        Assert.Contains("gear __layout_load_1(mut State state, mut PageHead page): void", code);
        Assert.Contains("    __layout_load_1(state, page);\n    load(state, page);\n", code);
        Assert.True(code.IndexOf("<header>", StringComparison.Ordinal) < code.IndexOf("</header><main>", StringComparison.Ordinal));
        Assert.True(code.IndexOf("</header><main>", StringComparison.Ordinal) < code.IndexOf("</main><footer", StringComparison.Ordinal));
        Assert.DoesNotContain("@layout", code);
        Assert.DoesNotContain("slot", code);
    }

    [Fact]
    public void Un_handler_du_layout_n_invalide_que_les_fragments_qui_lisent_son_etat()
    {
        using var project = new TempDirectory();
        project.File("Main.paper", LayoutMain);
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics);

        var bump = Assert.Single(document!.Handlers);
        Assert.Equal("bump", bump.Gear);
        Assert.Equal([0], bump.Invalidates);
    }

    [Fact]
    public void Un_layout_peut_avoir_son_propre_layout_et_les_load_vont_du_plus_exterieur_au_plus_proche()
    {
        using var project = new TempDirectory();
        project.File("Outer.paper", "@rgl { gear load(): void { page.title <- 'outer'; } }<body><slot /></body>");
        project.File("Main.paper", "@layout Outer;\n@rgl { gear load(): void { page.title <- 'main'; } }<nav><slot /></nav>");
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics, "@layout Main;\n@rgl { gear load(): void { page.title <- 'home'; } }<p>x</p>");

        Assert.NotNull(document);
        var code = document!.Source.Content;
        Assert.Contains("    __layout_load_2(state, page);\n    __layout_load_1(state, page);\n    load(state, page);\n", code);
        Assert.Contains("\"<body><nav><p>x</p></nav></body>\"", code);
    }

    [Fact]
    public void Une_page_ou_un_layout_seul_garde_une_place_vide_pour_le_slot()
    {
        using var project = new TempDirectory();
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics, "<nav>a<slot />b</nav>");

        Assert.NotNull(document);
        Assert.Contains("\"<nav>ab</nav>\"", document!.Source.Content);
        Assert.DoesNotContain("slot", document.Source.Content);
    }

    [Theory]
    [InlineData("@layout Absent;\n<p>x</p>", "introuvable")]
    [InlineData("@layout Main;\n@rgl { gear bump(): void { } }<p>x</p>", "déclarent tous deux le gear « bump »")]
    [InlineData("@layout Main\n<p>x</p>", "mal formée")]
    public void Un_layout_invalide_est_refuse_avec_sa_raison(string home, string reason)
    {
        using var project = new TempDirectory();
        project.File("Main.paper", LayoutMain);
        var diagnostics = new DiagnosticBag();

        Assert.Null(CompileHome(project, diagnostics, home));

        Assert.Contains(reason, Assert.Single(diagnostics.Items, d => d.Level == DiagnosticLevel.Error).Message);
    }

    [Fact]
    public void Un_layout_sans_slot_avec_deux_slots_ou_qui_boucle_est_refuse()
    {
        using var project = new TempDirectory();
        project.File("None.paper", "<p>rien</p>");
        project.File("Twice.paper", "<slot /><slot />");
        project.File("Loop.paper", "@layout Loop;\n<slot />");
        var diagnostics = new DiagnosticBag();

        Assert.Null(CompileHome(project, diagnostics, "@layout None;\n<p>x</p>"));
        Assert.Null(CompileHome(project, diagnostics, "@layout Twice;\n<p>x</p>"));
        Assert.Null(CompileHome(project, diagnostics, "@layout Loop;\n<p>x</p>"));

        var messages = diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message).ToArray();
        Assert.Contains("pas de <slot />", messages[0]);
        Assert.Contains("2 <slot />", messages[1]);
        Assert.Contains("se contient lui-même", messages[2]);
    }

    [ZigFact]
    public void Layout_et_page_partagent_un_etat_et_un_clic_du_layout_ne_rafraichit_que_ses_fragments()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var port = FreePort();
        project.File("Main.paper", LayoutMain);
        project.File("Home.paper", LayoutHome);
        var entry = project.File("main.rgl", $$"""
            use <Web>;
            import Home;
            train
            |> Web.createServer
            |> Home.mount('/')
            |> Web.listenWith('127.0.0.1', {{port}} as ushort, 1, 2)
            !> [$ 1]
            |> $ 0;
            """);
        var diagnostics = new DiagnosticBag();
        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-layout", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));

        using var process = Process.Start(new ProcessStartInfo(built!.Executable!) { WorkingDirectory = project.Path, UseShellExecute = false })!;
        using var socket = new ClientWebSocket();
        try
        {
            socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/__rgl/paper/Home"), CancellationToken.None).GetAwaiter().GetResult();
            var answer = Exchange(socket, "bump\nclick\n\n\n");
            Assert.Equal("{\"type\":\"fragments\",\"items\":[{\"id\":0,\"html\":\"26\"}]}", answer);
            socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(process.WaitForExit(10_000));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [ZigFact]
    public void Le_rendu_d_une_page_avec_layout_met_la_page_dans_le_slot_apres_les_load()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Main.paper", LayoutMain);
        project.File("Home.paper", LayoutHome);
        var entry = project.File("main.rgl", """
            import Home;
            gear show(): void { Home.render()° -> console; }
            emit finish(): uint => 0;
            !train |> show |> finish;
            """);
        var diagnostics = new DiagnosticBag();

        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-layout-render", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);

        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));
        var rendered = ZigBuild.Run(built!.Executable!);
        Assert.Equal(0, rendered.Item1);
        Assert.StartsWith("<header><!--rgl:f0-->25<!--rgl:/f0--></header><main><!--rgl:f1-->25<!--rgl:/f1--></main><footer data-rgl-on-click=\"bump\">F</footer>", rendered.Item2);
    }

    [Fact]
    public void Les_constantes_de_la_page_restent_avant_les_gears_du_layout_et_des_composants()
    {
        var errors = BindPage("@layout Main;\n@rgl { const string titre: 'T'; gear f(): string => titre + site + bord(1); }<p>@@ f() @@</p><Etiquette texte=\"a\" />", out _, null,
            ("Main.paper", "@rgl { const string site: 'S'; gear deuxFois(int n): int => n + n; }<slot />"),
            ("Etiquette.paper", "@rgl { param string texte; const int bordure: 2; gear bord(int n): string => `{n}`; }<b>@@ texte @@</b>"));

        Assert.True(errors == "", errors);
    }

    [Fact]
    public void Les_constantes_remontent_en_tete_sans_toucher_au_reste()
    {
        var blocks = "gear a(): int => 1;\nconst int x: 2;\n// note\nconst string t: 'a;b{';\nmold M { int v; }\nexport const M m: {v: 1};\ngear b(): string => `{1}`;\n";

        var (constants, others) = PaperCompiler.SplitConstants(blocks);

        Assert.Equal("const int x: 2;\n// note\nconst string t: 'a;b{';\nexport const M m: {v: 1};\n", constants);
        Assert.Contains("gear a(): int => 1;", others);
        Assert.Contains("mold M { int v; }", others);
        Assert.Contains("gear b(): string => `{1}`;", others);
        Assert.DoesNotContain("const", others);
        Assert.Equal("const int x: 1;\ngear a(): int => 1;\n", PaperCompiler.HoistConstants("const int x: 1;\ngear a(): int => 1;\n"));
    }

    #endregion

    #region Composants

    private const string ComponentCard = """
        @rgl { param string titre; param int niveau: 1; }
        <section class="carte"><h2>@@ titre.toUpper() @@ (@@ niveau @@)</h2><slot /></section>
        """;

    private const string ComponentHome = """
        @rgl { reactive int count: 0; gear increase(): void { count +<- 1; } }
        <Carte titre="salut" niveau=@@ count + 1 @@><button @click="increase">@@ count @@</button></Carte><Carte titre='b' />
        """;

    [Fact]
    public void Un_composant_est_remplace_par_son_markup_avec_ses_param_et_son_contenu()
    {
        using var project = new TempDirectory();
        project.File("Carte.paper", ComponentCard);
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics, ComponentHome);

        Assert.NotNull(document);
        Assert.Empty(diagnostics.Items);
        var code = document!.Source.Content;
        Assert.Equal(["(\"salut\").toUpper()", "(count + 1)", "count", "(\"b\").toUpper()", "(1)"], document.Fragments.Select(f => f.Expression));
        Assert.DoesNotContain("<Carte", code);
        Assert.DoesNotContain("export mold Props", code);
        Assert.Contains("gear __prop_1(mut State state): string => \"salut\";", code);
        Assert.Contains("gear __prop_2(mut State state): int => state.count + 1;", code);
        Assert.Contains("gear __prop_3(mut State state): string => \"b\";", code);
        Assert.Contains("gear __prop_4(mut State state): int => 1;", code);
        Assert.Equal([1, 2], Assert.Single(document.Handlers).Invalidates);
    }

    [Fact]
    public void Un_composant_apporte_ses_gears_une_seule_fois()
    {
        using var project = new TempDirectory();
        project.File("Etiquette.paper", "@rgl { param string texte; gear pure majuscules(string s): string => s.toUpper(); }<b>@@ majuscules(texte) @@</b>");
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics, "<Etiquette texte=\"a\" /><Etiquette texte=\"b\" />");

        Assert.NotNull(document);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(document!.Source.Content, "gear pure majuscules").Count);
    }

    [Theory]
    [InlineData("<Absent />", "introuvable")]
    [InlineData("<Carte />", "demande le paramètre « titre »")]
    [InlineData("<Carte titre=\"a\" inconnu=\"b\" />", "n'a pas de paramètre « inconnu »")]
    [InlineData("<Carte titre=\"a\" titre=\"b\" />", "écrit deux fois")]
    [InlineData("<Carte titre />", "n'a pas de valeur")]
    [InlineData("<Carte titre=a />", "entre guillemets")]
    [InlineData("<Carte titre=\"a\">x", "pas refermée")]
    [InlineData("<Carte titre=\"a\"", "pas fermée")]
    public void Un_usage_invalide_de_composant_est_refuse_avec_sa_raison(string home, string reason)
    {
        using var project = new TempDirectory();
        project.File("Carte.paper", ComponentCard);
        var diagnostics = new DiagnosticBag();

        Assert.Null(CompileHome(project, diagnostics, home));

        Assert.Contains(reason, Assert.Single(diagnostics.Items, d => d.Level == DiagnosticLevel.Error).Message);
    }

    [Fact]
    public void Un_composant_n_a_ni_etat_ni_chargement_ni_layout_et_ne_s_appelle_pas_lui_meme()
    {
        using var project = new TempDirectory();
        project.File("Etat.paper", "@rgl { param int a; reactive int c: 0; }<p>@@ a @@</p>");
        project.File("Charge.paper", "@rgl { gear load(): void { } }<p></p>");
        project.File("Habille.paper", "@layout Main;\n<p></p>");
        project.File("Boucle.paper", "<Boucle />");
        project.File("Sans.paper", "<p>fixe</p>");
        var diagnostics = new DiagnosticBag();

        Assert.Null(CompileHome(project, diagnostics, "<Etat a=\"1\" />"));
        Assert.Null(CompileHome(project, diagnostics, "<Charge />"));
        Assert.Null(CompileHome(project, diagnostics, "<Habille />"));
        Assert.Null(CompileHome(project, diagnostics, "<Boucle />"));
        Assert.Null(CompileHome(project, diagnostics, "<Sans>du contenu</Sans>"));

        var messages = diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message).ToArray();
        Assert.Contains("pas d'état", messages[0]);
        Assert.Contains("pas de chargement", messages[1]);
        Assert.Contains("seule une page a un layout", messages[2]);
        Assert.Contains("se contient-il lui-même", messages[3]);
        Assert.Contains("pas de <slot />", messages[4]);
    }

    [Fact]
    public void Les_composants_s_emboitent_et_un_composant_peut_en_utiliser_un_autre()
    {
        using var project = new TempDirectory();
        project.File("Gras.paper", "@rgl { param string t; }<b>@@ t @@</b>");
        project.File("Ligne.paper", "@rgl { param string t; }<p><Gras t=@@ t @@ /><slot /></p>");
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics, "<Ligne t=\"x\"><Ligne t=\"y\"></Ligne></Ligne>");

        Assert.NotNull(document);
        Assert.Equal(["((\"x\"))", "((\"y\"))"], document!.Fragments.Select(f => f.Expression));
        Assert.Contains("\"<p><b>\"", document.Source.Content);
    }

    [Fact]
    public void Une_balise_dans_un_commentaire_ou_une_interpolation_n_est_pas_un_composant()
    {
        using var project = new TempDirectory();
        var diagnostics = new DiagnosticBag();

        var document = CompileHome(project, diagnostics, "<!-- <Absent /> --><p>@@ 1 < 2 ? 'a' : 'b' @@</p><div class=\"Titre\">T</div>");

        Assert.NotNull(document);
        Assert.Empty(diagnostics.Items);
    }

    [ZigFact]
    public void Le_rendu_d_une_page_avec_composants_utilise_les_valeurs_et_l_etat_de_la_page()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Carte.paper", ComponentCard);
        project.File("Home.paper", ComponentHome);
        var entry = project.File("main.rgl", """
            import Home;
            gear show(): void { Home.render()° -> console; }
            emit finish(): uint => 0;
            !train |> show |> finish;
            """);
        var diagnostics = new DiagnosticBag();

        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-components", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);

        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));
        var rendered = ZigBuild.Run(built!.Executable!);
        Assert.Equal(0, rendered.Item1);
        Assert.StartsWith("<section class=\"carte\"><h2><!--rgl:f0-->SALUT<!--rgl:/f0--> (<!--rgl:f1-->1<!--rgl:/f1-->)</h2>"
            + "<button data-rgl-on-click=\"increase\"><!--rgl:f2-->0<!--rgl:/f2--></button></section>"
            + "<section class=\"carte\"><h2><!--rgl:f3-->B<!--rgl:/f3--> (<!--rgl:f4-->1<!--rgl:/f4-->)</h2></section>", rendered.Item2);
    }

    #endregion

    [ZigFact]
    public void Le_rendu_initial_ecrit_la_tete_et_echappe_les_scripts()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Home.paper", """
            @rgl {
                gear load(): void {
                    page.title <- 'A</script>B';
                    page.script('/app.js');
                    page.env('API', 'x</script>');
                    page.env('debug', true);
                    page.env('max', 5);
                }
            }
            <h1>Accueil</h1>
            """);
        var entry = project.File("main.rgl", """
            import Home;
            gear show(): void { Home.render()° -> console; }
            emit finish(): uint => 0;
            !train |> show |> finish;
            """);
        var diagnostics = new DiagnosticBag();

        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-head", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);

        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));
        var rendered = ZigBuild.Run(built!.Executable!);
        var expected = "<h1>Accueil</h1><script>window.RGL_ENV=Object.assign(window.RGL_ENV||{},{\"API\":\"x\\u003c/script>\",\"debug\":true,\"max\":5});</script>"
            + "<script>document.title=\"A\\u003c/script>B\";</script><script src=\"/app.js\"></script>";
        Assert.True(rendered.Item1 == 0 && rendered.Item2 == expected, $"{rendered.Item1}\n{rendered.Item2}\n---\n{expected}");
    }

    [ZigFact]
    public void Un_handler_change_le_titre_et_envoie_chaque_script_une_fois()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var port = FreePort();
        project.File("Counter.paper", """
            @rgl {
                reactive int count: 0;
                gear load(): void { page.title <- 'Départ'; }
                gear increase(): void {
                    count +<- 1;
                    page.title <- `Clic {count}`;
                    page.script('/plus.js');
                    page.env('clics', count as long);
                }
                gear quiet(): void { }
            }
            <button @click="increase">@@ count @@</button><i @click="quiet">q</i>
            """);
        var entry = project.File("main.rgl", $$"""
            use <Web>;
            import Counter;
            train
            |> Web.createServer
            |> Counter.mount('/')
            |> Web.listenWith('127.0.0.1', {{port}} as ushort, 1, 2)
            !> [$ 1]
            |> $ 0;
            """);
        var diagnostics = new DiagnosticBag();
        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-head-ws", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));

        using var process = Process.Start(new ProcessStartInfo(built!.Executable!) { WorkingDirectory = project.Path, UseShellExecute = false })!;
        using var socket = new ClientWebSocket();
        try
        {
            socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/__rgl/paper/Counter"), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("{\"type\":\"fragments\",\"items\":[]}", Exchange(socket, "quiet\nclick\n\n\n"));
            var first = Exchange(socket, "increase\nclick\n\n\n");
            Assert.Contains("\"head\":{\"title\":\"Clic 1\",\"scripts\":[\"/plus.js\"],\"env\":{\"clics\":1}}", first);
            var second = Exchange(socket, "increase\nclick\n\n\n");
            Assert.Contains("\"head\":{\"title\":\"Clic 2\",\"scripts\":[\"/plus.js\"],\"env\":{\"clics\":2}}", second);
            socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(process.WaitForExit(10_000));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [ZigFact]
    public void Une_page_importee_se_compile_et_rend_du_html_sûr()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        project.File("Home.paper", """
            @rgl { const string name: '<Tommy>'; }
            <title>@@ name @@</title><h1>@@ name @@</h1><p>@@ html('<b>RGL</b>') @@</p>
            """);
        var entry = project.File("main.rgl", """
            import Home;
            gear show(): void { Home.render()° -> console; }
            emit finish(): uint => 0;
            !train |> show |> finish;
            """);
        var manifest = new ProjectManifest("paper", "1", null, "0");
        var rglProject = new RglProject(project.Path, entry, manifest);
        var diagnostics = new DiagnosticBag();

        var built = BuildPipeline.Run(rglProject, cache.Path, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);

        Assert.NotNull(built);
        Assert.DoesNotContain(diagnostics.Items, d => d.Level == DiagnosticLevel.Error);
        Assert.Equal((0, "<title>&lt;Tommy&gt;</title><h1><!--rgl:f1-->&lt;Tommy&gt;<!--rgl:/f1--></h1><p><!--rgl:f2--><b>RGL</b><!--rgl:/f2--></p>"), ZigBuild.Run(built!.Executable!));
        Assert.True(File.Exists(Path.Combine(project.Path, "out/debug/rgl-paper.js")));
        Assert.True(File.Exists(Path.Combine(project.Path, "out/debug/rgl-paper.wasm")));
    }

    [ZigFact]
    public void Chaque_connexion_WebSocket_possede_son_propre_circuit()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        var port = FreePort();
        project.File("Counter.paper", """
            @rgl {
                reactive int count: 0;
                gear increase(DomEvent event): void { count +<- 1; }
            }
            <button @click="increase">@@ count @@</button>
            """);
        var entry = project.File("main.rgl", $$"""
            use <Web>;
            import Counter;
            train
            |> Web.createServer
            |> Counter.mount('/')
            |> Web.listenWith('127.0.0.1', {{port}} as ushort, 2, 2)
            !> [$ 1]
            |> $ 0;
            """);
        var diagnostics = new DiagnosticBag();
        var built = BuildPipeline.Run(new RglProject(project.Path, entry, new ProjectManifest("paper-circuits", "1", null, "0")), cache.Path,
            BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache.Path), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(item => item.Format(project.Path))));

        using var process = Process.Start(new ProcessStartInfo(built!.Executable!) { WorkingDirectory = project.Path, UseShellExecute = false })!;
        using var first = new ClientWebSocket();
        using var second = new ClientWebSocket();
        try
        {
            first.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/__rgl/paper/Counter"), CancellationToken.None).GetAwaiter().GetResult();
            second.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/__rgl/paper/Counter"), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Contains("\"html\":\"1\"", Exchange(first, "increase\nclick\n\n\n"));
            Assert.Contains("\"html\":\"2\"", Exchange(first, "increase\nclick\n\n\n"));
            Assert.Contains("\"html\":\"1\"", Exchange(second, "increase\nclick\n\n\n"));
            first.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            second.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static string Exchange(ClientWebSocket socket, string message)
    {
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        var buffer = new byte[4096];
        var received = socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
