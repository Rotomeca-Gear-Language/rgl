using System.Text;
using Rgl.Cli;

namespace Rgl.Tests;

/// <summary><c>rgl format</c> : la mise en forme officielle appliquée aux fichiers d'un projet.</summary>
public class FormatCommandTests
{
    private static (int ExitCode, string Out, string Error) Run(string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(["format", .. args], new CliContext(output, error, directory, Path.Combine(directory, ".cache")));
        return (exitCode, output.ToString(), error.ToString());
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgl-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void Un_dossier_est_mis_en_forme_et_un_second_passage_ne_change_rien()
    {
        var directory = NewDirectory();
        try
        {
            var file = Path.Combine(directory, "a.forge");
            File.WriteAllText(file, "forge: gears\ndescription: ''\n\ngear f(): int {\nreturn 1;\n}\n");
            Directory.CreateDirectory(Path.Combine(directory, "out"));
            File.WriteAllText(Path.Combine(directory, "out", "b.forge"), "gear g(): int {\nreturn 1;\n}\n");

            var first = Run(directory);
            var second = Run(directory);

            Assert.Equal(0, first.ExitCode);
            Assert.Contains("Mis en forme : a.forge", first.Out);
            Assert.Equal("forge: gears\ndescription: ''\n\ngear f(): int {\n    return 1;\n}\n", File.ReadAllText(file));
            Assert.Equal("gear g(): int {\nreturn 1;\n}\n", File.ReadAllText(Path.Combine(directory, "out", "b.forge")));
            Assert.Contains("0 fichier(s) mis en forme, 1 inchangé(s)", second.Out);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Avec_check_rien_n_est_ecrit_et_la_commande_echoue()
    {
        var directory = NewDirectory();
        try
        {
            var file = Path.Combine(directory, "a.forge");
            var text = "gear f(): int {\nreturn 1;\n}\n";
            File.WriteAllText(file, text);

            var result = Run(directory, "--check");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("À mettre en forme : a.forge", result.Out);
            Assert.Equal(text, File.ReadAllText(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Un_fichier_avec_une_erreur_de_syntaxe_n_est_pas_touche_et_le_bom_est_garde()
    {
        var directory = NewDirectory();
        try
        {
            var broken = Path.Combine(directory, "broken.forge");
            File.WriteAllText(broken, "gear f(: int {\nreturn 1;\n");
            var bom = Path.Combine(directory, "bom.forge");
            File.WriteAllBytes(bom, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("gear f(): int {\nreturn 1;\n}\n")]);

            var result = Run(directory);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("broken.forge : non mis en forme", result.Error);
            Assert.Equal("gear f(: int {\nreturn 1;\n", File.ReadAllText(broken));
            var bytes = File.ReadAllBytes(bom);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("gear f(): int {\n    return 1;\n}\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Un_chemin_inconnu_ou_d_un_autre_type_est_une_erreur()
    {
        var directory = NewDirectory();
        try
        {
            Assert.Equal(1, Run(directory, "absent.forge").ExitCode);
            File.WriteAllText(Path.Combine(directory, "a.txt"), "x");
            Assert.Equal(1, Run(directory, "a.txt").ExitCode);
        }
        finally { Directory.Delete(directory, true); }
    }
}
