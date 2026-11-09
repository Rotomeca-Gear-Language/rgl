using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le terminal (sans terminal : les replis), les signaux et l'observation de fichiers.</summary>
public class StdTerminalSignalsTests
{
    [ZigFact]
    public void Sans_terminal_les_saisies_se_replient_sur_des_lignes()
    {
        var output = StdHarness.Main("""
            use <Terminal> only {isTerminal, isTerminalOutput, terminalColumns, terminalRows, readPassword, readKey, chooseMenu, progressBar, showProgress};

            gear Test(): void {
                std.writeLine(`{isTerminal()} {isTerminalOutput()} {terminalColumns() > 0} {terminalRows() > 0}`);
                std.writeLine(readPassword('mot de passe : ') + '|');
                std.writeLine(readKey(10) eq null);
                const long? choice: chooseMenu('Choisis', ['rouge', 'vert', 'bleu']);
                std.writeLine(choice ?? -1);
                const long? invalid: chooseMenu('', ['a', 'b']);
                std.writeLine(invalid ?? -1);
                const long? tooBig: chooseMenu('', ['a', 'b']);
                std.writeLine(tooBig ?? -1);
                std.writeLine(chooseMenu('', []) eq null);
                std.writeLine(progressBar(0, 10, 10));
                std.writeLine(progressBar(5, 10, 10));
                std.writeLine(progressBar(10, 10, 10));
                std.writeLine(progressBar(99, 10, 10));
                std.writeLine(progressBar(3, 0, 4));
                showProgress(1, 2, 'tâche', 8);
                showProgress(2, 2, 'tâche', 8);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """, "secret\n2\nabc\n7\n");

        Assert.Equal(
            "false false true true\n"
            + "mot de passe : secret|\n"
            + "true\n"
            + "Choisis\n1) rouge\n2) vert\n3) bleu\n1\n"
            + "1) a\n2) b\n-1\n"
            + "1) a\n2) b\n-1\n"
            + "true\n"
            + "[----------] 0%\n[#####-----] 50%\n[##########] 100%\n[##########] 100%\n[----] 0%\n"
            + "\rtâche [####----] 50%\rtâche [########] 100%\n", output);
    }

    [ZigFact]
    public void Les_signaux_sont_gardes_et_l_observation_rend_une_liste_vide_au_delai()
    {
        var output = StdHarness.Main("""
            use <Signals> only {FileChange, onSignal, takeSignal, waitSignal, watch};

            @trusted
            gear raiseInterrupt(): void => cpp {
                #include <csignal>
                rgl_return rgl() { std::raise(SIGINT); }
            }(): void;

            gear Test(): void {
                std.writeLine(onSignal('interrupt'));
                std.writeLine(onSignal('inconnu'));
                std.writeLine(takeSignal() eq null);
                raiseInterrupt();
                std.writeLine(takeSignal() ?? 'rien');
                std.writeLine(takeSignal() eq null);
                std.writeLine(waitSignal(80) eq null);
                const [FileChange] changes: watch('.', 120);
                std.writeLine(changes.length);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\nfalse\ntrue\ninterrupt\ntrue\ntrue\n0\n", output);
    }
}
