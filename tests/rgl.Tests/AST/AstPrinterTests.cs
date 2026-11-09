using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Steps;
using Rgl.AST.Train;
using Rgl.AST.Types;
using Rgl.AST;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Tests.AST;

/// <summary>Tests de <see cref="AstPrinter"/> : le format des fichiers <c>.ast</c> des tests golden.</summary>
public class AstPrinterTests
{
    /// <summary>Span de <paramref name="text"/>, à sa première occurrence dans <paramref name="source"/>.</summary>
    private static SourceSpan At(SourceText source, string text, int occurrence = 0)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; ++i) index = source.Content.IndexOf(text, index + 1, StringComparison.Ordinal);
        return new SourceSpan(source, RUint.CreateChecked(index), RUint.CreateChecked(text.Length));
    }

    /// <summary>Span qui va du début de <paramref name="first"/> à la fin de <paramref name="last"/>.</summary>
    private static SourceSpan From(SourceSpan first, SourceSpan last) => first.Union(last);

    [Fact]
    public void Le_train_minimal_s_affiche_en_arbre_indente()
    {
        var source = new SourceText("main.rgl", "train |> done;");
        var pipeline = new Pipeline(From(At(source, "|>"), At(source, "done")), [new DoneStep(At(source, "done"))]);
        var train = new TrainDeclaration(From(At(source, "train"), At(source, ";")), false,
            null, null, pipeline);
        var unit = new CompilationUnit(At(source, "train |> done;"), [train]);

        Assert.Equal("""
            CompilationUnit 1:1
              TrainDeclaration 1:1 unguarded=false parameters=none
                Pipeline 1:7
                  DoneStep 1:10

            """.ReplaceLineEndings("\n"), AstPrinter.Print(unit));
    }

    [Fact]
    public void Un_train_complet_affiche_parametres_depart_etapes_et_rails()
    {
        var source = new SourceText("main.rgl", "!train([string] args) args |> add(1 + n)\n    !> [printUsage |> fail \"x\"] |> $ 0;");

        var parameter = new Parameter(From(At(source, "[string]"), At(source, "args")),
            new ArrayType(At(source, "[string]"), new PrimitiveType(At(source, "string"), TokenKind.StringKeyword)),
            new Identifier(At(source, "args")));
        var argument = new BinaryExpression(At(source, "1 + n"),
            new LiteralExpression(At(source, "1"), TokenKind.IntegerLiteral), TokenKind.Plus,
            new NameExpression(new Identifier(At(source, "n", 2))), At(source, "+"));
        var rail = new Rail(From(At(source, "!>"), At(source, "]", 1)),
        [
            new CallStep(At(source, "printUsage"), new Identifier(At(source, "printUsage")), []),
            new FailStep(At(source, "fail \"x\""), new LiteralExpression(At(source, "\"x\""), TokenKind.StringLiteral)),
        ]);
        var pipeline = new Pipeline(From(At(source, "|>"), At(source, "0")),
        [
            new CallStep(At(source, "add(1 + n)"), new Identifier(At(source, "add")), [argument], rail),
            new ExitCodeStep(At(source, "$ 0"), new LiteralExpression(At(source, "0"), TokenKind.IntegerLiteral)),
        ]);
        var train = new TrainDeclaration(From(At(source, "!train"), At(source, ";")), true,
            [parameter],
            new NameExpression(new Identifier(At(source, "args", 1))),
            pipeline);

        Assert.Equal("""
            TrainDeclaration 1:1 unguarded=true parameters=1
              Parameter 1:8 name=args
                ArrayType 1:8
                  PrimitiveType 1:9 type=string
              NameExpression 1:23 name=args
              Pipeline 1:28
                CallStep 1:31 gear=add
                  BinaryExpression 1:35 op=+
                    LiteralExpression 1:35 kind=IntegerLiteral text=1
                    NameExpression 1:39 name=n
                  Rail 2:5
                    CallStep 2:9 gear=printUsage
                    FailStep 2:23
                      LiteralExpression 2:28 kind=StringLiteral text="x"
                ExitCodeStep 2:36
                  LiteralExpression 2:38 kind=IntegerLiteral text=0

            """.ReplaceLineEndings("\n"), AstPrinter.Print(train));
    }

    [Fact]
    public void Les_noeuds_manquants_restent_visibles()
    {
        var source = new SourceText("main.rgl", "train |> ;");
        var missing = new SourceSpan(source, 9u, 0u);
        var pipeline = new Pipeline(At(source, "|>"), [new MissingStep(missing)]);
        var parameter = new Parameter(missing, new MissingType(missing), new Identifier(missing));

        Assert.Equal("""
            Pipeline 1:7
              MissingStep 1:10

            """.ReplaceLineEndings("\n"), AstPrinter.Print(pipeline));
        Assert.Equal("""
            Parameter 1:10 name=<manquant>
              MissingType 1:10

            """.ReplaceLineEndings("\n"), AstPrinter.Print(parameter));
    }

    [Fact]
    public void Un_template_sur_plusieurs_lignes_reste_sur_une_ligne()
    {
        var source = new SourceText("main.rgl", "`a\nb`");
        var literal = new LiteralExpression(At(source, "`a\nb`"), TokenKind.TemplateStringLiteral);

        Assert.Equal("LiteralExpression 1:1 kind=TemplateStringLiteral text=`a\\nb`\n", AstPrinter.Print(literal));
    }
}
