using Rgl.Tests.Runtime;

namespace Rgl.Tests.Emit;

/// <summary>Les grosses constantes de fichier s'écrivent comme des tables constexpr, copiées au démarrage (slice B.6).</summary>
public class TableTests
{
    private static string Numbers(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => (i * 7 % 101).ToString()));

    private static string IntegerKeys(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"{(count - i) * 3 - 100}: {i}"));

    private static string TextKeys(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"'{(i % 2 == 0 ? "é" : "z")}{i:D4}': {i}"));

    [Fact]
    public void Un_grand_tableau_constant_est_une_table_constexpr()
    {
        var cpp = CppEmitterTests.Emit($"const [int] t: [{Numbers(100)}];\nemit run(): uint => t[5] as uint;\n!train |> run;");

        Assert.Contains("static constexpr rgl::standard::fixed_array<std::int32_t, 100> rgl_d", cpp);
        Assert.Contains("::from(rgl_d", cpp);
    }

    [Fact]
    public void Un_petit_tableau_reste_ecrit_tel_quel()
    {
        var cpp = CppEmitterTests.Emit($"const [int] t: [{Numbers(10)}];\nemit run(): uint => t[5] as uint;\n!train |> run;");

        Assert.DoesNotContain("rgl_d", cpp);
    }

    [Fact]
    public void Un_grand_dictionnaire_constant_est_range_par_le_compilateur()
    {
        var cpp = CppEmitterTests.Emit($"const [int:int] d: [{IntegerKeys(100)}];\nemit run(): uint => d[-97] as uint;\n!train |> run;");

        Assert.Contains("std::pair<std::int32_t, std::int32_t> rgl_d", cpp);
        Assert.Contains("::from_sorted(rgl_d", cpp);
    }

    [ZigFact]
    public void Un_tableau_garde_l_ordre_et_les_valeurs_de_ses_elements()
    {
        var values = Enumerable.Range(0, 100).Select(i => i * 7 % 101).ToArray();
        var code = CppEmitterTests.CompileAndRun($"const [int] t: [{Numbers(100)}];\nemit run(): uint => (t[0] + t[1] + t[50] + t[99] + t.length) as uint;\n!train |> run;").Code;

        Assert.Equal(values[0] + values[1] + values[50] + values[99] + 100, code);
    }

    [ZigFact]
    public void Un_dictionnaire_a_cles_entieres_negatives_se_lit_par_cle()
    {
        // Les clés vont de -97 (i = 99) à 200 (i = 0) en désordre croissant d'écriture : lire quelques-unes, présentes et absentes.
        var code = CppEmitterTests.CompileAndRun($$"""
            const [int:int] d: [{{IntegerKeys(100)}}];
            emit run(): uint {
                return (d[-97] * 1000 + d[200] * 100 + (d.has(-98) ? 1 : 0) + d.length) as uint;
            }
            !train |> run;
            """).Code;

        // -97 → i = 99 ; 200 → i = 0 ; -98 absent ; longueur 100.
        Assert.Equal(99 * 1000 + 0 * 100 + 0 + 100, code);
    }

    [ZigFact]
    public void Un_dictionnaire_a_cles_textes_est_range_par_octets_UTF8()
    {
        // 'é' (U+00E9, octets C3 A9) passe après 'z' (7A) : l'ordre est celui des octets, pas celui des lettres.
        var output = CppEmitterTests.CompileAndRun($$"""
            const [string:int] d: [{{TextKeys(100)}}];
            emit run(): uint {
                `{d['z0001']} {d['é0000']} {d['é0098']} {d.length}` -> console;
                return 0;
            }
            !train |> run;
            """);

        Assert.Equal((0, "1 0 98 100"), (output.Code, output.Output.Replace("\r\n", "\n")));
    }

    [ZigFact]
    public void Un_dataset_de_cent_entrees_se_lit_par_identifiant()
    {
        var entries = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"    c{i}: {{nom: 'Carte {i}', puissance: {i * 3 % 17}}},"));
        var code = CppEmitterTests.CompileAndRun($$"""
            mold Carte { string nom; int puissance; }
            dataset Cartes of Carte {
            {{entries}}
            }
            emit run(): uint {
                const Carte a: Cartes.c42.get();
                return (a.puissance * 1000 + Cartes.length) as uint;
            }
            !train |> run;
            """).Code;

        Assert.Equal(42 * 3 % 17 * 1000 + 100, code);
    }
}
