using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Les positions des opérations vérifiées : une constante de 32 bits au lieu d'un texte par opération (slice B.3).</summary>
public class CompactPositionTests
{
    private const string Overflowing = """
        gear pure grand(int n): int => n * 100000;

        emit run(): uint {
            var x: grand(3) + grand(100000);
            `{x}` -> console;
            return 0;
        }

        !train |> run;
        """;

    [Fact]
    public void Une_operation_verifiee_recoit_sa_position_en_constante_de_32_bits_et_le_programme_definit_les_fichiers()
    {
        var cpp = CppEmitterTests.Emit(Overflowing);

        // Ligne 1 de main.rgl : fichier 0 (6 bits), ligne 1 (18 bits), colonne de l'opérateur (8 bits).
        var column = "gear pure grand(int n): int => n * 100000;".IndexOf('*') + 1;
        Assert.Contains($"checked_mul<std::int32_t>(rgl_p_n, 100000, {(1 << 8) | column}u)", cpp);
        Assert.Contains("namespace rgl::standard { const char* const position_files[] = {\"main.rgl\", nullptr}; }", cpp);
        Assert.DoesNotContain("\"main.rgl:1:", cpp);
    }

    [Fact]
    public void Un_programme_sans_operation_verifiee_n_ecrit_pas_la_table_des_fichiers()
    {
        var cpp = CppEmitterTests.Emit("emit run(): uint {\nreturn 0;\n}\n!train |> run;");

        Assert.DoesNotContain("position_files", cpp);
    }

    [ZigFact]
    public void Le_debordement_arrete_le_programme_avec_la_position_decodee_du_source()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(Overflowing);

        Assert.NotEqual(0, code);
        Assert.Contains("débordement d'entier (main.rgl:1:", output);
    }
}
