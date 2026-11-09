using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>La connexion TLS brute et le client WebSocket <c>wss://</c>, face à un serveur Kestrel.</summary>
public class StdTlsTests
{
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string _Run(string source, string certificatePem, TempDirectory directory)
    {
        directory.File("rgl.json", "{\"name\":\"tls-client\",\"version\":\"1\",\"rgl\":\"0\"}");
        directory.File("main.rgl", source);
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var diagnostics = new DiagnosticBag();
        var project = RglProject.Load(null, directory.Path, directory.Path, diagnostics)!;
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));
        var certificate = Path.Combine(directory.Path, "cert.pem");
        File.WriteAllText(certificate, certificatePem);
        using var client = Process.Start(new ProcessStartInfo(built.Executable!)
        {
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["SSL_CERT_FILE"] = certificate },
        })!;
        if (!client.WaitForExit(30_000))
        {
            client.Kill(entireProcessTree: true);
            client.WaitForExit();
            Assert.Fail("Le client ne finit pas. Sortie : " + client.StandardOutput.ReadToEnd() + client.StandardError.ReadToEnd());
        }
        var output = client.StandardOutput.ReadToEnd().Trim();
        Assert.True(client.ExitCode == 0, $"code {client.ExitCode} : {output}");
        return output;
    }

    [ZigFact]
    public void Une_connexion_TLS_brute_et_un_WebSocket_wss_parlent_a_Kestrel()
    {
        if (!OpenSslAvailable) return;

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pem = created.ExportCertificatePem();
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port, listen => { listen.Protocols = HttpProtocols.Http1; listen.UseHttps(certificate); }));
        using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/", () => "bonjour");
        app.Map("/ws", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[256];
            for (;;)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fini", CancellationToken.None);
                    return;
                }
                var text = "echo:" + Encoding.UTF8.GetString(buffer, 0, received.Count);
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });
        app.StartAsync().GetAwaiter().GetResult();
        try
        {
            using var directory = new TempDirectory();
            var output = _Run($$"""
                use <Tls> only {tlsConnect, tlsSend, tlsReceive, tlsClose};
                use <WebSocket> only {WebSocketClient, ClientMessage, webSocketConnect, webSocketSend, webSocketReceive, webSocketClose};

                gear Test(): void {
                    const long? connection: tlsConnect('localhost', {{port}}, 5000);
                    std.writeLine(connection eq null);
                    const long id: connection ?? 0;
                    std.writeLine(tlsSend(id, "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" as bytes) ?? -1);
                    const bytes? reply: tlsReceive(id, 4096, 5000);
                    std.writeLine(((reply ?? ('' as bytes)) as? string) ?? 'illisible');
                    tlsClose(id);
                    std.writeLine(tlsConnect('localhost', 1, 500) eq null);

                    const WebSocketClient? opened: webSocketConnect('wss://localhost:{{port}}/ws', 5000);
                    if (opened eq null) { std.writeLine('wss refusé'); return; }
                    mut WebSocketClient client: opened ?? {socket: 0, buffer: '' as bytes};
                    std.writeLine(webSocketSend(client, 'salut'));
                    const ClientMessage echo: webSocketReceive(client, 5000);
                    std.writeLine(`{echo.kind}|{echo.text}`);
                    webSocketClose(client);
                }
                emit Fin(): uint => 0;
                !train |> Test |> Fin;
                """, pem, directory);
            var lines = output.Replace("\r", "").Split('\n');
            Assert.Equal("false", lines[0]);
            Assert.Contains("HTTP/1.1 200 OK", output);
            Assert.Contains("bonjour", output);
            Assert.EndsWith("true\ntrue\ntext|echo:salut", output.Replace("\r", ""));
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}
