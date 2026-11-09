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
/// Tests de la surcharge d'opérateurs et des conversions déclarées dans un mold (slice 4.5) : opérateurs binaires et
/// unaires, ordre des opérandes, comparaisons déduites, conversions <c>implicit</c> et <c>explicit</c>, E009, E010, E118.
/// </summary>
public class OverloadTests
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

    /// <summary>Les erreurs de <paramref name="content"/>, par code ; aucun IR ne doit être produit.</summary>
    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "?")];
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

    private const string Vectors = """
        mold Vec3 { float x; float y; float z; }
        mold Vec2 {
            float x;
            float y;

            Vec2 +(Vec2 a, Vec2 b) { return {x: a.x + b.x, y: a.y + b.y}; }
            Vec2 -(Vec2 a, Vec2 b) { return {x: a.x - b.x, y: a.y - b.y}; }
            Vec2 -(Vec2 v) { return {x: -v.x, y: -v.y}; }
            Vec2 *(Vec2 v, float k) { return {x: v.x * k, y: v.y * k}; }
            Vec2 *(float k, Vec2 v) { return v * k; }
            bool eq(Vec2 a, Vec2 b) { return a.x eq b.x and a.y eq b.y; }
            bool <(Vec2 a, Vec2 b) { return a.x < b.x; }
            bool >(Vec2 a, Vec2 b) { return a.x > b.x; }

            Vec3 implicit(Vec2 v) { return {x: v.x, y: v.y, z: 0.0}; }
            string explicit(Vec2 v) { return `({v.x}, {v.y})`; }
        }

        """;

    private static string InGear(string body) => Vectors + "gear test(): void {\n" + body + "\n}\n!train |> done;";

    #region Liaison

    [Theory]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: a + a;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: a - {x: 1.0, y: 1.0};")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: -a;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: a * 2.0;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: 2.0 * a;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: a * 2;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; bool b: a eq a; bool c: a not eq a;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; bool b: a < a; bool c: a > a; bool d: a <= a; bool e: a >= a;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec3 p: a;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; var t: a as string;")]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: a; b +<- a;")]
    public void Les_operateurs_declares_se_lient(string body)
    {
        var (program, diagnostics) = Bind(InGear(body));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void L_ordre_des_operandes_compte()
    {
        const string source = """
            mold Vec2 {
                float x;
                float y;
                Vec2 *(Vec2 v, float k) { return {x: v.x * k, y: v.y * k}; }
            }
            gear test(): void { Vec2 a: {x: 1.0, y: 2.0}; Vec2 b: 2.0 * a; }
            !train |> done;
            """;

        Assert.Equal(["E040"], Errors(source));
    }

    [Fact]
    public void Un_operateur_non_declare_reste_une_erreur()
    {
        Assert.Equal(["E040"], Errors(InGear("Vec2 a: {x: 1.0, y: 2.0}; var b: a / a;")));
    }

    [Fact]
    public void Un_mold_sans_operateur_garde_l_egalite_champ_a_champ()
    {
        const string source = "mold P { int x; }\ngear test(): void { P a: {x: 1}; bool b: a eq {x: 1}; }\n!train |> done;";

        var (program, diagnostics) = Bind(source);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void Une_conversion_declaree_l_emporte_sur_le_duck_typing()
    {
        // Vec2 a x et y comme Vec3 les demande, mais pas z : le duck typing seul refuserait ; la conversion declaree passe.
        // Ici les deux moules ont des champs communs : sans la conversion, z vaudrait son defaut.
        const string source = """
            mold Vec3 { float x; float y; float z: 5.0; }
            mold Vec2 {
                float x;
                float y;
                Vec3 implicit(Vec2 v) { return {x: v.x, y: v.y, z: 0.0}; }
            }
            gear test(): float { Vec2 a: {x: 1.0, y: 2.0}; Vec3 p: a; return p.z; }
            !train |> done;
            """;

        var (program, diagnostics) = Bind(source);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    #endregion

    #region Erreurs

    [Fact]
    public void Deux_moules_qui_surchargent_le_meme_operateur_pour_le_meme_ordre_sont_ambigus()
    {
        const string source = """
            mold A {
                int n;
                int +(A a, B b) { return a.n + b.n; }
            }
            mold B {
                int n;
                int +(A a, B b) { return a.n + b.n; }
            }
            gear test(): void { A a: {n: 1}; B b: {n: 2}; var c: a + b; }
            !train |> done;
            """;

        Assert.Equal(["E009"], Errors(source));
    }

    [Theory]
    [InlineData("bool <(P a, P b) { return a.x < b.x; }")]
    [InlineData("bool >(P a, P b) { return a.x > b.x; }")]
    public void Inferieur_et_superieur_vont_ensemble(string operatorDeclaration)
    {
        Assert.Equal(["E010"], Errors($"mold P {{ int x; {operatorDeclaration} }}\n!train |> done;"));
    }

    [Theory]
    [InlineData("int +(P a) { return a.x; }")]                          // trop peu de paramètres
    [InlineData("int +(int a, int b) { return a + b; }")]               // le mold n'est pas parmi les opérandes
    [InlineData("int eq(P a, P b) { return 1; }")]                      // eq renvoie un bool
    [InlineData("P implicit(P v) { return v; }")]                       // une conversion change de type
    [InlineData("int implicit(int v) { return v; }")]                   // la valeur convertie est du mold
    [InlineData("int +(mut P a, P b) { return 1; }")]                   // pas de mut
    [InlineData("int +(P a, P b) { return 1; } int +(P a, P b) { return 2; }")]   // doublon
    [InlineData("string implicit(P v) { return \"a\"; } string implicit(P v) { return \"b\"; }")]   // deux conversions vers le même type
    public void Un_operateur_mal_declare_est_signale(string operatorDeclaration)
    {
        Assert.Equal(["E118"], Errors($"mold P {{ int x; {operatorDeclaration} }}\n!train |> done;"));
    }

    [Fact]
    public void Un_operateur_non_pure_ne_s_appelle_pas_depuis_un_gear_pure()
    {
        const string source = """
            mold P {
                int x;
                P +(P a, P b) { return {x: a.x + b.x}; }
            }
            gear pure somme(P a): P => a + a;
            !train |> done;
            """;

        Assert.Equal(["E057"], Errors(source));
    }

    [Fact]
    public void Un_operateur_pure_s_appelle_depuis_un_gear_pure()
    {
        const string source = """
            mold P {
                int x;
                pure P +(P a, P b) { return {x: a.x + b.x}; }
            }
            gear pure somme(P a): P => a + a;
            !train |> done;
            """;

        var (program, diagnostics) = Bind(source);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    #endregion

    #region Exécution

    [Fact]
    public void Un_programme_avec_operateurs_et_conversions_compile_et_s_execute()
    {
        const string program = Vectors + """
            mold Mesure {
                int n;
                Mesure +(Mesure a, Mesure b) { return {n: a.n + b.n}; }
                Mesure +(Mesure a, int k) { return {n: a.n + k}; }
                int explicit(Mesure m) { return m.n; }
            }

            gear norme(Vec3 p): float => p.x + p.y + p.z;

            gear test(): uint {
                Vec2 a: {x: 1.0, y: 2.0};
                Vec2 b: {x: 3.0, y: 5.0};
                Vec2 c: a + b;
                Vec2 d: c - a;
                Vec2 e: -a;
                Vec2 f: 2.0 * a;
                Vec2 g: a * 3.0;
                Mesure x: {n: 4};
                Mesure m: x + {n: 5} + 1;
                Vec3 p: a;
                `{c.x} {d.y} {e.x} {f.y} {g.x} {a eq a} {a not eq b} {a < b} {a > b} {a <= a} {b >= a} {a >= b} {norme(c)} {p.z} {a as string} {m as int}` -> console;
                return 0;
            }

            !train |> $ test();
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(0, code);
        Assert.Equal("4.0 5.0 -1.0 4.0 3.0 true true true false true true false 11.0 0.0 (1.0, 2.0) 10", output);
    }

    #endregion

    #region Opérateur d'index

    private const string Entry = "\ntrain |> done;";

    [Theory]
    [InlineData("pure int [int i] { return 1; } pure int [int j] { return 2; }")]   // doublon
    [InlineData("pure int [mut int i] { return 1; }")]                         // index mut
    public void Un_index_mal_declare_est_une_erreur(string member)
    {
        Assert.Contains("E118", Errors("mold P { [int] d; " + member + " }" + Entry));
    }

    [Fact]
    public void Lire_un_mold_sans_lecture_d_index_est_une_erreur()
    {
        Assert.Contains("E040", Errors("mold P { [int] d; mut int [int i] { this.d[i] <- value; } }\ngear f(P p): int => p[0];" + Entry));
    }

    [Fact]
    public void Ecrire_dans_un_index_demande_une_variable_mut()
    {
        Assert.Contains("E102", Errors("mold P { [int] d; mut int [int i] { this.d[i] <- value; } }\ngear f(): void { P p: {d: [1]}; p[0] <- 2; }" + Entry));
    }

    #endregion
}
