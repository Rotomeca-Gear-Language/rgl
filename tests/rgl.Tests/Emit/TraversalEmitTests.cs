using Rgl.Tests.Runtime;

namespace Rgl.Tests.Emit;

/// <summary>Parcours : rang de l'élément, arrêt anticipé d'une accumulation par <c>done valeur;</c>, modification sur place de ce que capture la lambda d'une étape.</summary>
public sealed class TraversalEmitTests
{
    private const string Program = """
        emit afficher(): uint {
            [int] doubles: [10, 20, 30] ||> (x, i) => x + i;
            int pondere: [5, 5, 5] |+> 0 with (acc, x, i) => acc + x * i;

            mut [int] visites: [0];
            bool trouve: [1, 2, 7, 8, 9] |+> false with (acc, x) => {
                visites[0] <- visites[0] + 1;
                if (x > 5) { done true; }
                return acc;
            };

            mut [int] v: [1, 2, 3, 4, 5];
            [0..<2] ||> (i) => {
                int garde: v[i];
                v[i] <- v[-1 - i];
                v[-1 - i] <- garde;
            };

            `{doubles} {pondere} {trouve} {visites[0]} {v}` -> console;
            return 0;
        }

        !train |> afficher;
        """;

    [ZigFact]
    public void Un_parcours_donne_le_rang_arrete_l_accumulation_et_modifie_ce_qu_il_capture()
    {
        var result = CppEmitterTests.CompileAndRun(Program);

        Assert.Equal((0, "[10, 21, 32] 15 true 3 [5, 4, 3, 2, 1]"), (result.Code, result.Output));
    }
}
