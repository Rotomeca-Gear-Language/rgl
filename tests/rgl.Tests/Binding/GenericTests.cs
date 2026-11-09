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
/// Tests des génériques (slice 4.4) : gears et molds génériques, instances, déduction et arguments écrits, contraintes,
/// paramètres de valeur, <c>is</c> et <c>accept</c>.
/// </summary>
public class GenericTests
{
    /// <summary>Lie <paramref name="content"/> ; la syntaxe doit être correcte.</summary>
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        var program = new Binder(diagnostics).Bind(unit);
        return (program, diagnostics.Items);
    }

    /// <summary>Les erreurs de <paramref name="content"/>, par code et position ; aucun IR ne doit être produit.</summary>
    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code} {d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];
    }

    /// <summary>Compile <paramref name="content"/> avec le runtime et Zig (avertissements interdits) et l'exécute.</summary>
    private static (int Code, string Output) CompileAndRun(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        return ZigBuild.RunWithInput(executable, null, "un argument");
    }

    private static string InGear(string declarations, string body) =>
        declarations + "\ngear test(): void {\n" + body + "\n}\n!train |> done;";

    #region Gears génériques

    [Theory]
    [InlineData("gear identity<T>(T v): T => v;", "var a: identity(3);")]
    [InlineData("gear identity<T>(T v): T => v;", "var a: identity<long>(3);")]
    [InlineData("gear premier<T>([T] xs): T => xs[0];", "var a: premier([1, 2]);")]
    [InlineData("gear deux<T>(T a, T b): T => a;", "long x: 1; var a: deux(2, x);")]
    [InlineData("gear deux<T>(T a, T b): T => a;", "long x: 1; var a: deux(x, 2);")]
    [InlineData("gear premier<T, U>((T, U) t): T => t.Item1;", "var a: premier((1, \"a\"));")]
    public void Un_gear_generique_se_deduit_ou_s_ecrit(string declarations, string body)
    {
        var (program, diagnostics) = Bind(InGear(declarations, body));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Theory]
    [InlineData("var a: deux(2, 3);", "int")]
    [InlineData("var a: deux(2, 3000000000);", "long")]
    [InlineData("var a: deux(3000000000, 2);", "long")]
    [InlineData("var a: deux(1.5, 2.5);", "float")]
    [InlineData("var a: deux(1, 2.5);", "float")]
    [InlineData("var a: deux(2.5, 1);", "float")]
    [InlineData("var a: deux(1.5, 1e300);", "double")]
    [InlineData("var a: deux<long>(2, 3);", "long")]
    public void Les_constantes_numeriques_prennent_le_type_courant_qui_les_contient(string body, string expected)
    {
        var (program, diagnostics) = Bind(InGear("gear deux<T>(T a, T b): T => a;", body));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Single(program!.Gears, g => g.Symbol.Name == $"deux<{expected}>");
    }

    [Fact]
    public void Un_nombre_non_constant_garde_son_type_et_les_constantes_s_y_adaptent()
    {
        var (program, diagnostics) = Bind(InGear("gear deux<T>(T a, T b): T => a;", "long x: 1; var a: deux(2, x); short s: 1; var b: deux(s, 3);"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Contains(program!.Gears, g => g.Symbol.Name == "deux<long>");
        Assert.Contains(program.Gears, g => g.Symbol.Name == "deux<short>");
    }

    [Fact]
    public void Un_nombre_et_un_texte_ne_s_accordent_pas()
    {
        Assert.StartsWith("E115", Errors(InGear("gear deux<T>(T a, T b): T => a;", "var a: deux(2, \"x\");"))[0]);
    }

    [Fact]
    public void Chaque_combinaison_d_arguments_cree_une_seule_instance()
    {
        var (program, _) = Bind(InGear("gear identity<T>(T v): T => v;", "var a: identity(1); var b: identity(2); var c: identity(\"x\"); var d: identity<int>(3);"));

        Assert.Equal(2, program!.Gears.Count(g => g.Symbol.Name.StartsWith("identity<", StringComparison.Ordinal)));
    }

    [Fact]
    public void Un_generique_jamais_utilise_n_est_pas_lie()
    {
        var (program, diagnostics) = Bind("gear inutile<T>(T v): T => v + v;\n!train |> done;");

        Assert.Empty(diagnostics);
        Assert.NotNull(program);
        Assert.Empty(program.Gears);
    }

    [Theory]
    [InlineData("gear defaut<T>(): T => 0;", "var a: defaut();", "E115 3:8")]                           // rien ne le déduit
    [InlineData("gear deux<T>(T a, T b): T => a;", "var a: deux(1, \"a\");", "E115 3:8")]              // deux types pour un paramètre
    [InlineData("gear id<T>(T v): T => v;", "var a: id<int, int>(1);", "E115 3:8")]                    // trop d'arguments
    [InlineData("gear id<T>(T v): T => v;", "var a: id(null);", "E115 3:8")]                           // null ne donne pas de type
    [InlineData("gear id<T>(T v): T => v;", "var a: id<10>(1);", "E116 3:11")]                         // une valeur pour un type
    public void Un_argument_generique_impossible_a_determiner_est_signale(string declarations, string body, string expected)
    {
        Assert.Equal([expected], Errors(InGear(declarations, body)));
    }

    #endregion

    #region Contraintes

    [Theory]
    [InlineData("gear f<T: number>(T v): T => v;", "var a: f(1); var b: f(2.5);")]
    [InlineData("gear f<T: {int x}>(T v): int => v.x;", "var a: f({x: 1, y: 2});")]
    [InlineData("gear f<E: error>(E e): string => e.message;", "var a: f(new Exception {message: \"m\"});")]
    public void Une_contrainte_respectee_est_acceptee(string declarations, string body)
    {
        var (program, diagnostics) = Bind(InGear(declarations, body));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Theory]
    [InlineData("gear f<T: number>(T v): T => v;", "var a: f(\"a\");")]
    [InlineData("gear f<T: {int x}>(T v): int => 0;", "var a: f(5);")]
    [InlineData("gear f<T: {int x}>(T v): int => 0;", "var a: f({x: \"a\"});")]
    [InlineData("gear f<E: error>(E e): string => \"\";", "var a: f(3);")]
    public void Une_contrainte_non_respectee_est_E114(string declarations, string body)
    {
        Assert.Equal(["E114 3:8"], Errors(InGear(declarations, body)));
    }

    [Theory]
    [InlineData("gear f<T: comparable>(T v): T => v;", "E117 1:11")]       // pas de contrainte sur un opérateur
    [InlineData("gear f<T, T>(T v): T => v;", "E117 1:11")]               // paramètre générique en double
    [InlineData("gear f<T>(int T): int => 0;", "E117 1:8")]              // même nom qu'un paramètre
    [InlineData("mold M<int n> { int x; }", "E117 1:8")]                  // un mold n'a pas de paramètre de valeur
    public void Une_declaration_generique_invalide_est_E117(string declarations, string expected)
    {
        Assert.Equal([expected], Errors(declarations + "\n!train |> done;"));
    }

    #endregion

    #region Paramètres de valeur

    [Fact]
    public void Un_parametre_de_valeur_s_ecrit_et_se_connait_a_la_compilation()
    {
        var (program, diagnostics) = Bind("const int GRAND: 100;\n" + InGear("gear borne<int max>(int v): int => v > max ? max : v;", "var a: borne<10>(3); var b: borne<GRAND>(3);"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Theory]
    [InlineData("var a: borne(3);", "E115 3:8")]                       // une valeur ne se déduit pas
    [InlineData("int k: 4; var a: borne<k>(3);", "E116 3:24")]          // une variable n'est pas connue à la compilation
    [InlineData("var a: borne<int>(3);", "E116 3:14")]                  // un type pour une valeur
    public void Un_parametre_de_valeur_mal_donne_est_signale(string body, string expected)
    {
        Assert.Equal([expected], Errors(InGear("gear borne<int max>(int v): int => max;", body)));
    }

    #endregion

    #region Molds génériques

    [Fact]
    public void Un_mold_generique_s_instancie_pour_chaque_combinaison()
    {
        var (program, diagnostics) = Bind(InGear("mold Pair<T, U> { T first; U second; }",
            "Pair<int, string> a: {first: 1, second: \"un\"}; Pair<int, string> b: {first: 2, second: \"deux\"}; Pair<string, int> c: {first: \"x\", second: 3};"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Equal(2, program!.Molds.Count(m => m.Name?.StartsWith("Pair<", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Un_mold_generique_deduit_ses_arguments_des_champs()
    {
        var (program, diagnostics) = Bind(InGear("mold Pair<T, U> { T first; U second; }",
            "var a: new Pair {first: 1, second: \"un\"}; Pair<int, string> b: a; var c: new Pair {first: 2, second: \"deux\"};"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Single(program!.Molds, m => m.Name?.StartsWith("Pair<", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Un_mold_generique_prend_ses_arguments_du_type_attendu()
    {
        var (program, diagnostics) = Bind(InGear("mold Box<T> { T value; }", "Box<long> a: new Box {value: 1};"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Single(program!.Molds, m => m.Name == "Box<long>");
    }

    [Theory]
    [InlineData("var a: new Pair {first: 1};", "E115 3:8")]                  // U ne se déduit pas
    public void Un_mold_generique_non_deductible_est_signale(string body, string expected)
    {
        Assert.StartsWith(expected[..4], Errors(InGear("mold Pair<T, U> { T first; U second; }", body))[0]);
    }

    [Theory]
    [InlineData("Pair<int> a: {first: 1};", "E115 3:1")]                    // trop peu d'arguments
    [InlineData("Pair<int, string, int> a: {first: 1};", "E115 3:1")]       // trop d'arguments
    [InlineData("Pair a: {first: 1};", "E115 3:1")]                         // pas d'arguments du tout
    [InlineData("Pair<int, string> a: {first: \"x\", second: \"y\"};", "E003 3:25")]   // le champ a le type de l'argument
    public void Un_mold_generique_mal_utilise_est_signale(string body, string expected)
    {
        Assert.StartsWith(expected[..4], Errors(InGear("mold Pair<T, U> { T first; U second: \"\"; }", body))[0]);
    }

    [Fact]
    public void Un_mold_generique_peut_se_contenir_dans_un_tableau_mais_pas_directement()
    {
        Assert.DoesNotContain(Bind(InGear("mold Arbre<T> { T v; [Arbre<T>] fils; }", "Arbre<int> a: {v: 1, fils: []};")).Diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Contains("E073", Errors(InGear("mold Boucle<T> { T v; Boucle<T>? suite; }", "Boucle<int> a: {v: 1};")).Select(e => e[..4]));
    }

    [Fact]
    public void Un_argument_de_gear_se_deduit_d_un_type_de_mold_generique()
    {
        var (program, diagnostics) = Bind(InGear("mold Boite<T> { T v; }\ngear ouvre<T>(Boite<T> b): T => b.v;", "Boite<string> b: {v: \"x\"}; var a: ouvre(b);"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    #endregion

    #region is et accept

    [Fact]
    public void Un_if_sur_un_test_de_type_ne_lie_que_la_branche_prise()
    {
        // 2 * v n'existe pas pour un string : la branche n'est liée que pour int.
        var (program, diagnostics) = Bind(InGear("gear f<T>(T v): int {\n    if (T is int) { return v * 2; }\n    return 0;\n}", "var a: f(1); var b: f(\"x\");"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void Un_test_de_type_hors_de_toute_condition_vaut_un_bool()
    {
        var (program, diagnostics) = Bind(InGear("mold Vec2 { int x; int y; }\ngear f<T>(T v): bool => T accept Vec2;", "var a: f({x: 1, y: 2}); var b: f(3);"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    #endregion

    #region Exécution

    [Fact]
    public void Un_programme_generique_compile_et_s_execute()
    {
        const string program = """
            mold Vec2 { int x; int y; }
            mold Pair<T, U> { T first; U second; }
            mold Boite<T> { T v; int n: 1; }
            error Rate { string cause; }

            gear identity<T>(T val): T => val;
            gear clamp<T: number>(T v, T lo, T hi): T {
                if (v < lo) { return lo; }
                if (v > hi) { return hi; }
                return v;
            }
            gear borne<int max>(int v): int => v > max ? max : v;
            gear champ<T: {int x}>(T v): int => v.x;
            gear erreur<E: error>(E e): string => e.message;
            gear nom<T>(T v): string {
                if (T is int) { return "entier"; }
                else if (T is string) { return "texte"; }
                else if (T accept Vec2) { return "vec"; }
                return "autre";
            }
            gear premier<T, U>(Pair<T, U> p): T => p.first;
            gear boite<T>(T v): Boite<T> => {v: v};
            gear compte<int n>(int v): int {
                if (v >= n) { return v; }
                return compte<n>(v + 1);
            }

            gear test(): uint {
                Vec2 p: {x: 3, y: 4};
                Pair<int, string> pair: {first: 5, second: "un"};
                var e: boite(p);
                `{nom(1)} {nom("a")} {nom(p)} {nom(2.5)} {identity(3)} {identity<long>(4)} {clamp(15, 0, 10)} {clamp(-1.5, 0.0, 2.0)} {borne<7>(20)} {champ(p)} {erreur(new Rate {cause: "x"})} {premier(pair)} {e.v.y} {e.n} {compte<5>(0)}` -> console;
                return 0;
            }

            !train |> $ test();
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(0, code);
        Assert.Equal("entier texte vec autre 3 4 10 0.0 7 3 échec 5 4 1 5", output);
    }

    #endregion
}
