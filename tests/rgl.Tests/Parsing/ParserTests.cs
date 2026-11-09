using Rgl.AST;
using Rgl.Parsing;
using Rgl.Diagnostics;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Parsing;

/// <summary>Tests du parseur : arbre produit pour un train valide, erreurs et récupération.</summary>
public class ParserTests
{
    /// <summary>Lexe puis parse <paramref name="content"/>.</summary>
    private static (CompilationUnit Unit, IReadOnlyList<Diagnostic> Diagnostics) Parse(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        return (unit, diagnostics.Items);
    }

    /// <summary>Arbre affiché de <paramref name="content"/>, en vérifiant qu'aucune erreur n'est signalée.</summary>
    private static string Tree(string content)
    {
        var (unit, diagnostics) = Parse(content);
        Assert.Empty(diagnostics);
        return AstPrinter.Print(unit);
    }

    /// <summary>Codes et positions des diagnostics de <paramref name="content"/>.</summary>
    private static string[] Errors(string content) =>
        [.. Parse(content).Diagnostics.Select(d => $"{d.Code} {d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];

    private static string Lines(string text) => text.ReplaceLineEndings("\n");

    [Fact]
    public void Le_train_de_la_demo_V0()
    {
        Assert.Equal(Lines("""
            CompilationUnit 1:1
              TrainDeclaration 1:1 unguarded=false parameters=none
                Pipeline 1:7
                  DoneStep 1:10

            """), Tree("train |> done;"));
    }

    [Fact]
    public void Un_train_d_entree_complet()
    {
        Assert.Equal(Lines("""
            CompilationUnit 1:1
              TrainDeclaration 1:1 unguarded=true parameters=1
                Parameter 1:8 name=args
                  ArrayType 1:8
                    PrimitiveType 1:9 type=string
                NameExpression 1:23 name=args
                Pipeline 1:28
                  CallStep 1:31 gear=parseArgs
                    Rail 1:41
                      CallStep 1:45 gear=printUsage
                      ExitCodeStep 1:59
                        LiteralExpression 1:61 kind=IntegerLiteral text=2
                  CallStep 1:67 gear=add
                    LiteralExpression 1:71 kind=IntegerLiteral text=1
                    CallExpression 1:74
                      NameExpression 1:74 name=f
                      NameExpression 1:76 name=x
                    Rail 2:5
                      FailStep 2:9
                        LiteralExpression 2:14 kind=StringLiteral text="oups"
                  ExitCodeStep 2:25
                    LiteralExpression 2:27 kind=IntegerLiteral text=0

            """), Tree("!train([string] args) args |> parseArgs !> [printUsage |> $ 2] |> add(1, f(x))\n    !> [fail \"oups\"] |> $ 0;"));
    }

    [Fact]
    public void Plusieurs_parametres_et_un_type_nomme()
    {
        Assert.Contains("Parameter 1:14 name=p", Tree("train(int n, Profile p) n |> done;"));
    }

    [Fact]
    public void Un_fichier_vide_donne_une_unite_sans_train()
    {
        var (unit, diagnostics) = Parse("// rien\n");

        Assert.Null(unit.Train);
        Assert.Equal("W006", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData("train |> done", "E030 1:14")]                 // « ; » manquant, signalé juste après done
    [InlineData("train |> done\n", "E030 1:14")]               // sur la ligne de done, pas sur la suivante
    [InlineData("|> done;", "E030 1:1")]                      // « train » manquant
    [InlineData("train done;", "E030 1:11")]                  // « |> » manquant : done est lu comme valeur de départ
    [InlineData("train |> ;", "E031 1:10")]                   // étape manquante
    [InlineData("train([string]) |> done;", "E030 1:15")]     // nom de paramètre manquant
    [InlineData("train |> $ ;", "E032 1:12")]                 // expression manquante
    [InlineData("train |> done; train |> done;", "E034 1:16")] // un seul train par fichier
    [InlineData("var int: 5;", "E011 1:5")]                     // nom de type comme nom de variable
    [InlineData("train([string] string) |> done;", "E011 1:16")] // … comme nom de paramètre
    [InlineData("train |> $ double;", "E011 1:12")]             // … comme valeur
    public void Les_erreurs_de_syntaxe_sont_signalees_a_leur_place(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Fact]
    public void Un_parametre_sans_type_puis_un_type_sans_nom_donnent_deux_erreurs()
    {
        Assert.Equal(["E033 1:7", "E030 1:10"], Errors("train(, x) |> done;"));
    }

    [Fact]
    public void Un_caractere_invalide_n_est_signale_que_par_le_lexer()
    {
        Assert.Equal(["E023 1:10"], Errors("train |> € ;"));
    }

    [Fact]
    public void Apres_un_point_virgule_manquant_le_parseur_se_resynchronise()
    {
        var (unit, diagnostics) = Parse("train |> done done done;");

        Assert.Equal("E030", Assert.Single(diagnostics).Code);
        Assert.NotNull(unit.Train);
        Assert.Equal("train |> done done done;", unit.Train.Span.Text);
    }

    [Fact]
    public void Un_noeud_manquant_garde_l_arbre_complet()
    {
        var (unit, _) = Parse("train |> ;");

        Assert.Contains("MissingStep 1:10", AstPrinter.Print(unit));
    }
}
