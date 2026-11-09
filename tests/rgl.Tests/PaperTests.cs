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
