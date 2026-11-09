using Rgl.Syntax;

namespace Rgl.Tests.Syntax;

/// <summary>Tests de la table des mots-clés de <see cref="SyntaxFacts"/>.</summary>
public class SyntaxFactsTests
{
    [Fact]
    public void Chaque_kind_de_mot_cle_est_dans_la_table_une_seule_fois()
    {
        var expected = Enum.GetValues<TokenKind>()
            .Where(kind => kind.ToString().EndsWith("Keyword") && kind != TokenKind.ReservedKeyword)
            .Order();

        Assert.Equal(expected, SyntaxFacts.Keywords.Select(keyword => keyword.Kind).Order());
    }

    [Fact]
    public void Chaque_mot_cle_a_un_texte_unique()
    {
        var texts = SyntaxFacts.Keywords.Select(keyword => keyword.Text).ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void Le_texte_d_un_mot_cle_redonne_son_kind()
    {
        Assert.All(SyntaxFacts.Keywords, keyword =>
        {
            var kind = SyntaxFacts.GetKeywordKind(keyword.Text);
            Assert.True(kind.HasValue, keyword.Text);
            Assert.Equal(keyword.Kind, kind.Value);
        });
    }

    [Theory]
    [InlineData("score")]   // identificateur
    [InlineData("forge")]   // mot-clé contextuel
    [InlineData("Var")]     // la casse compte
    public void Un_mot_qui_n_est_pas_un_mot_cle_ne_donne_rien(string text)
    {
        Assert.True(SyntaxFacts.GetKeywordKind(text).IsEmpty);
    }

    /// <summary>Kinds sans texte fixe : leur texte dépend du source.</summary>
    private static readonly TokenKind[] KindsSansTexte =
    [
        TokenKind.Bad, TokenKind.EndOfFile, TokenKind.CppBlock, TokenKind.Identifier, TokenKind.ReservedKeyword,
        TokenKind.IntegerLiteral, TokenKind.FloatLiteral, TokenKind.DurationLiteral,
        TokenKind.StringLiteral, TokenKind.RawStringLiteral, TokenKind.TemplateStringLiteral,
        TokenKind.MutableStringLiteral, TokenKind.SymbolLiteral, TokenKind.RegexLiteral,
    ];

    [Fact]
    public void Chaque_kind_a_un_texte_sauf_ceux_qui_dependent_du_source()
    {
        Assert.All(Enum.GetValues<TokenKind>(), kind =>
            Assert.Equal(!KindsSansTexte.Contains(kind), SyntaxFacts.GetText(kind).HasValue));
    }

    [Theory]
    [InlineData(nameof(TokenKind.Semicolon), ";")]
    [InlineData(nameof(TokenKind.EachStrictPipe), "|!|>")]
    [InlineData(nameof(TokenKind.PlusAssign), "+<-")]
    [InlineData(nameof(TokenKind.VarKeyword), "var")]
    public void GetText_donne_le_texte_du_kind(string kind, string text)
    {
        // TokenKind est internal : une méthode de test publique ne peut pas le recevoir en paramètre.
        Assert.Equal(text, SyntaxFacts.GetText(Enum.Parse<TokenKind>(kind)).Value);
    }
}
