using Rgl.Tests.Runtime;

namespace Rgl.Tests.Emit;

/// <summary>Les copies évitées : l'accumulateur d'une accumulation est déplacé dans l'étape qui le consomme (slice B.4b).</summary>
public class MoveTests
{
    private const string Collect = """
        emit run(): uint {
            [int] values: [1, 2, 3, 4, 5, 6];
            [int] none: [];
            const [int] evens: values |+> none with ([int] acc, int v) => {
                if (v % 2 eq 1) { return acc; }
                return [...acc, v];
            };
            return evens.length + (evens[0] as uint) * 10;
        }

        !train |> run;
        """;

    [Fact]
    public void Un_accumulateur_qui_possede_de_la_memoire_est_recu_par_valeur_et_deplace()
    {
        var cpp = CppEmitterTests.Emit(Collect);

        Assert.Contains("std::move(", cpp);
        Assert.DoesNotContain("const rgl::standard::array<std::int32_t>& rgl_p_acc", cpp);
    }

    [ZigFact]
    public void Le_resultat_est_le_meme_qu_avec_une_copie()
    {
        Assert.Equal(3 + 20, CppEmitterTests.CompileAndRun(Collect).Code);
    }

    [Fact]
    public void Un_accumulateur_qui_ne_possede_rien_reste_une_valeur_simple()
    {
        var cpp = CppEmitterTests.Emit("emit run(): uint {\n    const int total: [1, 2, 3] |+> 0 with (int acc, int v) => acc + v;\n    return total as uint;\n}\n!train |> run;");

        Assert.DoesNotContain("std::move(", cpp);
    }
}
