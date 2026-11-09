using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le client WebSocket, face à un serveur écrit en C#.</summary>
public class StdWebSocketTests
{
    private static byte[] _Frame(int opcode, bool fin, byte[] payload)
    {
        var frame = new List<byte> { (byte)((fin ? 128 : 0) | opcode) };
        if (payload.Length < 126) frame.Add((byte)payload.Length);
        else { frame.Add(126); frame.Add((byte)(payload.Length >> 8)); frame.Add((byte)payload.Length); }
        frame.AddRange(payload);
        return frame.ToArray();
    }

    private static (int Opcode, byte[] Payload) _ReadClientFrame(NetworkStream stream)
    {
        var head = new byte[2];
        stream.ReadExactly(head);
        var length = head[1] & 127;
        if (length == 126)
        {
            var extended = new byte[2];
            stream.ReadExactly(extended);
            length = (extended[0] << 8) | extended[1];
        }
        var mask = new byte[4];
        stream.ReadExactly(mask);
        var payload = new byte[length];
        stream.ReadExactly(payload);
        for (var i = 0; i < length; i++) payload[i] ^= mask[i % 4];
        return (head[0] & 15, payload);
    }

    private static string _ReadRequest(NetworkStream stream)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n"))
        {
            stream.ReadExactly(one);
            text.Append((char)one[0]);
        }
        return text.ToString();
    }

    private static string _Accept(string request)
    {
        var key = request.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:")).Substring(18).Trim();
        return Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
    }

    [ZigFact]
    public void Le_client_discute_avec_un_serveur()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = Task.Run(() =>
        {
            using var client = listener.AcceptTcpClient();
            var stream = client.GetStream();
            var request = _ReadRequest(stream);
            received.Add(request.Split("\r\n")[0]);
            var reply = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + _Accept(request) + "\r\n\r\n";
            // La poignée de main et un ping partent d'un seul envoi : le client doit garder les trames déjà arrivées.
            stream.Write([.. Encoding.ASCII.GetBytes(reply), .. _Frame(9, true, Encoding.ASCII.GetBytes("p1"))]);
            // Un message coupé en deux trames, puis un message binaire.
            stream.Write(_Frame(1, false, Encoding.UTF8.GetBytes("sal")));
            stream.Write(_Frame(0, true, Encoding.UTF8.GetBytes("ut héros")));
            stream.Write(_Frame(2, true, [1, 2, 3]));
            var pong = _ReadClientFrame(stream);
            received.Add($"{pong.Opcode}:{Encoding.ASCII.GetString(pong.Payload)}");
            var text = _ReadClientFrame(stream);
            received.Add($"{text.Opcode}:{Encoding.UTF8.GetString(text.Payload)}");
            stream.Write(_Frame(1, true, text.Payload));
            var big = _ReadClientFrame(stream);
            received.Add($"{big.Opcode}:{big.Payload.Length}");
            stream.Write(_Frame(8, true, [3, 232, .. Encoding.ASCII.GetBytes("bye")]));
            var close = _ReadClientFrame(stream);
            received.Add($"{close.Opcode}:{close.Payload.Length}");
        });

        var output = StdHarness.Main($$"""
            use <WebSocket> only {WebSocketClient, ClientMessage, webSocketConnect, webSocketSend, webSocketSendBytes, webSocketReceive, webSocketClose};

            gear Test(): void {
                const WebSocketClient? opened: webSocketConnect('ws://127.0.0.1:{{port}}/chat', 3000);
                if (opened eq null) { std.writeLine('refusé'); return; }
                mut WebSocketClient client: opened ?? {socket: 0, buffer: '' as bytes};
                const ClientMessage first: webSocketReceive(client, 3000);
                std.writeLine(`{first.kind}|{first.text}`);
                const ClientMessage second: webSocketReceive(client, 3000);
                std.writeLine(`{second.kind}|{second.data.length}|{second.data[2]}`);
                std.writeLine(webSocketSend(client, 'écho'));
                const ClientMessage echo: webSocketReceive(client, 3000);
                std.writeLine(`{echo.kind}|{echo.text}`);
                std.writeLine(webSocketSendBytes(client, '' as bytes));
                std.writeLine(webSocketSendBytes(client, [0..<300] ||> (i) => (i % 256) as ubyte));
                const ClientMessage last: webSocketReceive(client, 3000);
                std.writeLine(`{last.kind}|{last.code}|{last.text}`);
                const ClientMessage silent: webSocketReceive(client, 100);
                std.writeLine(silent.kind);
                webSocketClose(client);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);
        server.Wait(TimeSpan.FromSeconds(10));
        listener.Stop();

        Assert.Equal("text|salut héros\nbinary|3|3\ntrue\ntext|écho\ntrue\ntrue\nclose|1000|bye\ntimeout\n", output);
        Assert.Equal("GET /chat HTTP/1.1", received[0]);
        Assert.Equal("10:p1", received[1]);
        Assert.Equal("1:écho", received[2]);
    }

    [ZigFact]
    public void Une_poignee_de_main_refusee_ou_une_adresse_invalide_donnent_null()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() =>
        {
            using var client = listener.AcceptTcpClient();
            var stream = client.GetStream();
            _ReadRequest(stream);
            stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));
        });

        var output = StdHarness.Body($$"""
            std.writeLine(std.webSocketConnect('ws://127.0.0.1:{{port}}/', 3000) eq null);
            std.writeLine(std.webSocketConnect('http://127.0.0.1/', 1000) eq null);
            std.writeLine(std.webSocketConnect('ws://127.0.0.1:1/', 500) eq null);
            """);
        server.Wait(TimeSpan.FromSeconds(10));
        listener.Stop();

        Assert.Equal("true\ntrue\ntrue\n", output);
    }
}
