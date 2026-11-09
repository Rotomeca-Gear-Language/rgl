using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Les processus enfants qui restent ouverts.</summary>
public class StdChildTests
{
    [ZigFact]
    public void On_ecrit_sur_l_entree_d_un_enfant_et_on_lit_sa_sortie_au_fil_de_l_eau()
    {
        var output = StdHarness.Main("""
            use <Child> only {ChildProcess, ChildChunk, startChild, childWrite, closeChildInput, childReadLine, childReadErrorLine, childRead, childReadError, childWait, killChild, releaseChild};
            use <Environment> only {platform};

            gear echoer(): ChildProcess? => platform() eq 'windows' ? startChild('findstr', ['^']) : startChild('cat', []);
            gear sleeper(): ChildProcess? => platform() eq 'windows' ? startChild('ping', ['-n', '30', '127.0.0.1']) : startChild('sleep', ['30']);
            gear speaker(): ChildProcess? => platform() eq 'windows' ? startChild('cmd', ['/c', 'echo out& echo err 1>&2']) : startChild('sh', ['-c', 'echo out; echo err 1>&2']);
            gear missing(): ChildProcess? => startChild('ce-programme-n-existe-pas-xyz', []);

            gear Test(): void {
                const ChildProcess? started: echoer();
                if (started eq null) { std.writeLine('pas lancé'); return; }
                const ChildProcess echo: started ?? {id: 0};
                std.writeLine(childWrite(echo, "bonjour\nhéros\n"));
                closeChildInput(echo);
                const ChildChunk first: childReadLine(echo, 5000);
                const ChildChunk second: childReadLine(echo, 5000);
                const ChildChunk end: childReadLine(echo, 5000);
                std.writeLine(`{first.text.trim()}|{second.text.trim()}|{end.closed}|{first.closed}`);
                std.writeLine(childWait(echo, 5000) ?? -9);
                releaseChild(echo);

                const ChildProcess speaking: speaker() ?? {id: 0};
                std.writeLine(childReadLine(speaking, 5000).text.trim());
                std.writeLine(childReadErrorLine(speaking, 5000).text.trim());
                std.writeLine(childWait(speaking, 5000) ?? -9);
                std.writeLine(childRead(speaking, 200).closed);
                releaseChild(speaking);

                std.writeLine(missing() eq null);

                const ChildProcess sleeping: sleeper() ?? {id: 0};
                std.writeLine(childReadError(sleeping, 100).timedOut);
                std.writeLine(childWait(sleeping, 100) eq null);
                killChild(sleeping);
                std.writeLine(childWait(sleeping, 5000) eq null);
                releaseChild(sleeping);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\nbonjour|héros|true|false\n0\nout\nerr\n0\ntrue\ntrue\ntrue\ntrue\nfalse\n", output);
    }

    [ZigFact]
    public void Un_enfant_qui_ecrit_des_octets_non_UTF8_les_rend_intacts()
    {
        var output = StdHarness.Main("""
            use <Child> only {ChildProcess, ChildBytes, startChild, childReadBytes, childWait, releaseChild};
            use <Environment> only {platform};

            gear speaker(): ChildProcess? => platform() eq 'windows' ? startChild('cmd', ['/c', 'echo ok']) : startChild('sh', ['-c', 'echo ok']);

            gear Test(): void {
                const ChildProcess child: speaker() ?? {id: 0};
                const ChildBytes first: childReadBytes(child, 0, 5000);
                std.writeLine(first.data.length >= 2);
                std.writeLine(first.data[0]);
                std.writeLine(first.data[1]);
                const ChildBytes last: childReadBytes(child, 0, 5000);
                std.writeLine(last.closed);
                std.writeLine(childWait(child, 5000) ?? -9);
                releaseChild(child);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\n111\n107\ntrue\n0\n", output);
    }
}
