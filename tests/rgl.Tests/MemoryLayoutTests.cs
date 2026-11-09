using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>La disposition mémoire d'un mold : ordre physique par alignement, ordre logique conservé, <c>static_assert</c> (slice B.2).</summary>
public class MemoryLayoutTests
{
    private const string Mixed = """
        mold Mix { bool a; long b; bool c; int d; string nom; }

        emit run(): uint {
            var m: new Mix {a: true, b: 7, c: false, d: 3, nom: "x"};
            `{m} {m.a} {m.b + m.d}` -> console;
            return 0;
        }

        !train |> run;
        """;

    [Fact]
    public void Les_champs_d_un_struct_viennent_par_alignement_decroissant_a_alignement_egal_dans_l_ordre_du_mold()
    {
        var cpp = CppEmitterTests.Emit(Mixed);

        var struct_ = cpp[cpp.IndexOf("struct rgl_m_Mix {", StringComparison.Ordinal)..];
        var order = new[] { "rgl_f_b;", "rgl_f_nom;", "rgl_f_d;", "rgl_f_a;", "rgl_f_c;" }.Select(f => struct_.IndexOf(f, StringComparison.Ordinal)).ToArray();
        Assert.All(order, position => Assert.True(position > 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void Un_mold_reordonne_a_un_constructeur_dans_l_ordre_du_mold_et_un_mold_deja_range_reste_un_agregat()
    {
        var reordered = CppEmitterTests.Emit(Mixed);
        Assert.Contains("constexpr rgl_m_Mix(bool rgl_a0, std::int64_t rgl_a1, bool rgl_a2, std::int32_t rgl_a3, rgl::standard::string rgl_a4)", reordered);

        var ordered = CppEmitterTests.Emit("mold Ordonne { long a; int b; bool c; }\nemit run(): uint {\nvar m: new Ordonne {a: 1, b: 2, c: true};\n`{m}` -> console;\nreturn 0;\n}\n!train |> run;");
        Assert.DoesNotContain("constexpr rgl_m_Ordonne(", ordered);
    }

    [Fact]
    public void La_disposition_est_verifiee_par_des_static_assert()
    {
        var cpp = CppEmitterTests.Emit(Mixed);

        Assert.Contains("static_assert(offsetof(rgl_m_Mix, rgl_f_b) == 0", cpp);
        Assert.Contains("static_assert(alignof(decltype(rgl_m_Mix::rgl_f_b)) >= alignof(decltype(rgl_m_Mix::rgl_f_nom))", cpp);
        Assert.Contains("static_assert(sizeof(rgl_m_Mix) ==", cpp);
    }

    [ZigFact]
    public void Un_mold_reordonne_garde_son_texte_ses_valeurs_et_ses_acces_dans_l_ordre_du_mold()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(Mixed);

        Assert.Equal((0, "{a: true, b: 7, c: false, d: 3, nom: \"x\"} true 10"), (code, output));
    }

    [ZigFact]
    public void Un_mold_a_cycle_de_vie_reordonne_se_ferme_une_fois()
    {
        var (code, output) = CppEmitterTests.CompileAndRun("""
            mold Poignee -> fermer { bool ouverte; long id; bool autre; }

            gear fermer(Poignee p): void {
                `ferme {p.id}` -> console;
            }

            emit run(): uint {
                var p: new Poignee {ouverte: true, id: 42, autre: false};
                `{p.ouverte} ` -> console;
                return 0;
            }

            !train |> run;
            """);

        Assert.Equal((0, "true ferme 42"), (code, output));
    }

    private const string Niches = """
        enum Couleur { Rouge, Vert, Bleu }
        enum Duo { A, B }
        mold Boite { bool? actif; Couleur? couleur; int<0..100>? score; Duo? duo; int? libre; string? nom; long taille; }

        gear pure lire(bool? b): string => b ?? false ? "oui" : "non";

        emit run(): uint {
            var b: new Boite {actif: true, couleur: Couleur.Bleu, score: 100, duo: Duo.B, libre: 5, nom: "n", taille: 9};
            var v: new Boite {taille: 1};
            bool? flag: null;
            flag <- false;
            `{b} | {v}` -> console;
            `|{lire(b.actif)}|{lire(v.actif)}|{lire(flag)}|{b.couleur ?? Couleur.Rouge}|{v.score ?? 7}|{b.score eq 100}|{v.duo eq null}|{v.libre ?? 3}` -> console;
            return 0;
        }

        !train |> run;
        """;

    [Fact]
    public void Un_nullable_dont_une_valeur_n_est_jamais_utilisee_la_prend_pour_null()
    {
        var cpp = CppEmitterTests.Emit(Niches);

        Assert.Contains("rgl::standard::niche_nullable<bool, 2>", cpp);
        Assert.Contains("rgl::standard::niche_nullable<rgl_e_Couleur, 255>", cpp);
        Assert.Contains("rgl::standard::niche_nullable<rgl_e_Duo, 2>", cpp);
        Assert.Contains("rgl::standard::niche_nullable<std::int32_t, 101>", cpp);   // int<0..100>? : 101 n'est pas une valeur
        // Un entier ou un texte sans niche garde l'indicateur.
        Assert.Contains("rgl::standard::nullable<std::int32_t> rgl_f_libre", cpp);
        Assert.Contains("rgl::standard::nullable<rgl::standard::string> rgl_f_nom", cpp);
    }

    [ZigFact]
    public void Un_nullable_a_niche_se_lit_s_ecrit_et_se_compare_comme_un_autre()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(Niches);

        Assert.Equal((0, "{actif: true, couleur: Bleu, score: 100, duo: B, libre: 5, nom: \"n\", taille: 9} | {actif: null, couleur: null, score: null, duo: null, libre: null, nom: null, taille: 1}"
            + "|oui|non|non|Bleu|7|true|true|3"), (code, output));
    }
}
