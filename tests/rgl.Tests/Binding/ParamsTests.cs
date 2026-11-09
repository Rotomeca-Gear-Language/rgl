using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>
/// Tests des paramètres multiples (slice 5.6) : <c>params [T] reste</c>, le dernier paramètre, reçoit tous les arguments qui restent, ou un tableau.
/// </summary>
public class ParamsTests
{
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        var program = new Binder(diagnostics).Bind(unit);
        return (program, diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "")];
    }

    /// <summary>Un programme dont le gear <c>fin</c> écrit <paramref name="body"/> sur la console ; <paramref name="declarations"/> le précède.</summary>
    private static (int Code, string Output) Run(string declarations, string body)
    {
        var (program, diagnostics) = Bind($"{declarations}\ngear fin(): uint {{\n{body}\nreturn 0;\n}}\n!train |> $ fin();");
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        var result = ZigBuild.RunWithInput(executable, null, "");
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    private const string Compte = "gear compte(params [int] valeurs): uint => valeurs.length;\n";

    [ZigFact]
    public void Params_recoit_les_arguments_en_trop_ou_un_tableau()
    {
        var (code, output) = Run(Compte, "`{compte()} {compte(5)} {compte(1, 2, 3)} {compte([4, 5])} {compte([4, 5, 6, 7])}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("0 1 3 2 4", output);
    }

    [ZigFact]
    public void Params_suit_les_parametres_fixes_et_ceux_par_defaut()
    {
        var (code, output) = Run(
            "gear f(int a, string b: \"x\", params [int] reste): string => `{a}{b}{reste.length}`;\n",
            "`{f(1)} {f(1, \"y\")} {f(1, \"y\", 2, 3)} {f(1, \"y\", [7, 8, 9, 10])}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("1x0 1y0 1y2 1y4", output);
    }

    [ZigFact]
    public void Les_elements_gardent_leur_ordre_et_leur_valeur()
    {
        var (code, output) = Run(
            "gear elements(string tete, params [int] valeurs): string => `{tete} {valeurs[0]} {valeurs[valeurs.length - 1]}`;\n",
            "`{elements(\"a\", 10, 20, 30)}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("a 10 30", output);
    }

    [ZigFact]
    public void Un_argument_de_pipeline_est_le_premier_element()
    {
        var (code, output) = Run(Compte, "`{5 |> compte(6, 7)} {4 |> compte()}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("3 1", output);
    }

    [ZigFact]
    public void Un_gear_generique_infere_son_type_des_elements_ou_du_tableau()
    {
        var (code, output) = Run(
            "gear liste<T>(params [T] xs): uint => xs.length;\ngear tete<T>(T premier, params [T] suite): uint => suite.length;\n",
            "`{liste(1, 2, 3)} {liste(\"a\")} {liste([1.5, 2.5])} {tete(1, 2, 3)} {tete(\"a\")}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("3 1 2 2 0", output);
    }

    [ZigFact]
    public void Les_elements_se_convertissent_vers_le_type_du_tableau()
    {
        var (code, output) = Run("gear somme(params [double] xs): double => xs[0] + xs[1];\n", "`{somme(1, 2.5)}` -> console;");

        Assert.Equal(0, code);
        Assert.Equal("3.5", output);
    }

    [Theory]
    [InlineData("compte(\"a\");", "E003")]                                                           // un élément qui n'est pas un int
    [InlineData("compte(1, \"a\");", "E003")]
    [InlineData("var x: compte(1, 2) + \"a\";", "E040")]
    public void Un_element_du_mauvais_type_est_refuse(string body, string code) =>
        Assert.Contains(code, Errors($"{Compte}gear test(): void {{\n{body}\n}}\n!train |> done;"));

    [Fact]
    public void Le_nombre_d_arguments_minimal_est_verifie()
    {
        var errors = Errors("gear f(int a, params [int] r): int => a;\ngear test(): void {\nf();\n}\n!train |> done;");
        Assert.Contains("E058", errors);
    }

    [Theory]
    [InlineData("gear f(params [int] a, int b): int => b;", "E134")]                                  // pas le dernier
    [InlineData("gear f(params [int;3] a): int => 1;", "E134")]                                       // pas un tableau dynamique
    [InlineData("gear f(params [int] a: [1]): int => 1;", "E134")]                                    // sans valeur par défaut
    [InlineData("gear f(mut params [int] a): int => 1;", "E134")]
    [InlineData("gear f(params [int] a, params [int] b): int => 1;", "E134")]
    public void Un_params_mal_ecrit_est_signale(string declaration, string code) =>
        Assert.Contains(code, Errors($"{declaration}\n!train |> done;"));

    [Fact]
    public void Un_train_n_a_pas_de_params()
    {
        var (program, diagnostics) = Bind("!train(params [string] args) |> done;");
        Assert.Null(program);
        Assert.Contains(diagnostics, d => d.Code == "E134");
    }

    [Fact]
    public void Un_gear_reference_comme_valeur_prend_le_tableau()
    {
        var (program, diagnostics) = Bind($"{Compte}gear test(): void {{\nvar f: compte;\nvar n: f([1, 2, 3]);\n}}\n!train |> done;");
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }
}
