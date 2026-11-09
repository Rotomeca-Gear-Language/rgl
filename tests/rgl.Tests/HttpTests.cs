using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le serveur HTTP de la std, du socket jusqu'au handler RGL.</summary>
public class HttpTests
{
    /// <summary>Hors Windows OpenSSL vient du système ; sous Windows de tools/openssl (fetch-tools) ou de RGL_OPENSSL_INCLUDE.</summary>
    private static bool OpenSslAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RGL_OPENSSL_INCLUDE"))) return true;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "tools", "openssl", "include", "openssl", "ssl.h"))) return true;
            return false;
        }
    }

    [ZigFact]
    public void Une_route_RGL_repond_a_une_requete_HTTP()
    {
        // Le client HTTP lie le transport HTTPS : sous Windows, OpenSSL vient de tools/openssl ou de RGL_OPENSSL_INCLUDE/LIB.
        if (!OpenSslAvailable) return;
        using var directory = new TempDirectory();
        var port = FreePort();
        var staticRoot = Path.Combine(directory.Path, "public").Replace('\\', '/');
        directory.File("public/site.css", "body { color: green; }");
        directory.File("rgl.json", "{\"name\":\"http-test\",\"version\":\"1\",\"rgl\":\"0\"}");
        directory.File("main.rgl", $$"""
            use <Http> only {HttpRequest, HttpResponse, HttpServer, WebSocketMessage};

            gear home(HttpRequest request): HttpResponse =>
                HttpResponse.html(`<h1>{request.method} {request.routeParameters['name']} {request.queryParameters['x']} {request.headers.contains('Host: localhost')}</h1>`);

            gear echo(HttpRequest request): HttpResponse => HttpResponse.text(request.body, 201);
            gear socketEcho(WebSocketMessage message): string => `{message.request.routeParameters['room']}:{message.text}`;

            guard run(): void {
                mut HttpServer server: HttpServer.create();
                server.get('/hello/:name', home);
                server.post('/echo', echo);
                server.webSocket('/socket/:room', socketEcho);
                server.staticFiles('{{staticRoot}}');
                server.listen('127.0.0.1', {{port}} as ushort, 7, 4);
            }

            train |> run !> [$ 1] |> $ 0;
            """);

        var diagnostics = new DiagnosticBag();
        var project = RglProject.Load(null, directory.Path, directory.Path, diagnostics)!;
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));

        using var clientDirectory = new TempDirectory();
        clientDirectory.File("rgl.json", "{\"name\":\"http-client-test\",\"version\":\"1\",\"rgl\":\"0\"}");
        clientDirectory.File("main.rgl", $$"""
            use <HttpClient> only {HttpClient, HttpClientResponse};
            guard run(): void {
                const HttpClientResponse response: HttpClient.create().get('http://127.0.0.1:{{port}}/hello/client?x=7');
                `{response.status}|{response.body}` -> console;
            }
            train |> run !> [$ 1] |> $ 0;
            """);
        var clientDiagnostics = new DiagnosticBag();
        var clientProject = RglProject.Load(null, clientDirectory.Path, clientDirectory.Path, clientDiagnostics)!;
        var clientBuilt = BuildPipeline.Run(clientProject, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), clientDiagnostics);
        Assert.True(clientBuilt is not null, string.Join("\n", clientDiagnostics.Items.Select(d => d.Format("."))));

        using var process = Process.Start(new ProcessStartInfo(built.Executable!)
        {
            WorkingDirectory = directory.Path,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        try
        {
            var response = Send(port, process, "GET /hello/rgl?x=42 HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

            Assert.Contains("HTTP/1.1 200 OK\n", response);
            Assert.Contains("Content-Type: text/html; charset=utf-8\n", response);
            Assert.EndsWith("\n\n<h1>GET rgl 42 true</h1>", response);

            response = Send(port, process, "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\nConnection: close\r\n\r\nsalut");
            Assert.Contains("HTTP/1.1 201 Created\n", response);
            Assert.EndsWith("\n\nsalut", response);

            using (var clientProcess = Process.Start(new ProcessStartInfo(clientBuilt.Executable!)
            {
                WorkingDirectory = clientDirectory.Path,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!)
            {
                Assert.True(clientProcess.WaitForExit(10_000));
                Assert.Equal(0, clientProcess.ExitCode);
                Assert.Equal("200|<h1>GET client 7 false</h1>", clientProcess.StandardOutput.ReadToEnd().Trim());
            }

            using (var webSocket = new ClientWebSocket())
            {
                webSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/socket/general"), CancellationToken.None).GetAwaiter().GetResult();
                webSocket.SendAsync(Encoding.UTF8.GetBytes("bonjour"), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
                var buffer = new byte[128];
                var received = webSocket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal("general:bonjour", Encoding.UTF8.GetString(buffer, 0, received.Count));
                webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            }

            response = Send(port, process, "GET /missing HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            Assert.Contains("HTTP/1.1 404 Not Found\n", response);
            response = Send(port, process, "GET /site.css HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            Assert.Contains("Content-Type: text/css; charset=utf-8\n", response);
            Assert.EndsWith("\n\nbody { color: green; }", response);
            response = Send(port, process, "GET /../main.rgl HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            Assert.Contains("HTTP/1.1 404 Not Found\n", response);
            Assert.True(process.WaitForExit(10_000), "le serveur ne s'est pas arrêté après maxRequests");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [ZigFact]
    public void Le_serveur_et_le_client_TLS_acceptent_HTTPS_et_WSS()
    {
        if (!OpenSslAvailable) return;
        using var directory = new TempDirectory();
        var port = FreePort();
        var certificate = Path.Combine(directory.Path, "cert.pem");
        var privateKey = Path.Combine(directory.Path, "key.pem");
        using (var openssl = Process.Start(new ProcessStartInfo("openssl")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            ArgumentList =
            {
                "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost",
                "-keyout", privateKey, "-out", certificate,
            },
        })!) Assert.True(openssl.WaitForExit(10_000) && openssl.ExitCode == 0, openssl.StandardError.ReadToEnd());

        directory.File("rgl.json", "{\"name\":\"https-test\",\"version\":\"1\",\"rgl\":\"0\"}");
        directory.File("main.rgl", $$"""
            use <Http> only {HttpRequest, HttpResponse, HttpServer, WebSocketMessage};
            gear home(HttpRequest request): HttpResponse => HttpResponse.text('secure');
            gear echo(WebSocketMessage message): string => `tls:{message.text}`;
            guard run(): void {
                mut HttpServer server: HttpServer.create();
                server.get('/', home);
                server.webSocket('/socket', echo);
                server.listenTls('{{certificate}}', '{{privateKey}}', '127.0.0.1', {{port}} as ushort, 2, 2);
            }
            train |> run !> [$ 1] |> $ 0;
            """);
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var diagnostics = new DiagnosticBag();
        var project = RglProject.Load(null, directory.Path, directory.Path, diagnostics)!;
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));

        using var clientDirectory = new TempDirectory();
        clientDirectory.File("rgl.json", "{\"name\":\"https-client-test\",\"version\":\"1\",\"rgl\":\"0\"}");
        clientDirectory.File("main.rgl", $$"""
            use <HttpClient> only {HttpClient, HttpClientResponse};
            guard run(): void {
                const HttpClientResponse response: HttpClient.create().get('https://localhost:{{port}}/');
                `{response.status}|{response.body}` -> console;
            }
            train |> run !> [$ 1] |> $ 0;
            """);
        var clientDiagnostics = new DiagnosticBag();
        var clientProject = RglProject.Load(null, clientDirectory.Path, clientDirectory.Path, clientDiagnostics)!;
        var clientBuilt = BuildPipeline.Run(clientProject, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), clientDiagnostics);
        Assert.True(clientBuilt is not null, string.Join("\n", clientDiagnostics.Items.Select(d => d.Format("."))));

        using var server = Process.Start(new ProcessStartInfo(built.Executable!) { WorkingDirectory = directory.Path, UseShellExecute = false })!;
        try
        {
            using (var client = Process.Start(new ProcessStartInfo(clientBuilt.Executable!)
            {
                WorkingDirectory = clientDirectory.Path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                Environment = { ["SSL_CERT_FILE"] = certificate },
            })!)
            {
                Assert.True(client.WaitForExit(10_000));
                Assert.Equal(0, client.ExitCode);
                Assert.Equal("200|secure", client.StandardOutput.ReadToEnd().Trim());
            }

            using var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            socket.ConnectAsync(new Uri($"wss://localhost:{port}/socket"), CancellationToken.None).GetAwaiter().GetResult();
            socket.SendAsync(Encoding.UTF8.GetBytes("ok"), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
            var buffer = new byte[32];
            var received = socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("tls:ok", Encoding.UTF8.GetString(buffer, 0, received.Count));
            socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(server.WaitForExit(10_000));
            Assert.Equal(0, server.ExitCode);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
        }
    }

    [ZigFact]
    public void Plusieurs_workers_executent_les_handlers_en_parallele()
    {
        using var directory = new TempDirectory();
        var port = FreePort();
        directory.File("rgl.json", "{\"name\":\"http-workers-test\",\"version\":\"1\",\"rgl\":\"0\"}");
        directory.File("main.rgl", $$"""
            use <Http> only {HttpRequest, HttpResponse, HttpServer};
            @trusted
            gear together(): bool => cpp {
                #include <atomic>
                #include <chrono>
                #include <thread>
                bool rgl() {
                    static std::atomic<int> entered{0};
                    ++entered;
                    for (int i = 0; i < 200 && entered.load() < 2; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(5));
                    return entered.load() >= 2;
                }
            }(): bool;
            gear home(HttpRequest request): HttpResponse => HttpResponse.text(together() as string, 200);
            guard run(): void {
                mut HttpServer server: HttpServer.create();
                server.get('/', home);
                server.listen('127.0.0.1', {{port}} as ushort, 2, 2);
            }
            train |> run !> [$ 1] |> $ 0;
            """);
        var diagnostics = new DiagnosticBag();
        var project = RglProject.Load(null, directory.Path, directory.Path, diagnostics)!;
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));
        using var process = Process.Start(new ProcessStartInfo(built.Executable!) { WorkingDirectory = directory.Path, UseShellExecute = false })!;
        try
        {
            var first = Task.Run(() => Send(port, process, "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
            var second = Task.Run(() => Send(port, process, "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
            Task.WaitAll(first, second);
            Assert.EndsWith("\n\ntrue", first.Result);
            Assert.EndsWith("\n\ntrue", second.Result);
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static TcpClient Connect(int port, Process process)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 100 && !process.HasExited; ++attempt)
        {
            var client = new TcpClient();
            try
            {
                client.Connect(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException error)
            {
                last = error;
                client.Dispose();
                Thread.Sleep(20);
            }
        }
        throw new InvalidOperationException("le serveur HTTP n'écoute pas", last);
    }

    private static string Send(int port, Process process, string request)
    {
        using var client = Connect(port, process);
        using var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
