using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary><c>[mold;N]</c> en colonnes rangées dans l'objet (<c>soa_fixed_array</c>) et AoSoA <c>[[mold;N]]</c> (slice B.1).</summary>
public class SoaFixedTests
{
    private const string Program = """
        mold P { int x; int y; int z; }

        const [P;2] FIXE: [{x: 5, y: 6, z: 0}, {x: 7, y: 8, z: 0}];

        gear pure somme(P p): int => p.x + p.y;

        emit run(): uint {
            [P;2] bloc: [{x: 1, y: 2, z: 9}, {x: 3, y: 4, z: 9}];
            mut [P;2] bloc2: [{x: 1, y: 2, z: 9}, {x: 3, y: 4, z: 9}];
            bloc2[1].x <- 10;
            var sommes: bloc ||> somme;
            [[P;2]] blocs: [bloc, bloc2];
            [P] dynamique: [{x: 1, y: 1, z: 1}, {x: 2, y: 2, z: 2}];
            [P;2]? converti: dynamique as? [P;2];
            [P;2]? refuse: [...dynamique, {x: 3, y: 3, z: 3}] as? [P;2];
            `{sommes} {bloc2[1].x} {blocs.length} {blocs[1][1].y} {bloc2 eq bloc2} {FIXE[1].x} {converti eq null} {refuse eq null}` -> console;
            return 0;
        }

        !train |> run;
        """;

    [Fact]
    public void Un_tableau_fixe_de_molds_est_un_bloc_de_colonnes_et_un_tableau_de_blocs_est_un_aosoa()
    {
        var cpp = CppEmitterTests.Emit(Program);

        Assert.Contains("rgl::standard::soa_fixed_array<rgl_l_m_P, 2>", cpp);
        Assert.Contains("rgl::standard::array<rgl::standard::soa_fixed_array<rgl_l_m_P, 2>>", cpp);
        Assert.Contains("template <std::size_t N> struct fixed_columns", cpp);
    }

    [ZigFact]
    public void Un_bloc_de_colonnes_se_lit_s_ecrit_et_se_convertit_comme_un_tableau_fixe()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(Program);

        Assert.Equal((0, "[3, 7] 10 2 4 true 7 false true"), (code, output));
    }
}
