using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Tests des precasts (slice 4.6B) : instances de mold connues à la compilation, dans un fichier <c>forge: precast</c>.</summary>
public class PrecastTests
{
    private const string Geometry = "forge: mold\ndescription: \"g\"\nmold Vec2 { float x; float y; }";

    private const string Vectors = "forge: precast\ndescription: \"v\"\nimport Geometry only {Vec2};\nprecast Vec2 Zero  { x: 0.0, y: 0.0 }\nprecast Vec2 UnitX { x: 1.0, y: 0.0 }";

    private const string Config = "forge: precast\ndescription: \"c\"\nprecast Config {\n    string name: \"MonApp\";\n    bool debug: false;\n}";

    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(params (string Name, string Content)[] files)
    {
        var diagnostics = new DiagnosticBag();
        var modules = new List<SourceModule>();
        for (var i = 0; i < files.Length; ++i)
        {
            var isEntry = i == files.Length - 1;
            var path = isEntry ? "main.rgl" : files[i].Name + ".forge";
            var tokens = new Lexer(new SourceText(path, files[i].Content), diagnostics).Tokenize();
            var unit = new Parser(tokens, diagnostics).Parse();
            Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));

            FileChecks.Check(unit, isEntry, diagnostics);
            if (diagnostics.HasErrors) return (null, diagnostics.Items);
            modules.Add(new SourceModule(files[i].Name, unit, isEntry));
        }

        var program = new Binder(diagnostics).Bind(modules);
        return (program, diagnostics.Items);
    }

    private static string[] Errors(params (string Name, string Content)[] files)
    {
        var (program, diagnostics) = Bind(files);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code} {Path.GetFileName(d.Span?.Source.Path)}:{d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];
    }

    private static string Entry(string body) => "import Geometry;\nimport Vectors;\nimport Config only {Config};\ngear test(): void {\n" + body + "\n}\n!train |> done;";

    [Theory]
    [InlineData("var z: Vectors.Zero; float x: z.x;")]
    [InlineData("Geometry.Vec2 u: Vectors.UnitX;")]
    [InlineData("var c: Config; string n: c.name; bool d: c.debug;")]
    public void Un_precast_s_utilise_comme_une_constante(string body)
    {
        var (program, diagnostics) = Bind(("Geometry", Geometry), ("Vectors", Vectors), ("Config", Config), ("main", Entry(body)));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void Un_precast_s_importe_un_a_un()
    {
        var (program, diagnostics) = Bind(("Geometry", Geometry), ("Vectors", Vectors),
            ("main", "import Vectors only {UnitX as Un};\ngear test(): float => Un.x;\n!train |> done;"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void Un_precast_peut_typer_son_mold_explicitement()
    {
        const string typed = "forge: precast\ndescription: \"c\"\nprecast {string name, string version} Config {\n    name: \"MonApp\";\n    version: \"1.0.0\";\n}";
        var (program, diagnostics) = Bind(("Config", typed),
            ("main", "import Config only {Config};\ngear test(): string => Config.version;\n!train |> done;"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Theory]
    [InlineData("forge: precast\ndescription: \"c\"\nprecast Config {\n    string name;\n}", "E121 Config.forge:4:12")]                                   // champ sans valeur
    [InlineData("forge: precast\ndescription: \"c\"\ngear f(): int => 1;\nprecast Config { int n: 1; }", "E104 Config.forge:3:1")]                       // un gear n'a pas sa place
    [InlineData("forge: precast\ndescription: \"c\"\nprecast A { int n: 1; }\nprecast B { int n: 2; }", "E104 Config.forge:4:1")]                       // un seul precast qui déclare son mold
    [InlineData("forge: precast\ndescription: \"c\"\nimport Geometry only {Vec2};\nprecast A { int n: 1; }", "E104 Config.forge:4:1")]                    // déclare son mold malgré un import
    [InlineData("forge: precast\ndescription: \"c\"\nimport Geometry only {Vec2};\nimport Geometry;\nprecast Vec2 Z { x: 0.0, y: 0.0 }", "E104 Config.forge:4:1")] // au plus un import
    [InlineData("forge: precast\ndescription: \"c\"\nprecast Inconnu Z { x: 0.0 }", "E041 Config.forge:3:9")]                                          // mold inconnu
    [InlineData("forge: precast\ndescription: \"c\"\nimport Geometry only {Vec2};\nprecast Vec2 Z { x: 0.0, y: 0.0, z: 1.0 }", "E003 Config.forge:4:1")]  // champ inconnu
    [InlineData("forge: precast\ndescription: \"c\"\nimport Geometry only {Vec2};\nprecast Vec2 Z { x: \"a\", y: 0.0 }", "E003 Config.forge:4:1")]         // mauvais type
    public void Un_precast_mal_ecrit_est_signale(string content, string expected)
    {
        var errors = Errors(("Geometry", Geometry), ("Config", content), ("main", "import Config;\n!train |> done;"));

        Assert.StartsWith(expected, errors[0]);
    }

    [Fact]
    public void Un_precast_dont_la_valeur_est_calculee_a_l_execution_est_signale()
    {
        const string lib = "forge: gears\ndescription: \"l\"\nmold Vec2 { float x; float y; }\ngear un(): float => 1.0;";
        const string precast = "forge: precast\ndescription: \"c\"\nimport Lib only {Vec2, un};\nprecast Vec2 Z { x: un(), y: 0.0 }";
        var errors = Errors(("Lib", lib), ("Config", precast), ("main", "import Config;\n!train |> done;"));

        Assert.StartsWith("E121 Config.forge:4:", errors[0]);
    }

    [Fact]
    public void Un_precast_n_a_pas_sa_place_hors_d_un_fichier_precast()
    {
        const string gears = "forge: gears\ndescription: \"g\"\nprecast A { int n: 1; }";
        Assert.StartsWith("E104", Errors(("Gears", gears), ("main", "import Gears;\n!train |> done;"))[0]);
        Assert.StartsWith("E104", Errors(("main", "precast A { int n: 1; }\n!train |> done;"))[0]);
    }

    [Fact]
    public void Un_programme_avec_des_precasts_compile_et_s_execute()
    {
        const string entry = """
            import Geometry;
            import Vectors;
            import Config only {Config};

            gear test(): void {
                Geometry.Vec2 v: Vectors.UnitX;
                `{Vectors.Zero.x} {v.x} {Config.name} {Config.debug}` -> console;
            }

            !train |> test() |> $ 0;
            """;
        var (program, diagnostics) = Bind(("Geometry", Geometry), ("Vectors", Vectors), ("Config", Config), ("main", entry));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        var (code, text) = ZigBuild.RunWithInput(executable, null, "");

        Assert.Equal(0, code);
        Assert.Equal("0.0 1.0 MonApp false", text);
    }

    [Fact]
    public void Un_precast_que_rien_ne_lit_n_est_pas_ecrit_dans_le_cpp()
    {
        const string entry = "import Vectors;\ngear test(): float => Vectors.Zero.x;\n!train |> done;";
        var (program, diagnostics) = Bind(("Geometry", Geometry), ("Vectors", Vectors), ("main", entry));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));

        var code = CppEmitter.Emit(program, "main.rgl").Code;

        Assert.DoesNotContain("UnitX", code);
    }

    [Fact]
    public void Un_precast_avec_des_tableaux_lu_par_une_lambda_s_execute()
    {
        // Des tableaux dans un precast (taille fixe ou dynamique) ; celui que la lambda lit est gardé, l'autre non.
        const string table = "forge: precast\ndescription: \"t\"\nprecast Table { [int;3] ids: [1, 2, 3]; [string] data: ['a', 'b']; int n: 4; }";
        const string other = "forge: precast\ndescription: \"o\"\nprecast Autre { int n: 9; }";
        const string entry = """
            import Table only {Table};
            import Autre only {Autre};

            gear test(): void {
                () => int f: () => Table.ids[2] + Table.n;
                `{f()} {Table.ids.length} {Table.data[1]}` -> console;
            }

            !train |> test() |> $ 0;
            """;
        var (program, diagnostics) = Bind(("Table", table), ("Autre", other), ("main", entry));
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));

        var cpp = CppEmitter.Emit(program, "main.rgl").Code;
        Assert.DoesNotContain("Autre", cpp);

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, cpp);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        var (exit, text) = ZigBuild.RunWithInput(executable, null, "");

        Assert.Equal(0, exit);
        Assert.Equal("7 3 b", text);
    }
}
