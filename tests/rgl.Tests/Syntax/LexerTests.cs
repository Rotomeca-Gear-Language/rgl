using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Syntax;

/// <summary>Tests du <see cref="Lexer"/> : tokens, trivia, erreurs lexicales et aller-retour.</summary>
public class LexerTests
{
    /// <summary>Découpe <paramref name="content"/> et renvoie ses tokens et ses diagnostics.</summary>
    private static (IReadOnlyList<Token> Tokens, IReadOnlyList<Diagnostic> Diagnostics) Lex(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize().Tokens;
        return (tokens, diagnostics.Items);
    }

    /// <summary>Sortes des tokens de <paramref name="content"/>, sans le token de fin.</summary>
    private static TokenKind[] Kinds(string content) =>
        [.. Lex(content).Tokens.Select(token => token.Kind).Where(kind => kind != TokenKind.EndOfFile)];

    /// <summary>Codes des diagnostics de <paramref name="content"/>.</summary>
    private static string[] Codes(string content) => [.. Lex(content).Diagnostics.Select(diagnostic => diagnostic.Code ?? "")];

    #region Symboles et mots

    [Fact]
    public void Les_symboles_sont_reconnus_en_plus_longue_correspondance()
    {
        Assert.Equal([TokenKind.Identifier, TokenKind.PlusAssign, TokenKind.IntegerLiteral, TokenKind.Semicolon], Kinds("x +<- 5;"));
        Assert.Equal([TokenKind.EachStrictPipe, TokenKind.EachPipe, TokenKind.BlueprintClose, TokenKind.Pipe, TokenKind.Bar], Kinds("|!|> ||> |]> |> |"));
        Assert.Equal([TokenKind.DotDotDot, TokenKind.DotDot, TokenKind.Dot], Kinds("... .. ."));
    }

    [Fact]
    public void Une_affectation_collee_reste_un_seul_token()
    {
        Assert.Equal([TokenKind.Identifier, TokenKind.Assign, TokenKind.IntegerLiteral], Kinds("a<-5"));
    }

    [Fact]
    public void Deux_chevrons_fermants_restent_deux_tokens()
    {
        Assert.Equal([TokenKind.Greater, TokenKind.Greater], Kinds(">>"));
    }

    [Fact]
    public void Un_symbole_en_toute_fin_de_fichier_est_reconnu()
    {
        Assert.Equal([TokenKind.Identifier, TokenKind.Semicolon], Kinds("a;"));
    }

    [Fact]
    public void Mots_cles_mots_reserves_et_identificateurs()
    {
        Assert.Equal([TokenKind.VarKeyword, TokenKind.ReservedKeyword, TokenKind.Identifier, TokenKind.Identifier],
            Kinds("var class score forge"));
    }

    [Fact]
    public void Un_identificateur_de_plus_de_255_caracteres_est_signale()
    {
        Assert.Equal(["E026"], Codes(new string('a', 256)));
        Assert.Empty(Codes(new string('a', 255)));
    }

    [Fact]
    public void Un_caractere_invalide_donne_un_token_Bad_et_le_lexer_continue()
    {
        var (tokens, diagnostics) = Lex("a € b");

        Assert.Equal([TokenKind.Identifier, TokenKind.Bad, TokenKind.Identifier, TokenKind.EndOfFile], tokens.Select(token => token.Kind));
        Assert.Equal("E023", Assert.Single(diagnostics).Code);
    }

    #endregion

    #region Nombres

