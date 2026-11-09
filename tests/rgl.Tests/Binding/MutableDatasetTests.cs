using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

/// <summary>Une table mutable de dataset : une copie où l'on ajoute des entrées, aux identifiants positionnels (slice B.6).</summary>
public class MutableDatasetTests
{
    private const string Moves = """
        mold Move { string name; int power; }
        dataset Moves of Move {
            attaquer: {name: 'Attaquer', power: 5},
            soin: {name: 'Soin', power: -20},
        }

        """;

    private static (int Code, string Output) Run(string body) =>
        CppEmitterTests.CompileAndRun(Moves + "emit fin(): uint {\n" + body + "\nreturn 0;\n}\n!train |> fin;");

    [ZigFact]
    public void Une_table_mutable_garde_les_entrees_du_dataset_et_recoit_les_suivantes()
    {
        var (code, output) = Run("""
            [Move] t: Moves.table();
            t.push({name: 'Braise', power: 9});
            const Moves.id braise: Moves.at(t.length - 1);
            const Moves.id soin: Moves.soin;
            const Move? lue: braise.get(t);
            const Move? ancienne: soin.get(t);
            `{t.length} {braise°} {(lue ?? {name: '?', power: 0}).name} {(ancienne ?? {name: '?', power: 0}).power}\n` -> console;
            """);

        Assert.Equal((0, "3 2 Braise -20\n"), (code, output));
    }

    [ZigFact]
    public void Un_identifiant_hors_de_la_table_donne_null()
    {
        var (code, output) = Run("""
            const [Move] t: Moves.table();
            const Moves.id loin: Moves.at(7);
            const Move? rien: loin.get(t);
            `{rien eq null} {Moves.attaquer.get().name}\n` -> console;
            """);

        Assert.Equal((0, "true Attaquer\n"), (code, output));
    }

    [ZigFact]
    public void La_table_du_dataset_n_est_pas_modifiee_par_les_ajouts_a_sa_copie()
    {
        var (code, output) = Run("""
            [Move] t: Moves.table();
            t.push({name: 'Braise', power: 9});
            `{Moves.length} {t.length}\n` -> console;
            """);

        Assert.Equal((0, "2 3\n"), (code, output));
    }
}
