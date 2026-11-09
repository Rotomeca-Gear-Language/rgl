using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

/// <summary>Le client HTTP de la std face à un vrai serveur HTTP/2 (Kestrel).</summary>
public class StdHttp2Tests
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
        directory.File("rgl.json", "{\"name\":\"h2-client\",\"version\":\"1\",\"rgl\":\"0\"}");
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
        if (!client.WaitForExit(20_000))
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
    public void Le_client_parle_HTTP2_quand_le_serveur_le_propose_et_HTTP11_sinon()
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

        var modern = FreePort();
        var legacy = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, modern, listen => { listen.Protocols = HttpProtocols.Http1AndHttp2; listen.UseHttps(certificate); });
            options.Listen(IPAddress.Loopback, legacy, listen => { listen.Protocols = HttpProtocols.Http1; listen.UseHttps(certificate); });
        });
        using var app = builder.Build();
        app.MapGet("/", (HttpRequest r, HttpResponse response) =>
        {
            response.Headers["X-Proto"] = r.Protocol;
            return $"salut {r.Protocol}";
        });
        app.MapPost("/echo", async (HttpRequest r) =>
        {
            using var reader = new StreamReader(r.Body);
            var text = await reader.ReadToEndAsync();
            return $"{r.Protocol}|{text.Length}|{text.All(c => c == 'x')}";
        });
        app.MapGet("/gros", () => new string('z', 20_000));
        app.StartAsync().GetAwaiter().GetResult();
        try
        {
            using var directory = new TempDirectory();
            var output = _Run($$"""
                use <HttpClient> only {HttpClient, HttpClientResponse};
                guard run(): void {
                    const HttpClientResponse old: HttpClient.create().get('https://localhost:{{legacy}}/');
                    `{old.status}|{old.body}\n` -> console;
                    const HttpClientResponse first: HttpClient.create().get('https://localhost:{{modern}}/');
                    `{first.status}|{first.body}|{first.header('x-proto')}\n` -> console;
                    const HttpClientResponse echo: HttpClient.create().post('https://localhost:{{modern}}/echo', 'x'.repeat(100000));
                    `{echo.status}|{echo.body}\n` -> console;
                    const HttpClientResponse large: HttpClient.create().get('https://localhost:{{modern}}/gros');
                    `{large.body.length}\n` -> console;
                }
                train |> run !> [$ 1] |> $ 0;
                """, pem, directory);
            Assert.Equal("200|salut HTTP/1.1\n200|salut HTTP/2|HTTP/2\n200|HTTP/2|100000|True\n20000", output.Replace("\r", ""));
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}
