using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Steps;
using Rgl.Diagnostics;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Parsing;

/// <summary>Tests du parsing de Pratt : précédence, associativité, opérateurs préfixes et W008.</summary>
public class ExpressionParserTests
{
    /// <summary>
    /// Parse <paramref name="expression"/> comme code de sortie d'un train
    /// (<c>train |&gt; $ expression;</c>) et l'écrit avec toutes ses parenthèses.
    /// </summary>
    private static (string Tree, IReadOnlyList<Diagnostic> Diagnostics) Parse(string expression)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", $"train |> $ {expression};"), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        var exit = Assert.IsType<ExitCodeStep>(unit.Train!.Pipeline.Steps[^1]);
        return (Tree(exit.Code), diagnostics.Items);
    }

    /// <summary>Arbre de <paramref name="expression"/>, en vérifiant qu'aucun diagnostic n'est signalé.</summary>
    private static string Clean(string expression)
    {
        var (tree, diagnostics) = Parse(expression);
        Assert.Empty(diagnostics);
        return tree;
    }

    /// <summary>Écrit l'arbre en notation préfixe parenthésée : <c>(+ 1 (* 2 3))</c>.</summary>
    private static string Tree(IExpression expression) => expression switch
    {
        LiteralExpression literal => literal.Text,
        NameExpression name => name.Name.Name,
        UnaryExpression unary => $"({SyntaxFacts.GetText(unary.Operator).Value} {Tree(unary.Operand)})",
        BinaryExpression binary => $"({SyntaxFacts.GetText(binary.Operator).Value} {Tree(binary.Left)} {Tree(binary.Right)})",
        ParenthesizedExpression parenthesized => Tree(parenthesized.Inner),
        CallExpression call => $"(appel {Tree(call.Callee)}{string.Concat(call.Arguments.Select(a => " " + Tree(a)))})",
        IncrementExpression increment => $"({SyntaxFacts.GetText(increment.Operator).Value}{(increment.Step is null ? "" : "<" + increment.Step.Text + ">")} {Tree(increment.Operand)})",
        ConditionalExpression conditional => $"(? {Tree(conditional.Condition)} {Tree(conditional.WhenTrue)} {Tree(conditional.WhenFalse)})",
        MissingExpression => "<manquant>",
        _ => throw new ArgumentException(expression.GetType().Name),
    };

    [Theory]
    [InlineData("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [InlineData("(1 + 2) * 3", "(* (+ 1 2) 3)")]
    [InlineData("a - b - c", "(- (- a b) c)")]                  // associativité à gauche
    [InlineData("a ?? b ?? c", "(?? a (?? b c))")]              // coalescence : à droite
    [InlineData("a > 0 and b eq 3", "(and (> a 0) (eq b 3))")]
    [InlineData("a or b and c", "(or a (and b c))")]
    [InlineData("a xor b or c", "(or (xor a b) c)")]
    [InlineData("flags bitand 4 eq 0", "(eq (bitand flags 4) 0)")] // bit à bit avant les comparaisons
    [InlineData("1 shl 4 + 1", "(shl 1 (+ 4 1))")]
    [InlineData("a bitor b bitxor c bitand d", "(bitor a (bitxor b (bitand c d)))")]
    [InlineData("saisie ?° \"x\" ?? y", "(?° saisie (?? \"x\" y))")]
    public void Les_operateurs_binaires_respectent_precedence_et_associativite(string expression, string expected)
    {
        Assert.Equal(expected, Clean(expression));
    }

    [Theory]
    [InlineData("-a * b", "(* (- a) b)")]
    [InlineData("not ok and fini", "(and (not ok) fini)")]
    [InlineData("- -a", "(- (- a))")]
    [InlineData("invert m bitand 1", "(bitand (invert m) 1)")]
    [InlineData("increment n + 1", "(+ (increment n) 1)")]
    [InlineData("post_decrement<3> n * 2", "(* (post_decrement<3> n) 2)")]
    [InlineData("a or b ? 1 : 2", "(? (or a b) 1 2)")]                // ternaire : moins lié que la logique
    [InlineData("a ?? b ? c : d", "(? (?? a b) c d)")]                // … et que la coalescence
    [InlineData("a ? b : c ? d : e", "(? a b (? c d e))")]            // associatif à droite
    [InlineData("a ? b ? c : d : e", "(? a (? b c d) e)")]
    [InlineData("1 + (a ? 2 : 3)", "(+ 1 (? a 2 3))")]
    public void Les_operateurs_prefixes_lient_plus_fort_que_les_binaires(string expression, string expected)
    {
        Assert.Equal(expected, Clean(expression));
    }

    [Fact]
    public void Not_eq_se_lit_comme_la_negation_d_une_egalite()
    {
        Assert.Equal("(and (not (eq a 3)) ok)", Clean("a not eq 3 and ok"));
    }

    [Fact]
    public void Les_appels_et_leurs_arguments_sont_des_expressions_completes()
    {
        Assert.Equal("(+ (appel f (+ 1 2) (- x)) 1)", Clean("f(1 + 2, -x) + 1"));
    }

    [Fact]
    public void Une_affectation_collee_dans_une_expression_est_relue_et_avertit()
    {
        var (tree, diagnostics) = Parse("a<-5");

        Assert.Equal("(< a (- 5))", tree);
        var warning = Assert.Single(diagnostics);
        Assert.Equal(("W008", DiagnosticLevel.Warning), (warning.Code, warning.Level));
        Assert.Equal("<-", warning.Span!.Value.Text);
    }

    [Fact]
    public void Une_affectation_collee_garde_la_precedence_de_la_comparaison()
    {
        var (tree, _) = Parse("a + 1<-5 and ok");

        Assert.Equal("(and (< (+ a 1) (- 5)) ok)", tree);
    }

    [Fact]
    public void Un_operande_manquant_est_signale()
    {
        var (tree, diagnostics) = Parse("1 + ");

        Assert.Equal("(+ 1 <manquant>)", tree);
        Assert.Equal("E032", Assert.Single(diagnostics).Code);
    }
}
