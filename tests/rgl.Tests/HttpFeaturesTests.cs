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

/// <summary>Décodage des pourcentages, en-têtes, cookies, intergiciels, redirections et corps « chunked » du serveur.</summary>
public class HttpFeaturesTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Envoie une requête brute et lit la réponse jusqu'à la fermeture de la connexion.</summary>
    private static string Raw(int port, string request)
    {
        using var client = new TcpClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try { client.Connect(IPAddress.Loopback, port); break; }
            catch (SocketException) { Thread.Sleep(100); }
        }
        using var stream = client.GetStream();
        stream.ReadTimeout = 10000;
        var bytes = Encoding.UTF8.GetBytes(request);
        stream.Write(bytes, 0, bytes.Length);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>Lit une réponse (en-têtes puis corps de Content-Length octets) sur une connexion qui reste ouverte.</summary>
    private static (string Head, byte[] Body) ReadResponse(Stream stream, List<byte> buffer)
    {
        var chunk = new byte[4096];
        int HeadEnd()
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
            return -1;
        }
        int end;
        while ((end = HeadEnd()) < 0)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) throw new EndOfStreamException("connexion fermée avant la fin des en-têtes");
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        var head = Encoding.ASCII.GetString(buffer.GetRange(0, end).ToArray());
        var length = 0;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
        buffer.RemoveRange(0, end + 4);
        while (buffer.Count < length)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) throw new EndOfStreamException("connexion fermée avant la fin du corps");
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }
        var body = buffer.GetRange(0, length).ToArray();
        buffer.RemoveRange(0, length);
        return (head, body);
    }

    private static TcpClient Connect(IPAddress address, int port)
    {
        var client = new TcpClient(address.AddressFamily);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try { client.Connect(address, port); client.GetStream().ReadTimeout = 15000; return client; }
            catch (SocketException) { Thread.Sleep(100); }
        }
        throw new TimeoutException("le serveur n'écoute pas");
    }

    [ZigFact]
    public void Le_serveur_garde_la_connexion_compresse_en_gzip_et_ecoute_en_IPv6()
    {
        if (!Socket.OSSupportsIPv6) return;
        using var directory = new TempDirectory();
        var port = FreePort();
        directory.File("rgl.json", "{\"name\":\"http-keepalive\",\"version\":\"1\",\"rgl\":\"0\"}");
        directory.File("main.rgl", $$"""
            use <Http> only {HttpRequest, HttpResponse, HttpServer};

            gear hi(HttpRequest request): HttpResponse => HttpResponse.text('hi');
            gear big(HttpRequest request): HttpResponse => HttpResponse.text('a'.repeat(5000));

            guard run(): void {
                mut HttpServer server: HttpServer.create();
                server.get('/hi', hi);
                server.get('/big', big);
                server.listen('::', {{port}} as ushort, 4, 2);
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
            // Une connexion IPv6, deux requêtes puis une troisième qui demande la fermeture.
            using (var client = Connect(IPAddress.IPv6Loopback, port))
            {
                var stream = client.GetStream();
                var buffer = new List<byte>();
                var request = Encoding.ASCII.GetBytes("GET /hi HTTP/1.1\r\nHost: [::1]\r\n\r\n");
                for (var i = 0; i < 2; i++)
                {
                    stream.Write(request, 0, request.Length);
                    var (head, body) = ReadResponse(stream, buffer);
                    Assert.StartsWith("HTTP/1.1 200 OK", head);
                    Assert.Contains("Connection: keep-alive", head);
                    Assert.Equal("hi", Encoding.UTF8.GetString(body));
                }
                var last = Encoding.ASCII.GetBytes("GET /hi HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                stream.Write(last, 0, last.Length);
                var (closing, _) = ReadResponse(stream, buffer);
                Assert.Contains("Connection: close", closing);
                Assert.Equal(0, stream.Read(new byte[1], 0, 1));
            }

            // Une connexion IPv4 (la même écoute sert les deux familles) : gzip quand le client l'accepte.
            using (var client = Connect(IPAddress.Loopback, port))
            {
                var stream = client.GetStream();
                var buffer = new List<byte>();
                var request = Encoding.ASCII.GetBytes("GET /big HTTP/1.1\r\nHost: x\r\nAccept-Encoding: gzip, deflate\r\nConnection: close\r\n\r\n");
                stream.Write(request, 0, request.Length);
                var (head, body) = ReadResponse(stream, buffer);
                Assert.Contains("Content-Encoding: gzip", head);
                Assert.Contains("Vary: Accept-Encoding", head);
                Assert.True(body.Length < 500, $"corps compressé de {body.Length} octets");
                using var unpacked = new System.IO.Compression.GZipStream(new MemoryStream(body), System.IO.Compression.CompressionMode.Decompress);
                using var text = new StreamReader(unpacked, Encoding.UTF8);
                Assert.Equal(new string('a', 5000), text.ReadToEnd());
            }

            // Deux requêtes envoyées d'un coup (pipelining) reçoivent deux réponses, dans l'ordre.
            using (var client = Connect(IPAddress.Loopback, port))
            {
                var stream = client.GetStream();
                var both = Encoding.ASCII.GetBytes("GET /hi HTTP/1.1\r\nHost: x\r\n\r\nGET /big HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                stream.Write(both, 0, both.Length);
                var buffer = new List<byte>();
                var (_, first) = ReadResponse(stream, buffer);
                var (_, second) = ReadResponse(stream, buffer);
                Assert.Equal("hi", Encoding.UTF8.GetString(first));
                Assert.Equal(5000, second.Length);
            }
            // Sans Accept-Encoding, le corps reste tel quel.
            using (var client = Connect(IPAddress.Loopback, port))
            {
                var stream = client.GetStream();
                var request = Encoding.ASCII.GetBytes("GET /big HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                stream.Write(request, 0, request.Length);
                var (head, body) = ReadResponse(stream, new List<byte>());
                Assert.DoesNotContain("Content-Encoding", head);
                Assert.Equal(5000, body.Length);
            }

        }
        catch (EndOfStreamException ex) when (process.WaitForExit(3000))
        {
            throw new Exception("crash (" + ex.Message + ", code " + process.ExitCode + "): " + process.StandardError.ReadToEnd());
        }
        finally
        {
            // Un échec d'assertion laisse le serveur en vie : on l'arrête pour pouvoir effacer son dossier.
            if (!process.HasExited && !process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    [ZigFact]
    public void Le_serveur_decode_lit_les_en_tetes_et_chaine_les_intergiciels()
    {
        using var directory = new TempDirectory();
        var port = FreePort();
        directory.File("rgl.json", "{\"name\":\"http-features\",\"version\":\"1\",\"rgl\":\"0\"}");
        directory.File("main.rgl", $$"""
            use <Http> only {HttpRequest, HttpResponse, HttpServer};

            gear show(HttpRequest request): HttpResponse =>
                HttpResponse.text(`{request.routeParameters['name']}|{request.queryParameters['q']}|{request.header('X-Token') ?? 'none'}|{request.cookie('theme') ?? 'none'}`)
                    .withCookie('seen', '1', 'Path=/');

            gear moved(HttpRequest request): HttpResponse => HttpResponse.redirect('/elsewhere', 301);
            gear body(HttpRequest request): HttpResponse => HttpResponse.text(`{request.body.length}:{request.body}`);

            guard run(): void {
                mut HttpServer server: HttpServer.create();
                server.middleware((HttpRequest request, (HttpRequest) => HttpResponse next) => next(request).withHeader('X-First', 'a'));
                server.middleware((HttpRequest request, (HttpRequest) => HttpResponse next) => request.path eq '/hello/blocked' ? HttpResponse.text('stop', 403) : next(request).withHeader('X-Second', 'b'));
                server.get('/hello/:name', show);
                server.get('/old', moved);
                server.post('/body', body);
                server.listen('127.0.0.1', {{port}} as ushort, 4, 2);
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
            string first;
            try { first = Raw(port, "GET /hello/Jos%C3%A9%20B?q=a%26b+c HTTP/1.1\r\nHost: x\r\nX-Token: secret\r\nCookie: theme=dark; other=1\r\nConnection: close\r\n\r\n"); }
            catch (IOException ex) { process.WaitForExit(3000); throw new Exception("crash (" + ex.Message + ", code " + process.ExitCode + "): " + process.StandardError.ReadToEnd() + ex.StackTrace); }
            Assert.StartsWith("HTTP/1.1 200 OK", first);
            Assert.EndsWith("José B|a&b c|secret|dark", first);
            Assert.Contains("X-First: a", first);
            Assert.Contains("X-Second: b", first);
            Assert.Contains("Set-Cookie: seen=1; Path=/", first);

            var blocked = Raw(port, "GET /hello/blocked HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 403 Forbidden", blocked);
            Assert.Contains("X-First: a", blocked);
            Assert.EndsWith("stop", blocked);

            var redirect = Raw(port, "GET /old HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 301 Moved Permanently", redirect);
            Assert.Contains("Location: /elsewhere", redirect);

            var chunked = Raw(port, "POST /body HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200 OK", chunked);
            Assert.EndsWith("11:hello world", chunked);
        }
        finally
        {
            if (!process.HasExited && !process.WaitForExit(15000)) process.Kill();
        }
    }
}