    [Theory]
    [InlineData("1_000", nameof(TokenKind.IntegerLiteral))]
    [InlineData("0xFF", nameof(TokenKind.IntegerLiteral))]
    [InlineData("0b1010", nameof(TokenKind.IntegerLiteral))]
    [InlineData("0o17", nameof(TokenKind.IntegerLiteral))]
    [InlineData("1.5", nameof(TokenKind.FloatLiteral))]
    [InlineData("1.5e3", nameof(TokenKind.FloatLiteral))]
    [InlineData("2e-3", nameof(TokenKind.FloatLiteral))]
    [InlineData("100ms", nameof(TokenKind.DurationLiteral))]
    [InlineData("1.5s", nameof(TokenKind.DurationLiteral))]
    [InlineData("2min", nameof(TokenKind.DurationLiteral))]
    [InlineData("3h", nameof(TokenKind.DurationLiteral))]
    public void Les_nombres_valides_forment_un_seul_token(string content, string kind)
    {
        var (tokens, diagnostics) = Lex(content);

        Assert.Empty(diagnostics);
        Assert.Equal(Enum.Parse<TokenKind>(kind), tokens[0].Kind);
        Assert.Equal(content, tokens[0].Text);
    }

    [Fact]
    public void Une_plage_n_est_pas_un_flottant()
    {
        Assert.Equal([TokenKind.IntegerLiteral, TokenKind.DotDot, TokenKind.IntegerLiteral], Kinds("0..100"));
    }

    [Theory]
    [InlineData("var f: 1.;", "E024")]
    [InlineData("0x", "E025")]
    [InlineData("0b;", "E025")]
    [InlineData("10px", "E027")]
    [InlineData("0xFFms", "E027")]
    public void Les_nombres_mal_formes_sont_signales(string content, string code)
    {
        Assert.Equal([code], Codes(content));
    }

    #endregion

    #region Chaînes, symboles et regex

    [Theory]
    [InlineData("\"a\\n\\\"b\"", nameof(TokenKind.StringLiteral))]
    [InlineData("'C:\\chemin'", nameof(TokenKind.RawStringLiteral))]
    [InlineData("'l''eau'", nameof(TokenKind.RawStringLiteral))]
    [InlineData("`x = {x}\nsuite`", nameof(TokenKind.TemplateStringLiteral))]
    [InlineData("m\"modifiable\"", nameof(TokenKind.MutableStringLiteral))]
    [InlineData("§pret", nameof(TokenKind.SymbolLiteral))]
    [InlineData("/^[a-z/]+$/i", nameof(TokenKind.RegexLiteral))]
    public void Les_litteraux_forment_un_seul_token(string content, string kind)
    {
        var (tokens, diagnostics) = Lex(content);

        Assert.Empty(diagnostics);
        Assert.Equal([Enum.Parse<TokenKind>(kind), TokenKind.EndOfFile], tokens.Select(token => token.Kind));
        Assert.Equal(content, tokens[0].Text);
    }

    [Theory]
    [InlineData("\"abc")]
    [InlineData("\"abc\nsuite\"")]
    [InlineData("'abc")]
    [InlineData("`abc")]
    [InlineData("var r: /abc")]
    public void Une_chaine_non_fermee_est_signalee(string content)
    {
        Assert.Contains("E021", Codes(content));
    }

    [Fact]
    public void Une_chaine_classique_non_fermee_s_arrete_a_la_fin_de_ligne()
    {
        var (tokens, diagnostics) = Lex("var s: \"abc\nvar t: 1;");

        Assert.Equal("E021", Assert.Single(diagnostics).Code);
        Assert.Equal("\"abc", tokens[3].Text);
        Assert.Equal(TokenKind.VarKeyword, tokens[4].Kind);
    }

    [Fact]
    public void Un_paragraphe_seul_est_un_caractere_invalide()
    {
        Assert.Equal(["E023"], Codes("§ x"));
    }

    [Fact]
    public void Une_barre_oblique_apres_une_valeur_est_une_division()
    {
        Assert.Equal([TokenKind.Identifier, TokenKind.Slash, TokenKind.Identifier], Kinds("a / b"));
        Assert.Equal([TokenKind.Identifier, TokenKind.Slash, TokenKind.Identifier], Kinds("a/b"));
        Assert.Equal([TokenKind.Identifier, TokenKind.OpenParen, TokenKind.CloseParen, TokenKind.Slash, TokenKind.IntegerLiteral], Kinds("f() / 2"));
        Assert.Equal([TokenKind.Identifier, TokenKind.AsKeyword, TokenKind.IntKeyword, TokenKind.Slash, TokenKind.IntegerLiteral], Kinds("x as int / 2"));
    }

