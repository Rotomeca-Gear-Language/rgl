using Rgl.Tests.Runtime;
using Xunit;

namespace Rgl.Tests.Emit;

/// <summary>L'opérateur d'index d'un mold : lecture <c>pure T [P i]</c> et écriture <c>mut T [P i]</c>, avec l'ingot <c>index</c>.</summary>
public sealed class IndexerEmitTests
{
    private const string Pile = """
        ingot index: long;

        mold Pile {
            [int] data;

            pure int [index i] { return this.data[i]; }
            mut int [int i] { this.data[i] <- value; }
        }

        emit afficher(): uint {
            mut Pile p: {data: [1, 2, 3]};
            p[1] <- 20;
            p[-1] <- p[0] + 40;
            `{p[0]} {p[1]} {p[2]} {p[-2]}` -> console;
            return 0;
        }

        !train |> afficher;
        """;

    [ZigFact]
    public void L_index_d_un_mold_se_lit_et_s_ecrit()
    {
        var result = CppEmitterTests.CompileAndRun(Pile);

        Assert.Equal((0, "1 20 41 20"), (result.Code, result.Output));
    }
}
