using System.Text.Json;
using Rgl.Cli;

namespace Rgl.Tests;

/// <summary><c>rgl precast export</c> : les precasts d'un projet, en JSON, pour les outils.</summary>
public class PrecastExportTests
{
    private static string Project()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgl-precast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        File.WriteAllText(Path.Combine(directory, "rgl.json"), "{ \"name\": \"demo\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        File.WriteAllText(Path.Combine(directory, "main.rgl"), "import Vectors;\nimport Config;\ntrain |> done;\n");
        File.WriteAllText(Path.Combine(directory, "data", "Geometry.forge"), "forge: mold\ndescription: 'g'\nmold Vec2 { float x; float y; }\nenum Color { Red, Blue }\n");
        File.WriteAllText(Path.Combine(directory, "data", "Vectors.forge"),
            "forge: precast\ndescription: 'v'\nimport Geometry only {Vec2};\nprecast Vec2 Zero { x: 0.0, y: 0.0 }\nprecast Vec2 UnitX { x: 1.0, y: 0.5 }\n");
        File.WriteAllText(Path.Combine(directory, "data", "Config.forge"),
            "forge: precast\ndescription: 'c'\nprecast Config {\n    string name: 'MonApp';\n    bool debug: false;\n    [int] niveaux: [1, 2, 3];\n}\n");
        return directory;
    }

    private static (int ExitCode, string Out, string Error) Run(string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(["precast", .. args], new CliContext(output, error, directory, Path.Combine(directory, ".cache")));
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public void Les_precasts_sont_ecrits_en_json_meme_ceux_que_rien_ne_lit()
    {
        var directory = Project();
        try
        {
            var result = Run(directory, "export");
            Assert.True(result.ExitCode == 0, result.Out + result.Error);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "out", "precasts.json")));
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
            var precasts = root.GetProperty("precasts").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
            Assert.Equal(["Config", "UnitX", "Zero"], precasts.Keys.Order());
            Assert.Equal(0.5, precasts["UnitX"].GetProperty("value").GetProperty("y").GetDouble());
            Assert.Equal("data/Vectors.forge", precasts["Zero"].GetProperty("file").GetString());
            var config = precasts["Config"].GetProperty("value");
            Assert.Equal("MonApp", config.GetProperty("name").GetString());
            Assert.False(config.GetProperty("debug").GetBoolean());
            Assert.Equal([1, 2, 3], config.GetProperty("niveaux").EnumerateArray().Select(n => n.GetInt32()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void L_option_o_choisit_le_fichier_et_une_option_inconnue_est_refusee()
    {
        var directory = Project();
        try
        {
            Assert.Equal(0, Run(directory, "export", "-o", "sortie.json").ExitCode);
            Assert.True(File.Exists(Path.Combine(directory, "sortie.json")));
            Assert.NotEqual(0, Run(directory, "export", "--zzz").ExitCode);
            Assert.NotEqual(0, Run(directory).ExitCode);
        }
        finally { Directory.Delete(directory, true); }
    }
}
