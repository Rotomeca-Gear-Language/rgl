using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Sockets TCP et UDP sur la boucle locale.</summary>
public class StdNetTests
{
    [ZigFact]
    public void Tcp_et_udp_sur_la_boucle_locale()
    {
        var output = StdHarness.Body("""
            const long server: std.tcpListen('127.0.0.1', 0) ?? -1;
            const ushort port: std.socketPort(server);
            std.writeLine(port > 0);
            const long client: std.tcpConnect('127.0.0.1', port, 2000) ?? -1;
            const long accepted: std.tcpAccept(server, 2000) ?? -1;
            std.writeLine(std.socketSend(client, 'bonjour' as bytes) ?? -1);
            const bytes? received: std.socketReceive(accepted, 100, 2000);
            const string? got: (received ?? ('?' as bytes)) as? string;
            std.writeLine(got ?? 'null');
            std.socketClose(client);
            const bytes? closed: std.socketReceive(accepted, 100, 2000);
            std.writeLine((closed ?? ('x' as bytes)).length);
            std.socketClose(accepted);
            std.socketClose(server);
            std.writeLine(std.tcpConnect('127.0.0.1', 1, 500) eq null);
            const long a: std.udpOpen('127.0.0.1', 0) ?? -1;
            const long b: std.udpOpen('127.0.0.1', 0) ?? -1;
            std.udpSend(a, '127.0.0.1', std.socketPort(b), 'ping' as bytes);
            const bytes? datagram: std.socketReceive(b, 100, 2000);
            const string? got2: (datagram ?? ('?' as bytes)) as? string;
            std.writeLine(got2 ?? 'null');
            std.writeLine(std.udpSender().startsWith('127.0.0.1:'));
            std.writeLine(std.socketReceive(a, 10, 50) eq null);
            """);

        Assert.Equal("true\n7\nbonjour\n0\ntrue\nping\ntrue\ntrue\n", output);
    }

    [ZigFact]
    public void Les_sockets_asynchrones_ne_bloquent_pas_la_boucle()
    {
        var output = StdHarness.Main("""
            @async
            emit principal(): uint {
                const long server: std.tcpListen('127.0.0.1', 0) ?? -1;
                const ushort port: std.socketPort(server);
                var accepting: std.tcpAcceptAsync(server, 5000);
                const long client: (await std.tcpConnectAsync('127.0.0.1', port, 2000)) ?? -1;
                const long accepted: (await accepting) ?? -1;
                var sent: std.socketSendAsync(client, 'bonjour' as bytes);
                const bytes received: (await std.socketReceiveAsync(accepted, 100, 2000)) ?? ('?' as bytes);
                std.writeLine((await sent) ?? -1);
                std.writeLine((received as? string) ?? 'null');
                std.writeLine((await std.tcpConnectAsync('127.0.0.1', 1, 500)) ?? -1);
                const bytes silent: (await std.socketReceiveAsync(accepted, 10, 100)) ?? ('t' as bytes);
                std.writeLine((silent as? string) ?? 'null');
                std.socketClose(client);
                std.socketClose(accepted);
                std.socketClose(server);
                return 0;
            }
            !train |> principal;
            """);

        Assert.Equal("7\nbonjour\n-1\nt\n", output);
    }

    [ZigFact]
    public void Tcp_et_udp_en_IPv6()
    {
        var output = StdHarness.Body("""
            const long server: std.tcpListen('::1', 0) ?? -1;
            const ushort port: std.socketPort(server);
            std.writeLine(port > 0);
            const long client: std.tcpConnect('::1', port, 2000) ?? -1;
            const long accepted: std.tcpAccept(server, 2000) ?? -1;
            std.socketSend(client, 'salut' as bytes);
            const bytes? received: std.socketReceive(accepted, 100, 2000);
            std.writeLine(((received ?? ('?' as bytes)) as? string) ?? 'null');
            std.socketClose(client);
            std.socketClose(accepted);
            std.socketClose(server);
            const long a: std.udpOpen('::1', 0) ?? -1;
            const long b: std.udpOpen('::1', 0) ?? -1;
            std.udpSend(a, '::1', std.socketPort(b), 'ping' as bytes);
            const bytes? datagram: std.socketReceive(b, 100, 2000);
            std.writeLine(((datagram ?? ('?' as bytes)) as? string) ?? 'null');
            std.writeLine(std.udpSender().startsWith('[::1]:'));
            """);

        Assert.Equal("true\nsalut\nping\ntrue\n", output);
    }
}
