using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary><c>import json settings;</c> : le fichier <c>settings.json</c>, lu à la compilation, donné en constante.</summary>
public class ImportJsonTests
{
    /// <summary>Charge le projet de <paramref name="directory"/> (son <c>main.rgl</c>) et le lie ; renvoie le programme et les diagnostics.</summary>
    private static (CppProgram? Program, string[] Errors) Build(TempDirectory directory)
    {
        var diagnostics = new DiagnosticBag();
        var path = Path.Combine(directory.Path, "main.rgl");
        var source = new SourceText(path, System.IO.File.ReadAllText(path));
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        var errors = diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code} {d.Message}").ToArray();
        return (program is null ? null : CppEmitter.Emit(program, "main.rgl"), errors);
    }

    private const string Settings = """
        {
          "nom": "Héros \"1er\"\n",
          "niveau": 3,
          "ratio": 0.5,
          "grand": 4000000000,
          "actif": true,
          "tags": ["a", "b"],
          "pos": {"x": 1, "y": 2.5},
          "table": {"a-b": 1, "c d": 2},
          "petit": 1e-3
        }
        """;

    private const string Show = """
        gear Montrer(): void {
            `{settings.nom}|{settings.niveau}|{settings.ratio}|{settings.grand}|{settings.actif}|{settings.tags.length}|{settings.tags[1]}|{settings.pos.y}|{settings.table['a-b']}|{settings.petit}` -> console;
        }
        emit Fin(): uint => 0;
        !train |> Montrer |> Fin;
        """;

    [ZigFact]
    public void Un_fichier_json_donne_une_constante_que_le_programme_lit()
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", "import json settings;\n" + Show);
        directory.File("donnees/settings.json", Settings);

        var (program, errors) = Build(directory);
        Assert.True(program is not null, string.Join(", ", errors));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, program!.Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), cpp);
        var (code, output) = ZigBuild.Run(executable);

        Assert.Equal((0, "Héros \"1er\"\n|3|0.5|4000000000|true|2|b|2.5|1|0.001"), (code, output.Replace("\r\n", "\n")));
    }

    [Fact]
    public void Une_donnee_que_rien_ne_lit_n_est_pas_ecrite_en_cpp()
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", "import json settings;\n!train |> done;");
        directory.File("settings.json", Settings);

        var (program, errors) = Build(directory);

        Assert.True(program is not null, string.Join(", ", errors));
        Assert.DoesNotContain("settings", program!.Code);
    }

    [ZigFact]
    public void Un_import_json_se_fait_aussi_dans_un_fichier_forge_et_sous_un_autre_nom()
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", """
            import Outils;
            gear Montrer(): void { `{Outils.nom()}` -> console; }
            emit Fin(): uint => 0;
            !train |> Montrer |> Fin;
            """);
        directory.File("Outils.forge", """
            forge: gears
            description: "Un fichier qui lit des données"

            import json Config as cfg;

            gear nom(): string => cfg.nom;
            """);
        directory.File("sous/dossier/Config.json", """{"nom": "lu dans Config.json"}""");

        var (program, errors) = Build(directory);
        Assert.True(program is not null, string.Join(", ", errors));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        System.IO.File.WriteAllText(cpp, program!.Code);
        var executable = ZigBuild.Compile(directory.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), cpp);

        Assert.Equal((0, "lu dans Config.json"), ZigBuild.Run(executable));
    }

    [Theory]
    [InlineData("import json absent;\n!train |> done;", null, "E107")]                                             // aucun fichier de ce nom
    [InlineData("import json settings;\n!train |> done;", """{"a": null}""", "")]                                  // null n'a pas de type
    [InlineData("import json settings;\n!train |> done;", """{"a": []}""", "")]                                    // un tableau vide non plus
    [InlineData("import json settings;\n!train |> done;", """{"a": [1, "x"]}""", "")]                              // des éléments sans type commun
    [InlineData("import json settings;\n!train |> done;", """{"a": 1,}""", "")]                                    // JSON invalide
    [InlineData("import json settings;\n!train |> done;", "", "")]                                                 // document vide
    [InlineData("import json Settings;\n!train |> done;", """{"a": 1}""", "E107")]                                 // la casse compte
    public void Un_import_json_mal_utilise_est_signale(string main, string? json, string code)
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", main);
        if (json is not null) directory.File("settings.json", json);

        var (_, errors) = Build(directory);

        Assert.NotEmpty(errors);
        if (code.Length > 0) Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void Deux_fichiers_de_meme_nom_sont_ambigus_et_un_fichier_de_molds_ne_peut_pas_en_importer()
    {
        using var directory = new TempDirectory();
        directory.File("main.rgl", "import json settings;\n!train |> done;");
        directory.File("a/settings.json", """{"a": 1}""");
        directory.File("b/settings.json", """{"a": 2}""");
        Assert.Contains(Build(directory).Errors, e => e.StartsWith("E107", StringComparison.Ordinal));

        using var other = new TempDirectory();
        other.File("main.rgl", "import Types;\n!train |> done;");
        other.File("Types.forge", "forge: mold\ndescription: \"x\"\n\nimport json settings;\n");
        other.File("settings.json", """{"a": 1}""");
        Assert.Contains(Build(other).Errors, e => e.StartsWith("E104", StringComparison.Ordinal));
    }
}
