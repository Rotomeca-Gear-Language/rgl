using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Un serveur protégé par l'authentification Basic, avec CORS et envoi de fichiers multipart.</summary>
public class HttpAuthFeaturesTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Raw(int port, string request)
    {
        using var client = new TcpClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try { client.Connect(IPAddress.Loopback, port); break; }
            catch (SocketException) { Thread.Sleep(100); }
        }
        using var stream = client.GetStream();
        stream.ReadTimeout = 15000;
        var bytes = Encoding.UTF8.GetBytes(request);
        stream.Write(bytes, 0, bytes.Length);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    [ZigFact]
    public void Basic_cors_et_multipart_de_bout_en_bout()
    {
        using var directory = new TempDirectory();
        var port = FreePort();
        directory.File("rgl.json", "{\"name\":\"http-auth\",\"version\":\"1\",\"rgl\":\"0\"}");
        directory.File("main.rgl", $$"""
            use <Http> only {HttpRequest, HttpResponse, HttpServer};
            use <HttpAuth> only {basicAuth};
            use <Auth> only {BasicCredentials};
            use <HttpForm> only {FormPart};

            gear secret(HttpRequest request): HttpResponse =>
                HttpResponse.text(`hello {(request.basicCredentials() ?? {user: '?', password: '?'}).user}`);

            gear upload(HttpRequest request): HttpResponse {
                const [FormPart] none: [];
                const [string] lines: (request.multipart() ?? none) ||> (p) => `{p.name}:{p.fileName ?? '-'}:{p.content.length}`;
                return HttpResponse.text(lines.join(','));
            }

            guard run(): void {
                mut HttpServer server: HttpServer.create();
                server.cors('https://example.org');
                server.middleware(basicAuth((BasicCredentials c) => c.user eq 'alice' and c.password eq 'pw', 'zone'));
                server.get('/secret', secret);
                server.post('/upload', upload);
                server.listen('127.0.0.1', {{port}} as ushort, 5, 2);
            }

            train |> run !> [$ 1] |> $ 0;
            """);

        var diagnostics = new DiagnosticBag();
        var project = RglProject.Load(null, directory.Path, directory.Path, diagnostics)!;
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var built = BuildPipeline.Run(project, cache, BuildOutput.Executable, BuildProfile.Debug, ZigToolchain.Locate(cache), diagnostics);
        Assert.True(built is not null, string.Join("\n", diagnostics.Items.Select(d => d.Format("."))));

        using var process = Process.Start(new ProcessStartInfo(built.Executable!) { WorkingDirectory = directory.Path, UseShellExecute = false, RedirectStandardError = true })!;
        try
        {
            var anonymous = Raw(port, "GET /secret HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 401", anonymous);
            Assert.Contains("WWW-Authenticate: Basic realm=\"zone\"", anonymous);
            Assert.Contains("Access-Control-Allow-Origin: https://example.org", anonymous);

            var wrong = Raw(port, "GET /secret HTTP/1.1\r\nHost: x\r\nAuthorization: Basic Ym9iOnB3\r\nConnection: close\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 401", wrong);

            var allowed = Raw(port, "GET /secret HTTP/1.1\r\nHost: x\r\nAuthorization: Basic YWxpY2U6cHc=\r\nConnection: close\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200 OK", allowed);
            Assert.EndsWith("hello alice", allowed);

            var preflight = Raw(port, "OPTIONS /upload HTTP/1.1\r\nHost: x\r\nOrigin: https://example.org\r\nAccess-Control-Request-Method: POST\r\nAccess-Control-Request-Headers: x-token\r\nConnection: close\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 204", preflight);
            Assert.Contains("Access-Control-Allow-Methods: GET, POST", preflight);
            Assert.Contains("Access-Control-Allow-Headers: x-token", preflight);

            var body = "--XYZ\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nHello\r\n--XYZ\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.bin\"\r\nContent-Type: application/octet-stream\r\n\r\nxyz\r\n--XYZ--\r\n";
            var upload = Raw(port, $"POST /upload HTTP/1.1\r\nHost: x\r\nAuthorization: Basic YWxpY2U6cHc=\r\nContent-Type: multipart/form-data; boundary=XYZ\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            Assert.StartsWith("HTTP/1.1 200 OK", upload);
            Assert.EndsWith("title:-:5,file:a.bin:3", upload);
        }
        finally
        {
            if (!process.HasExited && !process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }
}
