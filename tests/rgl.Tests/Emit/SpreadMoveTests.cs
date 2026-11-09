using Rgl.Tests.Runtime;

namespace Rgl.Tests.Emit;

/// <summary>[...a, x] reprend le bloc de a quand a n'est plus lu ensuite : une boucle d'ajouts est linéaire (slice B.4c).</summary>
public class SpreadMoveTests
{
    private static string Program(string body) => "emit run(): uint {\n" + body + "\n}\n\n!train |> run;";

    [Fact]
    public void Une_variable_etalee_a_son_dernier_usage_est_reprise()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] a: [1, 2];
            [int] b: [...a, 3];
            return b.length as uint;
            """));

        Assert.Contains("make_array_from<std::int32_t>(std::move(", cpp);
    }

    [Fact]
    public void Une_variable_relue_apres_n_est_pas_reprise()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] a: [1, 2];
            [int] b: [...a, 3];
            return (b.length + a.length) as uint;
            """));

        Assert.DoesNotContain("make_array_from", cpp);
    }

    [Fact]
    public void Un_morceau_qui_lit_le_meme_tableau_empeche_la_reprise()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] a: [1, 2];
            [int] b: [...a, a[0]];
            return b.length as uint;
            """));

        Assert.DoesNotContain("make_array_from", cpp);
    }

    [Fact]
    public void Dans_une_accumulation_l_accumulateur_possede_son_tableau_et_est_repris()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            [int] none: [];
            const [int] all: [1, 2, 3] |+> none with ([int] acc, int v) => [...acc, v * 2];
            return all.length as uint;
            """));

        Assert.Contains("make_array_from", cpp);
    }

    [Fact]
    public void Le_champ_d_un_tuple_est_repris_si_un_autre_champ_est_lu_ensuite()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            ([int], int) none: ([], 0);
            const ([int], int) end: [1, 2, 3] |+> none with (state, v) => ([...state.Item1, v], state.Item2 + 1);
            return end.Item1.length as uint;
            """));

        Assert.Contains("make_array_from", cpp);
    }

    [Fact]
    public void Le_champ_relu_apres_n_est_pas_repris()
    {
        var cpp = CppEmitterTests.Emit(Program("""
            ([int], int) none: ([], 0);
            const ([int], int) end: [1, 2, 3] |+> none with (state, v) => ([...state.Item1, v], state.Item1.length as int);
            return end.Item1.length as uint;
            """));

        Assert.DoesNotContain("make_array_from", cpp);
    }

    [ZigFact]
    public void Les_resultats_sont_ceux_d_une_copie()
    {
        // 3 éléments ajoutés à chaque tour de [1, 2, 3, 4] : 12, puis le compte du tuple (4) : 12 * 10 + 4.
        Assert.Equal(12 * 10 + 4, CppEmitterTests.CompileAndRun(Program("""
            ([int], int) none: ([], 0);
            const ([int], int) end: [1, 2, 3, 4] |+> none with (state, v) => ([...state.Item1, v, v + 1, v + 2], state.Item2 + 1);
            return (end.Item1.length * 10 + end.Item2) as uint;
            """)).Code);
    }

    [ZigFact]
    public void Un_tableau_repris_n_est_pas_modifie_pour_les_autres_variables()
    {
        // b reprend le bloc de a : a n'est plus lu. c est une copie d'avant : elle garde ses éléments.
        Assert.Equal(2 * 100 + 3, CppEmitterTests.CompileAndRun(Program("""
            [int] a: [1, 2];
            [int] c: a;
            [int] b: [...a, 3];
            return (c.length * 100 + b.length) as uint;
            """)).Code);
    }

    [ZigFact]
    public void Un_ajout_dans_une_boucle_de_20000_elements_reste_rapide()
    {
        var run = CppEmitterTests.CompileAndRun(Program("""
            [int] none: [];
            const [int] all: [0..<20000] |+> none with ([int] acc, int v) => [...acc, v];
            return (all.length / 1000) as uint;
            """));

        Assert.Equal(20, run.Code);
    }

    [ZigFact]
    public void Un_deuxieme_tableau_etale_est_copie_et_le_premier_repris()
    {
        Assert.Equal(5, CppEmitterTests.CompileAndRun(Program("""
            [int] a: [1, 2];
            [int] b: [7, 8, 9];
            [int] c: [...a, ...b, 4];
            return (c.length - 1) as uint;
            """)).Code);
    }
}
