using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Environnement, fichiers, chemins, console, journal.</summary>
public class StdSystemTests
{
    [ZigFact]
    public void L_environnement()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.arguments().join('|'));
            std.writeLine(std.setEnv('RGL_TEST_VAR', 'valeur é'));
            std.writeLine(std.getEnv('RGL_TEST_VAR') ?? 'absente');
            std.writeLine(std.getEnv('RGL_TEST_ABSENTE') eq null);
            std.writeLine(std.environment().has('RGL_TEST_VAR'));
            std.writeLine(std.platform().length > 0);
            std.writeLine(std.architecture().length > 0);
            std.writeLine(std.cpuCount() >= 1);
            std.exit(3);
            std.writeLine('jamais');
            """, expectedCode: 3);

        Assert.Equal("un argument\ntrue\nvaleur é\ntrue\ntrue\ntrue\ntrue\ntrue\n", output);
    }

    [ZigFact]
    public void Les_fichiers_et_les_chemins()
    {
        var output = StdHarness.Main("""
            guard Test(): void {
                const string root: std.join(std.executableDirectory(), 'rgl_fs_test');
                std.removePath(root, true);
                std.writeLine(std.createDirectory(std.join(root, 'sous/dossier')));
                std.writeLine(std.isDirectory(root));
                std.writeLine(std.isFile(root));
                const string file: std.join(root, 'a.txt');
                std.writeText(file, 'hello');
                std.writeLine(std.isFile(file));
                std.writeLine(std.fileSize(file) ?? -1);
                std.writeLine((std.modifiedTime(file) ?? std.now()).year() >= 2024);
                std.writeLine(std.copyFile(file, std.join(root, 'b.txt')));
                std.writeLine(std.copyFile(file, std.join(root, 'b.txt')));
                std.writeLine(std.copyFile(file, std.join(root, 'b.txt'), true));
                std.writeLine(std.moveFile(std.join(root, 'b.txt'), std.join(root, 'c.txt')));
                const [string] none: [];
                std.writeLine((std.listDirectory(root) ?? none).join(','));
                std.writeLine(std.listDirectory(file) eq null);
                std.writeLine(std.removePath(root, false));
                std.writeLine(std.removePath(root, true));
                std.writeLine(std.exists(root));
                std.writeLine(std.relative('a/b/c.txt', 'a'));
                std.writeLine(std.changeExtension('dir/f.txt', '.md'));
            }
            emit Fin(): uint => 0;
            train |> Test |> Fin;
            """);

        Assert.Equal("true\ntrue\nfalse\ntrue\n5\ntrue\ntrue\nfalse\ntrue\ntrue\na.txt,c.txt,sous\ntrue\nfalse\ntrue\nfalse\nb/c.txt\ndir/f.md\n", output);
    }

    [ZigFact]
    public void La_console_et_le_journal()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.readAll().length);
            std.writeErrorLine('erreur');
            std.writeLine(std.colorize('x', std.ANSI_RED).length);
            std.setLogLevel(std.LogLevel.Warning);
            std.info('cache');
            std.warning('visible');
            """, input: "ab\ncd\n");

        Assert.Equal("6\n10\n/!\\ [Test] visible\nerreur\n", output);
    }
}
