using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Surcharges de gears : signatures, meilleur candidat, défauts, params, génériques et ambiguïtés.</summary>
public class GearOverloadTests
{
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items.Select(d => d.Format("."))));
        return (new Binder(diagnostics).Bind(unit), diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "?")];
    }

    private static string Run(string declarations, string expression)
    {
        var (program, diagnostics) = Bind(declarations + $"\ngear run(): int {{ {expression} -> console; return 0; }}\n!train |> run |> $ 0;");
        Assert.True(program is not null, string.Join(Environment.NewLine, diagnostics.Select(d => d.Format("."))));
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program!, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", true,
            Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        return ZigBuild.Run(executable).Output;
    }

    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) BindFiles(params (string Name, string Text)[] files)
    {
        var diagnostics = new DiagnosticBag();
        var modules = files.Select((file, index) =>
        {
            var source = new SourceText(index == files.Length - 1 ? "main.rgl" : file.Name + ".forge", file.Text);
            return new SourceModule(file.Name, new Parser(new Lexer(source, diagnostics).Tokenize(), diagnostics).Parse(), index == files.Length - 1);
        }).ToArray();
        Assert.False(diagnostics.HasErrors);
        return (new Binder(diagnostics).Bind(modules), diagnostics.Items);
    }

    [Fact]
    public void Le_type_et_le_nombre_d_arguments_choisissent_la_surcharge()
    {
        const string gears = "gear pure f(int x): string => 'int'; gear pure f(string x): string => 'string'; gear pure f(int x, int y): string => 'deux';";
        Assert.Equal("intstringdeux", Run(gears, "`{f(1)}{f('x')}{f(1, 2)}`"));
    }

    [Fact]
    public void Une_correspondance_exacte_precede_une_conversion()
    {
        const string gears = "gear pure f(int x): string => 'int'; gear pure f(long x): string => 'long';";
        Assert.Equal("intlong", Run(gears, "`{f(1)}{f(1 as long)}`"));
    }

    [Fact]
    public void Moins_de_defauts_puis_un_parametre_normal_precedent_params()
    {
        const string gears = "gear pure f(int x): string => 'simple'; gear pure f(int x, int y: 2): string => 'defaut'; gear pure f(params [int] xs): string => 'params';";
        Assert.Equal("simpledefautparams", Run(gears, "`{f(1)}{f(1, 2)}{f(1, 2, 3)}`"));
    }

    [Fact]
    public void Un_generique_sert_de_repli_et_le_specifique_gagne()
    {
        const string gears = "gear pure f<T>(T x): string => 'generic'; gear pure f(string x): string => 'string';";
        Assert.Equal("stringgeneric", Run(gears, "`{f('x')}{f(2)}`"));
    }

    [Fact]
    public void Le_retour_et_les_effets_ne_distinguent_pas_une_signature()
    {
        Assert.Equal([DiagnosticCodes.DuplicateGear], Errors("gear f(int x): int => x; gear pure f(int x): string => 'x'; !train |> done;"));
        Assert.Equal([DiagnosticCodes.DuplicateGear], Errors("gear f([int] x): int => 1; gear f(mut [int] x): int => 2; !train |> done;"));
    }

    [Theory]
    [InlineData("E152")]
    public void Deux_conversions_equivalentes_sont_ambigues(string expectedCode)
    {
        Assert.Contains(expectedCode, Errors("gear f(long x): int => 1; gear f(double x): int => 2; gear run(): int => f(1); !train |> $ run();"));
    }

    [Fact]
    public void Les_surcharges_traversent_les_imports_qualifies_et_only()
    {
        const string library = "forge: gears\ndescription: 'test'\ngear pure f(int x): int => 1; gear pure f(string x): int => 2;";
        var (program, diagnostics) = BindFiles(("Lib", library), ("main", "import Lib; import Lib only {f}; gear run(): int => Lib.f('x') + f(1); !train |> $ 0;"));
        Assert.True(program is not null, string.Join(Environment.NewLine, diagnostics.Select(d => d.Format("."))));
    }

    [Fact]
    public void Plusieurs_generiques_peuvent_partager_un_nom()
    {
        var (program, diagnostics) = Bind("gear pure f<T>(T x): int => 1; gear pure f<T, U>(T x, U y): int => 2; gear run(): int => f(1) + f(1, 'a'); !train |> $ 0;");
        Assert.True(program is not null, string.Join(Environment.NewLine, diagnostics.Select(d => d.Format("."))));
    }

    [Fact]
    public void Les_gears_d_extension_acceptent_aussi_des_surcharges()
    {
        var (program, diagnostics) = Bind("mold M { int n; } extend M { gear pure f(this M m, int x): int => x; gear pure f(this M m, string x): int => x.length as int; } gear run(): int { M m: {n: 0}; return m.f(2) + m.f('abc'); } !train |> $ 0;");
        Assert.True(program is not null, string.Join(Environment.NewLine, diagnostics.Select(d => d.Format("."))));
    }

    [Fact]
    public void Une_reference_surchargee_utilise_le_type_de_fonction_attendu()
    {
        var (program, diagnostics) = Bind("gear pure f(int x): int => x; gear pure f(string x): string => x; gear run(): int { pure (int) => int op: f; return op(2); } !train |> $ 0;");
        Assert.True(program is not null, string.Join(Environment.NewLine, diagnostics.Select(d => d.Format("."))));
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
    }
}
