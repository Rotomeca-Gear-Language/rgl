using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>
/// Tests du cycle de vie (slice A1.1) : <c>mold Handle -&gt; fermer { … }</c>. Une valeur n'a qu'un propriétaire, elle se déplace et ne se copie pas ;
/// son gear de fin, qui ne peut pas échouer, s'appelle une fois quand elle sort de portée, aussi quand un guard échoue.
/// </summary>
public class LifecycleTests
{
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        var program = new Binder(diagnostics).Bind(unit);
        return (program, diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content + "\n!train |> done;");
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code ?? "")];
    }

    /// <summary>Compile et exécute un programme entier, avec son train ; rend le code de sortie et ce qu'il écrit.</summary>
    private static (int Code, string Output) Run(string source)
    {
        var (program, diagnostics) = Bind(source);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);
        var result = ZigBuild.RunWithInput(executable, null, "");
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    /// <summary>Un handle qui écrit son ouverture et sa fermeture, et les gears pour l'ouvrir.</summary>
    private const string Header = """
        mold Handle -> fermer { int id; }
        gear ouvrir(int id): Handle {
            `ouvre{id} ` -> console;
            return {id: id};
        }
        gear fermer(Handle h): void { `ferme{h.id} ` -> console; }

        """;

    /// <summary>Le programme dont le gear <c>fin</c> exécute <paramref name="body"/>.</summary>
    private static (int Code, string Output) RunGear(string extra, string body) =>
        Run($"{Header}{extra}\ngear fin(): uint {{\n{body}\nreturn 0;\n}}\n!train |> $ fin();");

    #region Fermeture

    [ZigFact]
    public void Une_valeur_se_ferme_en_sortant_de_son_bloc()
    {
        var (code, output) = RunGear("", "{ var h: ouvrir(1); \"dans \" -> console; } \"apres \" -> console;");

        Assert.Equal(0, code);
        Assert.Equal("ouvre1 dans ferme1 apres ", output);
    }

    [ZigFact]
    public void Les_valeurs_se_ferment_dans_l_ordre_inverse_de_leur_creation()
    {
        var (_, output) = RunGear("", "{ var a: ouvrir(1); var b: ouvrir(2); var c: ouvrir(3); }");

        Assert.Equal("ouvre1 ouvre2 ouvre3 ferme3 ferme2 ferme1 ", output);
    }

    [ZigFact]
    public void Une_valeur_non_rangee_se_ferme_tout_de_suite()
    {
        var (_, output) = RunGear("", "ouvrir(7); \"apres \" -> console;");

        Assert.Equal("ouvre7 ferme7 apres ", output);
    }

    [ZigFact]
    public void Undefine_ferme_la_valeur_tout_de_suite()
    {
        var (_, output) = RunGear("", "var h: ouvrir(1); undefine h; \"apres \" -> console;");

        Assert.Equal("ouvre1 ferme1 apres ", output);
    }

    [ZigFact]
    public void Une_nouvelle_valeur_ferme_l_ancienne()
    {
        var (_, output) = RunGear("", "var h: ouvrir(1); h <- ouvrir(2); \"fin \" -> console;");

        Assert.Equal("ouvre1 ouvre2 ferme1 fin ferme2 ", output);
    }

    [ZigFact]
    public void Un_gear_voit_la_valeur_sans_la_fermer()
    {
        var (_, output) = RunGear("gear lire(Handle h): int => h.id;", "var h: ouvrir(4); `lu{lire(h)} ` -> console;");

        Assert.Equal("ouvre4 lu4 ferme4 ", output);
    }

    #endregion

    #region Déplacement

    [ZigFact]
    public void Un_deplacement_change_de_proprietaire_sans_fermer()
    {
        var (_, output) = RunGear("", "var a: ouvrir(1); { var b:: a; \"dans \" -> console; } \"apres \" -> console;");

        Assert.Equal("ouvre1 dans ferme1 apres ", output);
    }

    [ZigFact]
    public void Une_valeur_rendue_par_un_gear_n_est_fermee_qu_une_fois()
    {
        var (_, output) = RunGear("gear passer(): Handle { var h: ouvrir(5); return h; }", "{ var r: passer(); \"recu \" -> console; } \"apres \" -> console;");

        Assert.Equal("ouvre5 recu ferme5 apres ", output);
    }

    [ZigFact]
    public void Le_deplacement_vers_une_variable_assignee_ferme_l_ancienne_valeur()
    {
        var (_, output) = RunGear("", "var a: ouvrir(1); var b: ouvrir(2); b <-| a; \"fin \" -> console;");

        Assert.Equal("ouvre1 ouvre2 ferme2 fin ferme1 ", output);
    }

    #endregion

    #region Guards

    [ZigFact]
    public void Un_guard_qui_echoue_ferme_ses_valeurs_avant_le_rail()
    {
        const string program = Header + """
            guard Verifier(Handle h): void { if (h.id < 0) { fail "negatif"; } }
            guard Travail(int id): int {
                var h: ouvrir(id);
                Verifier(h);
                return h.id;
            }
            emit Signaler(Exception e): uint { `erreur:{e.message} ` -> console; return 1; }
            gear Afficher(int n): void { `ok{n} ` -> console; }

            """;
        var (ok, okOutput) = Run(program + "train 3 |> Travail !> [Signaler] |> Afficher |> done;");
        var (_, failedOutput) = Run(program + "train -3 |> Travail !> [Signaler] |> Afficher |> done;");

        Assert.Equal(0, ok);
        Assert.Equal("ouvre3 ferme3 ok3 ", okOutput);
        Assert.Equal("ouvre-3 ferme-3 erreur:negatif ", failedOutput);
    }

    [ZigFact]
    public void Un_guard_peut_rendre_une_valeur_a_cycle_de_vie()
    {
        const string program = Header + """
            guard Ouvrir(int id): Handle {
                if (id < 0) { fail "negatif"; }
                var h: ouvrir(id);
                return h;
            }
            guard Utiliser(int id): int {
                var h: Ouvrir(id);
                return h.id;
            }
            emit Signaler(Exception e): uint { `erreur:{e.message} ` -> console; return 1; }
            gear Afficher(int n): void { `ok{n} ` -> console; }

            """;
        var (_, ok) = Run(program + "train 6 |> Utiliser !> [Signaler] |> Afficher |> done;");
        var (_, failed) = Run(program + "train -6 |> Utiliser !> [Signaler] |> Afficher |> done;");

        Assert.Equal("ouvre6 ferme6 ok6 ", ok);
        Assert.Equal("erreur:negatif ", failed);
    }

    #endregion

    #region Molds qui en contiennent

    [ZigFact]
    public void Un_mold_qui_contient_une_valeur_a_cycle_de_vie_la_ferme_avec_lui()
    {
        var (_, output) = RunGear("mold Session { Handle canal; string nom; }", "{ var s: {canal: ouvrir(8), nom: \"x\"}; `nom{s.nom} ` -> console; } \"apres \" -> console;");

        Assert.Equal("ouvre8 nomx ferme8 apres ", output);
    }

    #endregion

    #region Erreurs

    [Fact]
    public void Copier_une_variable_est_une_erreur()
    {
        Assert.Equal(["E136"], Errors(Header + "gear f(): void { var a: ouvrir(1); var b: a; }"));
    }

    [Fact]
    public void Affecter_une_copie_est_une_erreur()
    {
        Assert.Equal(["E136"], Errors(Header + "gear f(): void { var a: ouvrir(1); var b: ouvrir(2); b <- a; }"));
    }

    [Fact]
    public void Rendre_un_parametre_est_une_erreur()
    {
        Assert.Equal(["E136"], Errors(Header + "gear f(Handle h): Handle { return h; }"));
    }

    [Fact]
    public void Deplacer_un_parametre_est_une_erreur()
    {
        Assert.Equal(["E136"], Errors(Header + "gear f(Handle h): void { var b:: h; }"));
    }

    [Fact]
    public void Ranger_une_valeur_existante_dans_un_champ_est_une_erreur()
    {
        Assert.Equal(["E136"], Errors(Header + "mold Session { Handle canal; }\ngear f(): void { var a: ouvrir(1); var s: {canal: a}; }"));
    }

    [Fact]
    public void Un_guard_ne_rend_pas_par_done_la_valeur_qu_il_recoit()
    {
        Assert.Equal(["E136"], Errors(Header + "guard g(Handle h): Handle { done; }"));
    }

    [Fact]
    public void Une_constante_a_cycle_de_vie_est_refusee()
    {
        Assert.Equal(["E137"], Errors("mold Handle -> fermer { int id; }\nconst Handle h: {id: 1};\ngear fermer(Handle h): void { }"));
    }

    [Fact]
    public void Un_tableau_de_valeurs_a_cycle_de_vie_est_refuse()
    {
        Assert.Equal(["E137"], Errors(Header + "gear f([Handle] hs): void { }"));
    }

    [Fact]
    public void Un_nullable_de_valeur_a_cycle_de_vie_est_refuse()
    {
        Assert.Equal(["E137"], Errors(Header + "gear f(): void { Handle? h: null; }"));
    }

    [Fact]
    public void Une_lambda_ne_capture_pas_une_valeur_a_cycle_de_vie()
    {
        Assert.Equal(["E137"], Errors(Header + "gear f(): void { var h: ouvrir(1); var g: () => h.id; }"));
    }

    [Fact]
    public void Les_valeurs_a_cycle_de_vie_ne_se_comparent_pas()
    {
        Assert.Contains("E040", Errors(Header + "gear f(): bool { var a: ouvrir(1); var b: ouvrir(2); return a eq b; }"));
    }

    [Fact]
    public void Le_gear_de_fin_doit_exister()
    {
        Assert.Equal(["E135"], Errors("mold Handle -> fermer { int id; }"));
    }

    [Fact]
    public void Le_gear_de_fin_ne_peut_pas_etre_un_guard()
    {
        Assert.Equal(["E135"], Errors("mold Handle -> fermer { int id; }\nguard fermer(Handle h): void { }"));
    }

    [Fact]
    public void Le_gear_de_fin_ne_renvoie_rien()
    {
        Assert.Equal(["E135"], Errors("mold Handle -> fermer { int id; }\ngear fermer(Handle h): int => 0;"));
    }

    [Fact]
    public void Le_gear_de_fin_recoit_le_mold()
    {
        Assert.Equal(["E135"], Errors("mold Handle -> fermer { int id; }\ngear fermer(int h): void { }"));
        Assert.Equal(["E135"], Errors("mold Handle -> fermer { int id; }\ngear fermer(): void { }"));
    }

    [Fact]
    public void Le_gear_de_fin_ne_modifie_pas_la_valeur()
    {
        Assert.Equal(["E135"], Errors("mold Handle -> fermer { int id; }\ngear fermer(mut Handle h): void { }"));
    }

    #endregion
}