    [Fact]
    public void Une_barre_oblique_ailleurs_commence_une_regex()
    {
        Assert.Equal([TokenKind.VarKeyword, TokenKind.Identifier, TokenKind.Colon, TokenKind.RegexLiteral, TokenKind.Semicolon],
            Kinds("var r: /a+/i;"));
    }

    #endregion

    #region Trivia

    [Fact]
    public void Les_trivia_suivent_la_regle_de_rattachement()
    {
        var (tokens, _) = Lex("// tête\nvar x: 1;   // fin\r\n\n");

        var var = tokens[0];
        Assert.Equal([TriviaKind.SingleComment, TriviaKind.EndOfLine], var.Before.Select(trivia => trivia.Kind));
        Assert.Equal("// tête", var.Before[0].Span.Text);

        var pointVirgule = tokens[4];
        Assert.Equal([TriviaKind.Whitespace, TriviaKind.SingleComment, TriviaKind.EndOfLine], pointVirgule.After.Select(trivia => trivia.Kind));
        Assert.Equal("// fin", pointVirgule.After[1].Span.Text);
        Assert.Equal("\r\n", pointVirgule.After[2].Span.Text);

        var fin = tokens[5];
        Assert.Equal(TokenKind.EndOfFile, fin.Kind);
        Assert.Equal([TriviaKind.EndOfLine], fin.Before.Select(trivia => trivia.Kind));
    }

    [Fact]
    public void Les_commentaires_de_bloc_ne_s_imbriquent_pas()
    {
        var (tokens, diagnostics) = Lex("/* a */ /**/ x");

        Assert.Empty(diagnostics);
        Assert.Equal([TriviaKind.BlockComment, TriviaKind.Whitespace, TriviaKind.BlockComment, TriviaKind.Whitespace],
            tokens[0].Before.Select(trivia => trivia.Kind));
    }

    [Fact]
    public void Un_commentaire_de_bloc_non_ferme_va_jusqu_a_la_fin_et_est_signale()
    {
        var (tokens, diagnostics) = Lex("x; /*/ y");

        Assert.Equal("E022", Assert.Single(diagnostics).Code);
        Assert.Equal("/*/ y", tokens[1].After[1].Span.Text);
        Assert.Equal(TokenKind.EndOfFile, tokens[2].Kind);
    }

    #endregion

    #region W006 et aller-retour

    [Theory]
    [InlineData("")]
    [InlineData("  \n\t\n")]
    [InlineData("// rien\n/* toujours rien */\n")]
    public void Un_fichier_sans_instruction_avertit(string content)
    {
        var (tokens, diagnostics) = Lex(content);

        Assert.Equal(TokenKind.EndOfFile, Assert.Single(tokens).Kind);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("W006", DiagnosticLevel.Warning), (diagnostic.Code, diagnostic.Level));
    }

    [Theory]
    [InlineData("")]
    [InlineData("// tête\nvar x: 1;   // fin\r\n\n")]
    [InlineData("!train |> done; /* bloc\nsur deux lignes */ x\n")]
    [InlineData("var s: \"abc\nvar r: /x[/]/g; € 1. 0x 10px §")]
    [InlineData("x; /* jamais fermé\n")]
    public void Recoller_les_tokens_et_leurs_trivia_redonne_le_source(string content)
    {
        var text = string.Concat(Lex(content).Tokens.Select(token =>
            string.Concat(token.Before.Select(trivia => trivia.Span.Text)) + token.Text + string.Concat(token.After.Select(trivia => trivia.Span.Text))));

        Assert.Equal(content, text);
    }

    [Fact]
    public void Tokenize_deux_fois_ne_signale_pas_deux_fois_les_erreurs()
    {
        var diagnostics = new DiagnosticBag();
        var lexer = new Lexer(new SourceText("main.rgl", "€"), diagnostics);

        var premier = lexer.Tokenize();
        var second = lexer.Tokenize();

        Assert.Same(premier.Tokens, second.Tokens);
        Assert.Single(diagnostics.Items);
    }

    #endregion
}
