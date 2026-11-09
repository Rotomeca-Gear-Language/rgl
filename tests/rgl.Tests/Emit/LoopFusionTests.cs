using Rgl.Tests.Runtime;

namespace Rgl.Tests.Emit;

/// <summary>Deux parcours qui se suivent, le second sur le tableau du premier, ne font qu'une boucle (slice B.4e).</summary>
public class LoopFusionTests
{
    private const string Header = """
        gear pure f(int i): int => (i * 7 + 3) % 1000;
        gear pure g(int x): int => x * 2 + 1;
        gear pure h(int x): int => x + 5;
        gear pure add(long acc, int x): long => acc + x;
        gear note(int x): int {
            `{x}` -> console;
            return x;
        }

        """;

    private static string Program(string body) => Header + "emit run(): uint {\n" + body + "\n}\n\n!train |> run;";

    private static int Loops(string cpp) => cpp.Split("for (std::size_t").Length - 1;

    [Fact]
    public void Deux_declarations_qui_se_suivent_font_une_seule_boucle()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] a: [0..<10] ||> f;
            [int] b: a ||> g;
            long s: b |+> 0 with add;
            return (s % 100) as uint;
            """));

        // a ||> g et b |+> add se fondent aussi : trois étapes, une boucle.
        Assert.Equal(1, Loops(cpp));
    }

    [Fact]
    public void Une_variable_relue_plus_loin_garde_ses_deux_boucles()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] a: [0..<10] ||> f;
            [int] b: a ||> g;
            return (a.length + b.length) as uint;
            """));

        Assert.Equal(2, Loops(cpp));
    }

    [Fact]
    public void Un_parcours_non_adjacent_n_est_pas_fondu()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] a: [0..<10] ||> f;
            int k: 3;
            [int] b: a ||> g;
            return (b.length + k) as uint;
            """));

        Assert.Equal(2, Loops(cpp));
    }

    [Fact]
    public void Une_etape_avec_effet_garde_l_ordre_des_deux_parcours()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] a: [0..<3] ||> note;
            [int] b: a ||> g;
            return b.length as uint;
            """));

        Assert.Equal(2, Loops(cpp));
    }

    [Fact]
    public void Une_variable_mut_n_est_pas_fondue()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            mut [int] a: [0..<10] ||> f;
            [int] b: a ||> g;
            return b.length as uint;
            """));

        Assert.Equal(2, Loops(cpp));
    }

    [ZigFact]
    public void Le_resultat_est_celui_de_deux_boucles()
    {
        // f(i) = (7i + 3) % 1000 pour i de 0 à 9 : 3, 10, 17, 24, 31, 38, 45, 52, 59, 66 ; g : 2x + 1 ; h : x + 5 ; somme = 2 * 345 + 10 + 50 = 750.
        Assert.Equal(750, CppEmitterTests.CompileAndRun(Program("""
            [int] a: [0..<10] ||> f;
            [int] b: a ||> g;
            [int] c: b ||> h;
            long s: c |+> 0 with add;
            return s as uint;
            """)).Code);
    }

    [ZigFact]
    public void Un_ajout_dans_un_gear_et_une_lambda_donne_le_meme_resultat()
    {
        Assert.Equal(15, CppEmitterTests.CompileAndRun(Program("""
            const pure (int) => int double: (int x) => x * 2;
            [int] a: [1, 2, 3] ||> double;
            [int] b: a ||> (x) => x + 1;
            long s: b |+> 0 with (acc, x) => acc + x;
            return s as uint;
            """)).Code);
    }
}
