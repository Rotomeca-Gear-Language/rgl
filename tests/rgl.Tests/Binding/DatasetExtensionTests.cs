using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Un autre fichier ajoute des entrées à un dataset : <c>extend dataset Moves { … }</c>, pour un mod ou un DLC (slice B.6).</summary>
public class DatasetExtensionTests
{
    private const string Base = "forge: gears\ndescription: \"Jeu de base\"\n\ndataset Moves as Move {\n  attaquer: {name: 'Attaquer', power: 5},\n  soin: {name: 'Soin', power: -20},\n}\n";

    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics, TempDirectory Directory) Bind(string main, params (string Path, string Content)[] files)
    {
        var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "main.rgl"), main);
        foreach (var (path, content) in files) File.WriteAllText(Path.Combine(directory.Path, path), content);

        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        return (program, diagnostics.Items, directory);
    }

    private static string[] Errors(string main, params (string Path, string Content)[] files)
    {
        var (program, diagnostics, directory) = Bind(main, files);
        using (directory)
        {
            Assert.Null(program);
            return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "")];
        }
    }

    private static (int Code, string Output) Run(string main, params (string Path, string Content)[] files)
    {
        var (program, diagnostics, directory) = Bind(main, files);
        using (directory)
        {
            Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));
            var emitted = CppEmitter.Emit(program, "main.rgl");
            var cpp = Path.Combine(directory.Path, "main.cpp");
            File.WriteAllText(cpp, emitted.Code);
            var executable = ZigBuild.CompileProgram(directory.Path, "programme", emitted, cpp,
                Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"));
            var result = ZigBuild.RunWithInput(executable, null, "");
            return (result.Code, result.Output.Replace("\r\n", "\n"));
        }
    }

    private const string Show = """
        emit fin(): uint {
            `{Moves.length} {Moves.attaquer.get().name} {Moves.soin.get().name} {Moves.braise.get().name} {Moves.glace.get().power}\n` -> console;
            return 0;
        }
        !train |> fin;
        """;

    [ZigFact]
    public void Un_fichier_importe_ajoute_des_entrees_apres_celles_du_jeu_de_base()
    {
        var mod = "forge: gears\ndescription: \"Mod\"\n\nimport Base only {Moves};\n\nextend dataset Moves {\n  braise: {name: 'Braise', power: 9},\n  glace: {name: 'Glace', power: 7},\n}\n";

        var (code, output) = Run("import Base only {Moves};\nimport Mod;\n" + Show, ("Base.forge", Base), ("Mod.forge", mod));

        Assert.Equal((0, "4 Attaquer Soin Braise 7\n"), (code, output));
    }

    [ZigFact]
    public void Un_fichier_extend_est_charge_sans_import_et_les_identifiants_de_base_ne_bougent_pas()
    {
        // Le mod est un forge: extend, chargé d'office : le programme n'a rien à importer pour l'avoir.
        var mod = "forge: extend\ndescription: \"Mod\"\nnamespace Jeu/Mod;\n\nimport Base only {Moves};\n\nextend dataset Moves {\n  braise: {name: 'Braise', power: 9},\n  glace: {name: 'Glace', power: 7},\n}\n";
        var main = """
            import Base only {Moves};
            emit fin(): uint {
                // Les entrées du jeu de base gardent leur position : attaquer 0, soin 1 ; celles du mod suivent.
                const Moves.id a: Moves.attaquer;
                const Moves.id s: Moves.soin;
                const Moves.id b: Moves.braise;
                `{a°} {s°} {b°} {Moves.length}\n` -> console;
                return 0;
            }
            !train |> fin;
            """;

        var (code, output) = Run(main, ("Base.forge", Base), ("Mod.forge", mod));

        Assert.Equal((0, "0 1 2 4\n"), (code, output));
    }

    [Fact]
    public void Une_extension_dont_le_dataset_n_est_pas_importe_est_E138()
    {
        var mod = "forge: gears\ndescription: \"Mod\"\n\nextend dataset Moves {\n  braise: {name: 'Braise', power: 9},\n}\n";

        Assert.Equal(["E138"], Errors("import Mod;\n!train |> done;", ("Mod.forge", mod)));
    }

    [Fact]
    public void Une_entree_ajoutee_qui_porte_le_nom_d_une_entree_de_base_est_E138()
    {
        var mod = "forge: gears\ndescription: \"Mod\"\n\nimport Base only {Moves};\n\nextend dataset Moves {\n  soin: {name: 'Soin 2', power: 1},\n}\n";

        // Le dataset invalide n'est pas exporté : les import « only {Moves} » qui le suivent se plaignent aussi (E109) ; la cause est la première erreur.
        var errors = Errors("import Base only {Moves};\nimport Mod;\n!train |> done;", ("Base.forge", Base), ("Mod.forge", mod));
        Assert.Equal("E138", errors[0]);
    }
}
