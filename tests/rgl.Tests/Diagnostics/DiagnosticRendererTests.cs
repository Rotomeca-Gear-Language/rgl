using Rgl.Diagnostics;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Tests.Diagnostics;

/// <summary>Tests de <see cref="DiagnosticRenderer"/> : extrait du source, soulignement, aides et couleurs.</summary>
public class DiagnosticRendererTests
{
    private static readonly string Here = Path.GetFullPath(".");

    private static SourceText Source(string content) => new(Path.Combine(Here, "main.rgl"), content);

    private static SourceSpan Span(SourceText source, int start, int length) =>
        new(source, RUint.CreateChecked(start), RUint.CreateChecked(length));

    private static string Render(Diagnostic diagnostic, bool color = false) =>
        new DiagnosticRenderer(Here, color).Render(diagnostic);

    private static string Lines(string text) => text.ReplaceLineEndings("\n");

    [Fact]
    public void Une_erreur_montre_l_extrait_le_soulignement_l_etiquette_et_l_aide()
    {
        var source = Source("// commentaire\ntrain |> done\n");
        var diagnostic = new Diagnostic(DiagnosticLevel.Error, "« ; » attendu à la fin du train", Code: "E030",
            Span: Span(source, 28, 0), Label: "« ; » manquant ici",
            Help: ["un train, rails compris, se termine par un unique « ; »"]);

        Assert.Equal(Lines("""
            erreur[E030]: « ; » attendu à la fin du train
             --> main.rgl:2:14
              |
            2 | train |> done
              |              ^ « ; » manquant ici
              |
              = aide : un train, rails compris, se termine par un unique « ; »

            """), Render(diagnostic));
    }

    [Fact]
    public void Le_soulignement_couvre_tout_le_span_et_un_avertissement_utilise_des_tildes()
    {
        var source = Source("var x: a<-5;");
        var diagnostic = new Diagnostic(DiagnosticLevel.Warning, "« a<-5 » est lu comme « a < -5 »", Code: "W008",
            Span: Span(source, 7, 4), Help: ["ajoute des espaces pour lever l'ambiguïté"]);

        Assert.Equal(Lines("""
            avertissement[W008]: « a<-5 » est lu comme « a < -5 »
             --> main.rgl:1:8
              |
            1 | var x: a<-5;
              |        ~~~~
              |
              = aide : ajoute des espaces pour lever l'ambiguïté

            """), Render(diagnostic));
    }

    [Fact]
    public void Les_tabulations_ne_decalent_pas_le_soulignement()
    {
        var source = Source("\ttrain |> ;");
        var diagnostic = new Diagnostic(DiagnosticLevel.Error, "étape attendue", Code: "E031", Span: Span(source, 10, 0));

        Assert.Equal(Lines("""
            erreur[E031]: étape attendue
             --> main.rgl:1:11
              |
            1 |     train |> ;
              |              ^

            """), Render(diagnostic));
    }

    [Fact]
    public void La_marge_s_elargit_avec_le_numero_de_ligne()
    {
        var source = Source(string.Concat(Enumerable.Repeat("\n", 9)) + "x");
        var rendered = Render(new Diagnostic(DiagnosticLevel.Error, "m", Code: "E023", Span: Span(source, 9, 1)));

        Assert.Equal(Lines("""
            erreur[E023]: m
              --> main.rgl:10:1
               |
            10 | x
               | ^

            """), rendered);
    }

    [Fact]
    public void Un_span_sur_plusieurs_lignes_est_souligne_jusqu_a_la_fin_de_sa_premiere_ligne()
    {
        var source = Source("x; /* pas\nfermé");
        var rendered = Render(new Diagnostic(DiagnosticLevel.Error, "m", Code: "E022", Span: Span(source, 3, 12)));

        Assert.Contains(Lines("""
            1 | x; /* pas
              |    ^^^^^^
            """), rendered);
    }

    [Fact]
    public void Un_diagnostic_sans_position_affiche_son_fichier_et_ses_aides()
    {
        var diagnostic = new Diagnostic(DiagnosticLevel.Error, "le fichier est encodé en UTF-16 LE", Path.Combine(Here, "main.rgl"), "E029",
            Help: ["réenregistre le fichier en UTF-8"]);

        Assert.Equal(Lines("""
            erreur[E029]: le fichier est encodé en UTF-16 LE
             --> main.rgl
             = aide : réenregistre le fichier en UTF-8

            """), Render(diagnostic));
        Assert.Equal("info: tout va bien\n", Render(new Diagnostic(DiagnosticLevel.Info, "tout va bien")));
    }

    [Fact]
    public void Les_couleurs_ne_sont_ajoutees_que_si_elles_sont_demandees()
    {
        var source = Source("train |> done");
        var diagnostic = new Diagnostic(DiagnosticLevel.Error, "m", Code: "E030", Span: Span(source, 13, 0));

        Assert.Contains("\e[1;31merreur[E030]\e[0m", Render(diagnostic, color: true));
        Assert.DoesNotContain('\e', Render(diagnostic, color: false));
    }

    [Fact]
    public void Plusieurs_diagnostics_sont_separes_par_une_ligne_vide()
    {
        var writer = new StringWriter();
        new DiagnosticRenderer(Here, false).Write(writer,
            [new Diagnostic(DiagnosticLevel.Error, "a"), new Diagnostic(DiagnosticLevel.Warning, "b")]);

        Assert.Equal("erreur: a\n\navertissement: b\n", writer.ToString());
    }
}
