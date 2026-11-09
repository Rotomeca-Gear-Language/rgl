using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Le mode sûr : un train ne cache aucune erreur. Ce qu'il refuse, le mode libre l'accepte en silence.</summary>
public class SafeModeTests
{
    /// <summary>Un guard qui peut échouer, un emit qui termine un rail, un gear qui garde l'erreur reçue (4 lignes + 1 par déclaration).</summary>
    private const string Prelude =
        "guard charger(int n): int {\n    if (n < 0) {\n        fail;\n    }\n    done;\n}\n" +
        "emit signaler(Exception e): uint {\n    return 2;\n}\n" +
        "emit ignorer(Exception e): void {\n}\n" +
        "gear garder(Exception e): Exception => e;\n" +
        "gear reparer(Exception e): int => 0;\n" +
        "guard ecrire(Exception e): Exception {\n    fail;\n}\n";

    private static string[] Errors(string train, bool safe)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", Prelude + train), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => $"{d.Code} {d.Message}")));

        new Binder(diagnostics, safe: safe).Bind(unit);
        return [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "-")];
    }

    [Theory]
    [InlineData("train 1 |> charger |> done;")]
    [InlineData("train 1 |> charger !> [ecrire |> signaler] |> done;")]
    [InlineData("train [1, 2] ||> charger |> done;")]
    [InlineData("train 1 |> ^1 charger !> [reparer |> ^1<3> |> done] |> done;")]
    public void Le_mode_libre_accepte_ce_que_le_mode_sur_refuse(string train)
    {
        Assert.Empty(Errors(train, safe: false));
    }

    [Fact]
    public void Une_erreur_sans_rail_est_refusee()
    {
        Assert.Equal([DiagnosticCodes.UnhandledError], Errors("train 1 |> charger |> done;", safe: true));
    }

    [Fact]
    public void Un_rail_qui_termine_traite_l_erreur()
    {
        Assert.Empty(Errors("train 1 |> charger !> [signaler] |> done;", safe: true));
    }

    [Fact]
    public void Un_fail_explicite_dans_un_rail_est_une_fin_legitime()
    {
        Assert.Empty(Errors("train 1 |> charger !> [garder |> fail] |> done;", safe: true));
    }

    [Fact]
    public void Le_guard_d_un_rail_porte_son_propre_rail()
    {
        Assert.Equal([DiagnosticCodes.UnhandledError], Errors("train 1 |> charger !> [ecrire |> signaler] |> done;", safe: true));
        Assert.Empty(Errors("train 1 |> charger !> [ecrire !> [signaler] |> signaler] |> done;", safe: true));
    }

    [Fact]
    public void Un_parcours_qui_ecarte_ses_erreurs_en_silence_est_refuse()
    {
        Assert.Equal([DiagnosticCodes.UnhandledError], Errors("train [1, 2] ||> charger |> done;", safe: true));
        Assert.Empty(Errors("train [1, 2] ||> charger !> [ignorer] |> done;", safe: true));
    }

    [Fact]
    public void Une_reprise_bornee_dans_un_rail_doit_echouer_a_l_epuisement()
    {
        Assert.Equal([DiagnosticCodes.SilentResumeInRail], Errors("train 1 |> ^1 charger !> [reparer |> ^1<3> |> done] |> done;", safe: true));
        Assert.Empty(Errors("train 1 |> ^1 charger !> [reparer |> ^1<!3>] |> done;", safe: true));
    }

    private static string[] Warnings(string train, string prelude = Prelude)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", prelude + train), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => $"{d.Code} {d.Message}")));

        new Binder(diagnostics, safe: true).Bind(unit);
        return [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Warning).Select(d => d.Code ?? "-")];
    }

    [Fact]
    public void Un_handler_qui_ne_lit_pas_l_erreur_et_finit_en_succes_avertit()
    {
        Assert.Contains(DiagnosticCodes.ErrorIgnored, Warnings("train 1 |> charger !> [ignorer] |> done;"));
        Assert.Contains(DiagnosticCodes.ErrorIgnored, Warnings("train 1 |> charger !> [done] |> done;"));
    }

    [Fact]
    public void Un_handler_se_tait_en_lisant_l_erreur_en_la_faisant_echouer_ou_par_ignoreError()
    {
        const string reads = "emit lire(Exception e): void {\n    `{e}` -> console;\n}\n";
        const string marked = "[ignoreError]\nemit avaler(Exception e): void {\n}\n";

        Assert.DoesNotContain(DiagnosticCodes.ErrorIgnored, Warnings("train 1 |> charger !> [lire] |> done;", Prelude + reads));
        Assert.DoesNotContain(DiagnosticCodes.ErrorIgnored, Warnings("train 1 |> charger !> [garder |> fail] |> done;"));
        Assert.DoesNotContain(DiagnosticCodes.ErrorIgnored, Warnings("train 1 |> charger !> [avaler] |> done;", Prelude + marked));
    }

    [Fact]
    public void Le_mode_libre_n_avertit_pas_d_une_erreur_avalee()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", Prelude + "train 1 |> charger !> [ignorer] |> done;"), diagnostics).Tokenize();
        new Binder(diagnostics).Bind(new Parser(tokens, diagnostics).Parse());

        Assert.DoesNotContain(diagnostics.Items, d => d.Code == DiagnosticCodes.ErrorIgnored);
    }

    [Fact]
    public void Le_resultat_ignore_d_un_await_est_une_erreur_en_mode_sur_et_un_avertissement_en_mode_libre()
    {
        const string program = "@async\ngear g(): int {\n    return 1;\n}\n@async\ngear h(): void {\n    await g();\n}\n!train |> done;";

        foreach (var safe in new[] { true, false })
        {
            var diagnostics = new DiagnosticBag();
            var tokens = new Lexer(new SourceText("main.rgl", program), diagnostics).Tokenize();
            new Binder(diagnostics, safe: safe).Bind(new Parser(tokens, diagnostics).Parse());

            if (safe) Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.IgnoredResult && d.Level == DiagnosticLevel.Error);
            else Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.AwaitResultIgnored && d.Level == DiagnosticLevel.Warning);
        }
    }

    [Fact]
    public void Une_erreur_ne_contient_pas_de_valeur_a_cycle_de_vie()
    {
        const string program = "mold Handle -> fermer { int id; }\ngear fermer(Handle h): void {\n}\nerror Boom { Handle h; }\n!train |> done;";

        Assert.Equal([DiagnosticCodes.LifecycleUnsupported], Errors(program, safe: false));
        Assert.Equal([DiagnosticCodes.LifecycleUnsupported], Errors(program, safe: true));
    }

    private static string[] LoopErrors(string content, bool boundedLoops)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", Prelude + content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => $"{d.Code} {d.Message}")));

        new Binder(diagnostics, boundedLoops: boundedLoops).Bind(unit);
        return [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "-")];
    }

    [Theory]
    [InlineData("train 1 |> charger* |> done;")]
    [InlineData("train 1 |> ^1 charger !> [reparer |> ^1] |> done;")]
    [InlineData("""
        gear f(int n): int {
            switch (n) {
                case 1:
                    goto 2;
                case 2:
                    return 2;
                default:
                    return 0;
            }
        }
        !train 1 |> done;
        """)]
    public void Une_boucle_sans_limite_et_goto_sont_refuses_avec_loop_safe(string content)
    {
        Assert.Empty(LoopErrors(content, boundedLoops: false));
        Assert.Equal([DiagnosticCodes.UnboundedLoop], LoopErrors(content, boundedLoops: true));
    }

    [Theory]
    [InlineData("train 1 |> charger*<3> |> done;")]
    [InlineData("train 1 |> charger*<!3> !> [signaler] |> done;")]
    [InlineData("train 1 |> ^1 charger !> [reparer |> ^1<!3>] |> done;")]
    public void Une_boucle_bornee_passe_avec_loop_safe(string content)
    {
        Assert.Empty(LoopErrors(content, boundedLoops: true));
    }

    private static IReadOnlyList<Diagnostic> All(string content, bool safe)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => $"{d.Code} {d.Message}")));

        new Binder(diagnostics, safe: safe).Bind(unit);
        return diagnostics.Items;
    }

    [Fact]
    public void Un_fail_seul_envoie_l_erreur_recue_et_un_fail_avec_texte_la_perd()
    {
        Assert.DoesNotContain(All(Prelude + "train 1 |> charger !> [fail] |> done;", safe: true), d => d.Code == DiagnosticCodes.ErrorLost);
        Assert.DoesNotContain(All(Prelude + "train 1 |> charger !> [garder |> fail] |> done;", safe: true), d => d.Code == DiagnosticCodes.ErrorLost);
        Assert.Contains(All(Prelude + "train 1 |> charger !> [fail \"autre\"] |> done;", safe: true), d => d.Code == DiagnosticCodes.ErrorLost);
    }

    [Fact]
    public void Un_pas_qui_atteint_un_fire_est_refuse()
    {
        const string fire = """
            @fire
            gear journaliser(int n): void {
                `{n}` -> console;
            }
            guard g(int n): int {
                journaliser(n);
                done;
            }

            """;

        Assert.Contains(All(fire + "train 1 |> g !> [done] |> done;", safe: true), d => d.Code == DiagnosticCodes.FireInSafeTrain);
        Assert.DoesNotContain(All(fire + "train 1 |> g !> [done] |> done;", safe: false), d => d.Code == DiagnosticCodes.FireInSafeTrain);
    }

    [Fact]
    public void Le_mode_sur_accepte_donne_un_certificat()
    {
        var certificate = Assert.Single(All(Prelude + "train 1 |> charger !> [garder |> fail] |> done;", safe: true), d => d.Code == DiagnosticCodes.SafeCertificate);
        Assert.Contains("1 train(s) et 1 rail(s)", certificate.Message);
        Assert.Contains("aucun rail n'avale", certificate.Message);

        var swallowing = Assert.Single(All(Prelude + "train 1 |> charger !> [ignorer] |> done;", safe: true), d => d.Code == DiagnosticCodes.SafeCertificate);
        Assert.Contains("1 rail(s) n'ont pas lu", swallowing.Message);
    }
}
