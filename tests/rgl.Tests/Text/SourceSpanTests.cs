using Rgl.Text;

namespace Rgl.Tests.Text;

/// <summary>Tests de <see cref="SourceSpan"/> : bornes, texte, positions et union.</summary>
public class SourceSpanTests
{
    // Débuts de ligne : 0, 10, 22 ; 26 caractères.
    private static readonly SourceText Texte = new("main.rgl", "var a: 1;\nvar b: \"x\";\nfin;");

    [Fact]
    public void Un_span_couvre_son_texte_avec_une_fin_exclusive()
    {
        var span = new SourceSpan(Texte, 17u, 3u);

        Assert.Equal("\"x\"", span.Text);
        Assert.Equal(20u, span.End.Value);
        Assert.Equal(new Position(2u, 8u), span.StartPosition);
        Assert.Equal(new Position(2u, 11u), span.EndPosition);
    }

    [Fact]
    public void FromBounds_calcule_la_longueur()
    {
        var span = SourceSpan.FromBounds(Texte, 10u, 13u);

        Assert.Equal(3u, span.Length.Value);
        Assert.Equal("var", span.Text);
    }

    [Fact]
    public void Un_span_vide_en_fin_de_fichier_est_legal()
    {
        var span = new SourceSpan(Texte, 26u, 0u);

        Assert.Equal("", span.Text);
        Assert.Equal(new Position(3u, 5u), span.StartPosition);
    }

    [Fact]
    public void Un_span_qui_depasse_la_fin_du_fichier_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSpan(Texte, 25u, 2u));
    }

    [Fact]
    public void FromBounds_refuse_une_fin_avant_le_debut()
    {
        Assert.Throws<OverflowException>(() => SourceSpan.FromBounds(Texte, 13u, 10u));
    }

    [Fact]
    public void L_union_couvre_les_deux_spans_et_l_entre_deux()
    {
        var motVar = new SourceSpan(Texte, 10u, 3u);
        var pointVirgule = new SourceSpan(Texte, 20u, 1u);

        Assert.Equal("var b: \"x\";", pointVirgule.Union(motVar).Text);
        Assert.Equal(motVar.Union(pointVirgule), pointVirgule.Union(motVar));
    }

    [Fact]
    public void L_union_de_deux_fichiers_differents_est_refusee()
    {
        var autre = new SourceText("main.rgl", "var a: 1;\nvar b: \"x\";\nfin;");

        Assert.Throws<ArgumentException>(() => new SourceSpan(Texte, 0u, 3u).Union(new SourceSpan(autre, 0u, 3u)));
    }

    [Fact]
    public void ToString_donne_chemin_ligne_et_colonne()
    {
        Assert.Equal("main.rgl:2:8", new SourceSpan(Texte, 17u, 3u).ToString());
    }
}
