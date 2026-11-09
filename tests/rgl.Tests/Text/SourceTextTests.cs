using Rgl.Diagnostics;
using Rgl.Text;

namespace Rgl.Tests.Text;

/// <summary>Tests de <see cref="SourceText"/> : débuts de ligne et conversion indice → position.</summary>
public class SourceTextTests
{
    // Débuts de ligne : 0, 10, 22.
    private static readonly SourceText Texte = new("main.rgl", "var a: 1;\nvar b: \"x\";\nfin;");

    [Fact]
    public void Les_debuts_de_ligne_suivent_chaque_saut_de_ligne()
    {
        Assert.Equal([0u, 10u, 22u], Texte.LineStart.Select(start => start.Value));
    }

    [Theory]
    [InlineData(0u, 1u, 1u)]
    [InlineData(3u, 1u, 4u)]
    [InlineData(9u, 1u, 10u)]   // le '\n' appartient à la ligne qu'il termine
    [InlineData(10u, 2u, 1u)]
    [InlineData(17u, 2u, 8u)]
    [InlineData(25u, 3u, 4u)]   // dernière ligne
    [InlineData(26u, 3u, 5u)]   // fin du fichier (Content.Length)
    public void L_indexeur_donne_la_ligne_et_la_colonne(uint index, uint line, uint column)
    {
        Assert.Equal(new Position(line, column), Texte[index]);
    }

    [Fact]
    public void Un_fichier_d_une_seule_ligne_a_une_seule_ligne()
    {
        var texte = new SourceText("main.rgl", "fin;");

        Assert.Equal(new Position(1u, 3u), texte[2u]);
    }

    [Fact]
    public void Crlf_compte_pour_un_seul_saut_de_ligne()
    {
        var texte = new SourceText("main.rgl", "a;\r\nb;");

        Assert.Equal(new Position(2u, 1u), texte[4u]);
    }

    [Fact]
    public void Un_index_apres_la_fin_du_fichier_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Texte[27u]);
    }

    [Fact]
    public void FromPath_retire_le_BOM()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "main.rgl");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'f', (byte)'i', (byte)'n', (byte)';']);

        var diagnostics = new DiagnosticBag();
        var texte = SourceText.FromPath(path, diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.NotNull(texte);
        Assert.Equal("fin;", texte.Content);
        Assert.Equal(path, texte.Path);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'f', 0 }, "UTF-16 LE")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'f' }, "UTF-16 BE")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0, 0, (byte)'f', 0, 0, 0 }, "UTF-32 LE")]
    [InlineData(new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0, (byte)'f' }, "UTF-32 BE")]
    public void FromPath_refuse_l_UTF16_et_l_UTF32_en_nommant_l_encodage(byte[] contenu, string encodage)
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "main.rgl");
        File.WriteAllBytes(path, contenu);
        var diagnostics = new DiagnosticBag();

        var texte = SourceText.FromPath(path, diagnostics);

        Assert.Null(texte);
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal("E029", diagnostic.Code);
        Assert.Contains(encodage, diagnostic.Message);
    }

    [Fact]
    public void FromPath_signale_un_octet_UTF8_invalide_a_sa_position()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "main.rgl");
        // « é » encodé en Latin-1 (0xE9) au lieu de l'UTF-8, sur la ligne 2.
        File.WriteAllBytes(path, [(byte)'a', (byte)';', (byte)'\n', (byte)'"', 0xE9, (byte)'"']);
        var diagnostics = new DiagnosticBag();

        var texte = SourceText.FromPath(path, diagnostics);

        Assert.NotNull(texte);
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal("E028", diagnostic.Code);
        Assert.Equal(new Position(2u, 2u), diagnostic.Span!.Value.StartPosition);
    }
}
