using System.Text.RegularExpressions;
using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Tests.Binding;

/// <summary>Tests du binder : IR produit pour un train valide, et erreurs sémantiques.</summary>
public class BinderTests
{
    /// <summary>Lexe, parse et lie <paramref name="content"/> ; la syntaxe doit être correcte.</summary>
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Code)));

        var program = new Binder(diagnostics).Bind(unit);
        return (program, diagnostics.Items);
    }

    /// <summary>IR de <paramref name="content"/>, en vérifiant qu'aucune erreur n'est signalée.</summary>
    private static IrEntryTrain Valid(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
        return program.Entry;
    }

    /// <summary>Codes des erreurs de <paramref name="content"/>, avec leur position ; aucun IR ne doit être produit.</summary>
    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error)
            .Select(d => $"{d.Code ?? "-"} {d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];
    }

    /// <summary>
    /// Un programme dont <paramref name="body"/> est le corps d'un gear de test : variables, if,
    /// switch… s'écrivent dans un gear. Le corps commence à la ligne 2.
    /// </summary>
    private static string InGear(string body) => "gear test(): void {\n" + body + "\n}\ntrain |> done;";

    /// <summary>Position ramenée à celle du corps du gear de test : sa première ligne devient la ligne 1.</summary>
    private static string _InBody(string diagnostic) =>
        Regex.Replace(diagnostic, @" (\d+):(\d+)$", m => $" {int.Parse(m.Groups[1].Value) - 1}:{m.Groups[2].Value}");

    /// <summary>Erreurs de <paramref name="body"/> placé dans un gear, positions relatives au corps.</summary>
    private static string[] GearErrors(string body) => [.. Errors(InGear(body)).Select(_InBody)];

    /// <summary>Diagnostics de <paramref name="body"/> placé dans un gear, positions relatives au corps.</summary>
    private static string[] GearAll(string body) => [.. All(InGear(body)).Select(_InBody)];

    /// <summary>Les instructions du gear de test, liées ; aucun diagnostic ne doit être signalé.</summary>
    private static IReadOnlyList<IrStatement> GearBody(string body)
    {
        var (program, diagnostics) = Bind(InGear(body));
        Assert.Empty(diagnostics);
        return Assert.Single(program!.Gears).Body.Statements;
    }

    #region Programmes valides

    [Fact]
    public void La_demo_V0_donne_un_train_qui_termine_par_done()
    {
        var entry = Valid("train |> done;");

        Assert.Null(entry.Arguments);
        Assert.Null(entry.StartValue);
        Assert.IsType<IrDone>(entry.Main.End);
        Assert.Empty(entry.Main.Steps);
    }

    [Fact]
    public void Le_parametre_des_arguments_devient_un_symbole_partage_par_ses_references()
    {
        var entry = Valid("!train([string] args) args |> $ 0;");

        Assert.NotNull(entry.Arguments);
        Assert.Equal("args", entry.Arguments.Name);
        Assert.Equal(new ArrayType(PrimitiveType.String), entry.Arguments.Type);

        var start = Assert.IsType<IrParameterReference>(entry.StartValue);
        Assert.Same(entry.Arguments, start.Parameter);
    }

    [Fact]
    public void Un_litteral_entier_positif_est_converti_en_code_de_sortie_uint()
    {
        var exit = Assert.IsType<IrExit>(Valid("!train |> $ 3;").Main.End);

        var conversion = Assert.IsType<IrConversion>(exit.Code);
        Assert.Equal(PrimitiveType.UInt, conversion.Type);
        Assert.Equal(3, Assert.IsType<IrIntegerConstant>(conversion.Operand).Value);
    }

    [Fact]
    public void Fail_envoie_une_exception_de_son_message()
    {
        var entry = Valid("!train |> fail \"oups\";");

        var fail = Assert.IsType<IrFail>(entry.Main.End);
        var exception = Assert.IsType<IrMoldValue>(fail.Error);
        Assert.Equal("Exception", exception.Mold.Symbol.Name);
        Assert.Equal("oups", Assert.IsType<IrStringConstant>(exception.Fields[0]).Value);
    }

    [Fact]
    public void Les_operations_sont_typees()
    {
        var declaration = Assert.IsType<IrVariableDeclaration>(Assert.Single(GearBody("var ok: 1 + 2 * 3 > 4 and not false;")));

        var and = Assert.IsType<IrBinary>(declaration.Value);
        Assert.Equal((IrBinaryOperator.And, (IrType)PrimitiveType.Bool), (and.Operator, and.Type));
        var greater = Assert.IsType<IrBinary>(and.Left);
        Assert.Equal(IrBinaryOperator.Greater, greater.Operator);
        Assert.Equal(PrimitiveType.Int, Assert.IsType<IrBinary>(greater.Left).Type);
    }

    [Theory]
    [InlineData("\"a\\n\\t\\\"b\\\\\"", "a\n\t\"b\\")]
    [InlineData("\"\\u{E9}t\\u{1F600}\"", "ét😀")]
    [InlineData("\"\\x41\"", "A")]
    [InlineData("'l''eau'", "l'eau")]
    [InlineData("'C:\\chemin'", "C:\\chemin")]
    public void Les_chaines_sont_decodees(string literal, string expected)
    {
        var fail = Assert.IsType<IrFail>(Valid($"!train |> fail {literal};").Main.End);

        Assert.Equal(expected, Assert.IsType<IrStringConstant>(Assert.IsType<IrMoldValue>(fail.Error).Fields[0]).Value);
    }

    [Theory]
    [InlineData("1_000", 1000)]
    [InlineData("0xFF", 255)]
    [InlineData("0b1010", 10)]
    [InlineData("0o17", 15)]
    [InlineData("2147483647", 2147483647)]
    public void Les_entiers_sont_decodes(string literal, long expected)
    {
        var declaration = Assert.IsType<IrVariableDeclaration>(Assert.Single(GearBody($"var n: {literal};")));

        Assert.Equal(expected, Assert.IsType<IrIntegerConstant>(declaration.Value).Value);
    }

    [Fact]
    public void Un_train_minimal_peut_aussi_porter_le_point_d_exclamation()
    {
        Valid("!train |> done;");
        Valid("train() |> done;"); // parenthèses vides : aucun argument, comme sans parenthèses
    }

    #endregion

    #region Erreurs

    [Fact]
    public void Un_fichier_sans_train_n_a_pas_de_train_d_entree()
    {
        Assert.Equal(["E036 1:1"], Errors("// rien\n"));
    }

    [Theory]
    [InlineData("!train(int n) |> done;", "E015 1:8")]
    [InlineData("!train([string] a, [string] b) |> done;", "E015 1:8")]
    [InlineData("!train(string args) |> done;", "E015 1:8")]
    public void Une_autre_signature_que_celle_du_train_d_entree_est_refusee(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Fact]
    public void Un_train_sans_guard_doit_avoir_son_point_d_exclamation()
    {
        Assert.Equal(["E037 1:1"], Errors("train |> $ 0;"));
        Assert.Equal(["E037 1:1"], Errors("train([string] args) |> done;"));
    }

    [Fact]
    public void Un_gear_inconnu_est_signale()
    {
        Assert.Equal(["E035 1:11"], Errors("!train |> run |> done;"));
        Assert.Equal(["E035 1:18"], Errors("gear g(): int => h(); train |> done;"));
        Assert.Equal(["E035 1:16"], Errors("!train |> fail f(1);"));
    }

    [Fact]
    public void Un_chemin_doit_se_terminer_et_rien_ne_doit_suivre_sa_fin()
    {
        Assert.Equal(["E039 1:19"], Errors("!train |> done |> done;"));
        Assert.Equal(["E039 4:26"], Errors("guard h(int n): int {\n    fail;\n}\ntrain 1 |> h !> [done |> done] |> done;"));
    }

    [Theory]
    [InlineData("!train |> $ \"x\";", "E040 1:13")]           // pas un entier
    [InlineData("!train |> $ -1;", "E040 1:13")]              // négatif : pas un littéral convertible
    [InlineData("!train |> $ 1.5;", "E040 1:13")]             // double vers uint : perte possible
    [InlineData("!train |> fail 1 + true;", "E040 1:16")]
    [InlineData("gear f(ulong a, long b): bool => a + b > 0; train |> done;", "E040 1:34")] // aucun type commun sans perte
    [InlineData("!train |> fail not 1;", "E040 1:16")]
    [InlineData("!train |> fail 9223372036854775808;", "E040 1:16")] // trop grand même pour un long
    public void Les_types_incompatibles_sont_signales(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Fact]
    public void Un_nom_inconnu_est_signale()
    {
        Assert.Equal(["E041 1:8"], Errors("!train x |> done;"));
        Assert.Equal(["E041 1:36"], Errors("!train([string] args) args |> fail argv;"));
    }

    [Fact]
    public void Un_echappement_inconnu_est_signale_a_sa_place()
    {
        Assert.Equal(["E042 1:18"], Errors("!train |> fail \"a\\qb\";"));
    }

    [Theory]
    [InlineData("const char C: 5;\n!train |> done;")]
    public void Un_char_hors_unsafe_est_E147(string content)
    {
        Assert.Equal(["E147 1:7"], Errors(content));
    }

    [Fact]
    public void Une_erreur_n_entraine_pas_d_erreurs_en_cascade()
    {
        // L'opérande inconnu est signalé une fois ; ni le « + », ni le « $ » n'en rajoutent.
        Assert.Equal(["E041 1:13"], Errors("!train |> $ y + 1;"));
    }

    #endregion

    #region Variables

    /// <summary>Diagnostics de <paramref name="content"/>, erreurs et avertissements, avec leur position.</summary>
    private static string[] All(string content) =>
        [.. Bind(content).Diagnostics.Select(d => $"{d.Code ?? "-"} {d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];

    [Fact]
    public void Les_constantes_du_fichier_precedent_le_train()
    {
        var (program, diagnostics) = Bind("const uint A: 3;\nconst uint B: A;\n!train |> $ B;");

        Assert.Empty(diagnostics);
        Assert.NotNull(program);
        Assert.Equal(["IrVariableDeclaration", "IrVariableDeclaration", "IrEntryTrain"], program.Statements.Select(s => s.GetType().Name));
    }

    [Theory]
    [InlineData("var x: null;", "E001 1:8")]                     // type impossible à déduire
    [InlineData("x <- 1;", "E002 1:1")]                          // variable inexistante
    [InlineData("undefine x;", "E002 1:10")]
    [InlineData("uint a: -1;", "E003 1:9")]             // seul un littéral positif devient uint
    [InlineData("int a: \"x\";", "E003 1:8")]
    [InlineData("var a: 1; a <- true;", "E003 1:16")]
    [InlineData("var a: 1; a: uint <- 2;", "E004 1:14")]         // type rappelé faux
    [InlineData("const int A: 1; A <- 2;", "E005 1:17")]         // constante
    [InlineData("var a: 1; undefine a; a <- 2;", "E006 1:23")]   // libérée
    [InlineData("var a: 1; undefine a; var b: a;", "E006 1:30")]
    [InlineData("var a: 1; var a: 2;", "E044 1:15")]             // déjà déclarée dans ce bloc
    [InlineData("var b: code; uint code: 1;", "E041 1:8")]             // visible seulement après sa déclaration
    [InlineData("{ var b: 1; }\nvar c: b;", "E041 2:8")]                       // morte à la fin de son bloc
    [InlineData("Profil p: 1;", "E041 1:1")]             // type inconnu
    public void Les_erreurs_sur_les_variables_sont_signalees_a_leur_place(string content, string expected)
    {
        Assert.Equal([expected], GearErrors(content));
    }

    [Theory]
    [InlineData("var a: 1;\n{\n    var a: 2;\n}", "W009 3:9")]  // masquage : un avertissement
    [InlineData("var a: 1;\nundefine a;\nvar a: 2;", "W001 3:5")] // redéclaration après undefine
    public void Masquer_ou_redeclarer_une_variable_donne_un_avertissement(string content, string expected)
    {
        Assert.Equal([expected], GearAll(content));
        Assert.NotNull(Bind(InGear(content)).Program);
    }

    [Fact]
    public void Une_constante_connue_a_la_compilation_garde_sa_valeur()
    {
        var (program, _) = Bind("const int A: 2 + 3;\nconst int B: A * 4;\nconst int C: A < 0 ? 1 : invert A;\nconst int D: 2147483647 + A;\ntrain |> done;");
        var values = program!.Statements.OfType<IrVariableDeclaration>().Select(d => d.Variable.CompileTimeValue);

        // Un débordement ne se calcule pas : la constante reste évaluée à l'exécution (et s'y arrête).
        Assert.Equal([5L, 20L, -6L, null], values);
    }

    #endregion

    #region Templates

    [Fact]
    public void Un_template_est_une_chaine_faite_de_texte_et_de_valeurs()
    {
        var statements = GearBody("var a: 1;\nvar r: 2.5;\nvar ok: true;\nvar t: `a = {a}, double = {r * 2.0}, ok = {ok}, \\{texte\\}\\n`;");

        var template = Assert.IsType<IrTemplate>(statements.OfType<IrVariableDeclaration>().Last().Value);
        Assert.Equal(PrimitiveType.String, template.Type);
        Assert.Equal(["a = ", "a", ", double = ", "(r * 2.0)", ", ok = ", "ok", ", {texte}\n"],
            template.Parts.Select(p => p switch
            {
                IrStringConstant text => text.Value,
                IrVariableReference reference => reference.Variable.Name,
                _ => "(" + p.Span.Text + ")",
            }));
    }

    [Theory]
    [InlineData("!train |> fail `a\\q`;", "E042 1:18")]                        // échappement inconnu
    [InlineData("!train |> fail `{x}`;", "E041 1:18")]                           // nom inconnu dans l'interpolation
    public void Les_erreurs_d_un_template_sont_signalees_a_leur_place(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    #endregion

    #region Gears

    [Theory]
    [InlineData("var a: 1; train |> done;", "E053 1:1")]                                                   // variable hors d'un gear
    [InlineData("if (true) { } train |> done;", "E053 1:1")]
    [InlineData("return 1; train |> done;", "E059 1:1")]                                                   // return hors d'un gear
    [InlineData("gear f(): int => 1; const int A: 1; train |> done;", "E054 1:31")]                       // constante après un gear
    [InlineData("gear f(): int => 1; gear f(): int => 2; train |> done;", "E055 1:26")]                   // deux gears de même nom
    [InlineData("gear f(int n): int { if (n > 0) { return 1; } } train |> done;", "E056 1:6")]            // un chemin sans return
    [InlineData("gear f(): int { return; } train |> done;", "E056 1:17")]                                 // return sans valeur
    [InlineData("gear f(): void { return 1; } train |> done;", "E040 1:25")]                              // valeur dans un gear void
    [InlineData("gear d(int n): int => n * 2; gear pure f(int n): int => d(n); train |> done;", "E057 1:57")] // pure appelle non pure
    [InlineData("gear f(int a, int b): int => a + b; gear g(): int => f(1); train |> done;", "E058 1:54")]   // nombre d'arguments
    [InlineData("gear f(int a): int => a; gear g(): int => 2 |> f(3); train |> done;", "E058 1:43")]       // le pipeline compte
    [InlineData("gear f(int n): int { n <- 1; return n; } train |> done;", "E005 1:22")]                  // un paramètre ne se modifie pas
    [InlineData("gear f(string s): int => s; train |> done;", "E003 1:26")]                               // type de retour
    [InlineData("gear f(int n): int => n; gear g(): int => f(\"x\"); train |> done;", "E003 1:45")]       // type d'argument
    [InlineData("gear log(): void { } gear g(): void { var x: log(); } train |> done;", "E040 1:46")]     // pas de valeur void
    public void Les_erreurs_des_gears_sont_signalees_a_leur_place(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Theory]
    [InlineData("gear fact(int n): int => n <= 1 ? 1 : n * fact(n - 1); train |> done;", "W010 1:6")]                  // récursif direct
    [InlineData("gear pair(int n): bool => n eq 0 ? true : impair(n - 1);\ngear impair(int n): bool => n eq 0 ? false : pair(n - 1);\ntrain |> done;",
        "W010 1:6,W010 2:6")]                                                                                         // récursion croisée
    [InlineData("gear f(int n): int { var n: 2; return n; } train |> done;", "W009 1:26")]                            // masque un paramètre
    public void Les_avertissements_des_gears_sont_signales(string content, string expected)
    {
        Assert.Equal(expected, string.Join(",", All(content)));
        Assert.NotNull(Bind(content).Program);
    }

    [Fact]
    public void Un_gear_peut_appeler_un_gear_ecrit_plus_bas_et_le_pipeline_passe_la_valeur_en_premier()
    {
        var (program, diagnostics) = Bind("gear calcul(): int => 3 |> twice |> add(1);\ngear twice(int n): int => n * 2;\ngear pure add(int a, int b): int => a + b;\ntrain |> done;");

        Assert.Empty(diagnostics);
        var calcul = program!.Gears[0];
        var add = Assert.IsType<IrCall>(Assert.IsType<IrReturn>(calcul.Body.Statements[0]).Value);
        Assert.Equal("add", add.Gear.Name);
        var twice = Assert.IsType<IrCall>(add.Arguments[0]);                      // 3 |> twice entre en premier argument
        Assert.Equal(("twice", 3L), (twice.Gear.Name, Assert.IsType<IrIntegerConstant>(twice.Arguments[0]).Value));
        Assert.Equal(1L, Assert.IsType<IrIntegerConstant>(add.Arguments[1]).Value);
        Assert.True(add.Gear.IsPure);
        Assert.False(calcul.Symbol.IsRecursive);
    }

    [Fact]
    public void Le_train_passe_sa_valeur_de_depart_au_premier_gear()
    {
        var (program, diagnostics) = Bind("gear compte([string] a): void { }\n!train([string] args) args |> compte |> $ 0;");

        Assert.Empty(diagnostics);
        var call = Assert.Single(program!.Entry.Main.Steps).Call;
        Assert.Equal("compte", call.Gear.Name);
        Assert.IsType<IrPipelineValue>(call.Arguments[0]);
    }

    [Fact]
    public void Les_constantes_du_fichier_sont_visibles_dans_les_gears()
    {
        Assert.Empty(All("const int MAX: 10;\ngear borne(int n): int => n > MAX ? MAX : n;\ntrain |> done;"));
    }

    #endregion

    #region Contrôle de flux

    [Theory]
    [InlineData("if (1) { }", "E040 1:5")]                                              // condition pas un bool
    [InlineData("var a: 1; switch (post_increment a) { default: break; }", "E050 1:19")] // valeur impure
    [InlineData("var a: 1; switch (1) { case a: break; }", "E051 1:29")]                  // case pas constant
    [InlineData("switch (1) { case 1: break; case 1: break; }", "E052 1:34")]           // doublon
    [InlineData("switch (1) { default: break; default: break; }", "E052 1:30")]         // second default
    [InlineData("switch (\"a\") { case 1: break; }", "E040 1:21")]                     // pas comparable
    [InlineData("break;", "E049 1:1")]                                                  // hors d'un switch
    [InlineData("goto 1;", "E049 1:1")]
    [InlineData("switch (1) { case 1: goto 3; }", "E007 1:27")]                         // cas inexistant
    [InlineData("switch (1) { case 1: goto 3; case eval $ > 2: break; }", "E008 1:27")] // seul un case eval pourrait
    [InlineData("var a: 1; switch (1) { case 1: goto a; }", "E051 1:37")]                // goto vers une valeur calculée
    [InlineData("var x: $;", "E048 1:8")]                                               // $ hors d'un case eval
    public void Les_erreurs_du_controle_de_flux_sont_signalees_a_leur_place(string content, string expected)
    {
        Assert.Equal([expected], GearErrors(content));
    }

    [Theory]
    [InlineData("switch (1) { case 1: break 2; }", "W002 1:28")]                        // trop profond : sort de tous
    [InlineData("var r: 0; switch (1) { case 1: r <- 1; case 2: break; }", "W003 1:24")] // poursuite dans le cas suivant
    [InlineData("var r: 0; switch (1) { case 1: if (r eq 0) { break; } case 2: break; }", "W003 1:24")] // if sans else : peut continuer
    public void Les_avertissements_du_switch_sont_signales(string content, string expected)
    {
        Assert.Equal([expected], GearAll(content));
        Assert.NotNull(Bind(InGear(content)).Program);
    }

    [Theory]
    [InlineData("switch (1) { case 1: case 2: break; }")]                                // case empilés : pas de W003
    [InlineData("switch (1) { case 1: goto 2; case 2: break; }")]                        // fini par goto
    [InlineData("switch (1) { case 1: { var x: 1; } break; default: break; }")]
    [InlineData("var c: 0; switch (true) { case true: increment c; if (c < 3) { goto true; } else { goto false; } case false: default: break; }")] // fausse boucle
    [InlineData("var c: 0; switch (1) { case 1: { c <- 1; break; } case 2: break; }")]   // bloc fini par break
    public void Un_cas_vide_ou_fini_par_break_ou_goto_n_avertit_pas(string content)
    {
        Assert.Empty(GearAll(content));
    }

    [Fact]
    public void Chaque_cas_a_sa_propre_portee()
    {
        // x meurt à la fin du cas 1 : dans le cas 2, elle n'existe pas.
        Assert.Equal(["E002 1:40"], GearErrors("switch (1) { case 1: var x: 5; case 2: x <- 6; break; }"));
    }

    #endregion

    #region Opérateurs

    [Theory]
    [InlineData("const int A: 1; increment A;", "E005 1:27")]           // constante
    [InlineData("var s: \"x\"; increment s;", "E040 1:23")]             // pas un nombre
    [InlineData("increment 5;", "E040 1:11")]                           // pas une variable
    [InlineData("var a: 1; increment<0> a;", "E046 1:21")]              // pas d'au moins 1
    [InlineData("ubyte b: 1; increment<300> b;", "E046 1:23")]  // ne tient pas dans un ubyte
    [InlineData("x <- 1;", "E002 1:1")]
    [InlineData("var a: 1; var b: a / 0;", "E047 1:22")]                // diviseur nul connu
    [InlineData("var a: 1; var b: a % (2 - 2);", "E047 1:23")]
    [InlineData("var b: invert \"x\";", "E040 1:8")]
    [InlineData("var b: 1 ? 2 : 3;", "E040 1:8")]                       // condition pas un bool
    [InlineData("var b: true ? 1 : \"x\";", "E040 1:8")]                // pas de type commun
    [InlineData("var a: 1; a +<- 1.5;", "E003 1:11")]                   // int + double : double, pas un int
    public void Les_erreurs_des_operateurs_sont_signalees_a_leur_place(string content, string expected)
    {
        Assert.Equal([expected], GearErrors(content));
    }

    [Fact]
    public void Increment_garde_son_sens_son_pas_et_sa_forme()
    {
        var statements = GearBody("var a: 1;\nincrement a;\nvar b: post_decrement<3> a;");

        var statement = Assert.IsType<IrExpressionStatement>(statements[1]);
        Assert.Equal((false, false, 1L), Assert.IsType<IrIncrement>(statement.Expression) is var i ? (i.IsDecrement, i.IsPost, i.Step) : default);
        var post = Assert.IsType<IrIncrement>(statements.OfType<IrVariableDeclaration>().Last().Value);
        Assert.Equal((true, true, 3L), (post.IsDecrement, post.IsPost, post.Step));
    }

    [Fact]
    public void Une_affectation_composee_calcule_puis_affecte()
    {
        var statements = GearBody("uint code: 40;\ncode +<- 2;");

        var assignment = Assert.IsType<IrAssignment>(statements[1]);
        var sum = Assert.IsType<IrBinary>(assignment.Value);
        Assert.Equal((IrBinaryOperator.Add, PrimitiveType.UInt), (sum.Operator, sum.Type));
    }

    [Fact]
    public void Un_ternaire_et_invert_se_calculent_a_la_compilation()
    {
        var (program, _) = Bind("const int A: 3;\nconst int B: A > 2 ? A * 2 : 0;\nconst int C: invert 0b1010;\nconst bool D: invert true;\ntrain |> done;");

        Assert.Equal([3L, 6L, -11L, false], program!.Statements.OfType<IrVariableDeclaration>().Select(d => d.Variable.CompileTimeValue));
    }

    #endregion

    #region Symboles

    [Fact]
    public void Un_symbole_se_declare_se_compare_et_s_ecrit_dans_un_template()
    {
        var declarations = GearBody("var monSymbol: §mykey;\nconst symbol K: §mykey;\nvar egal: §mykey eq monSymbol;\nvar t: `clé = {K}`;")
            .OfType<IrVariableDeclaration>().ToArray();
        Assert.Equal(PrimitiveType.Symbol, declarations[0].Variable.Type);
        Assert.Equal("mykey", Assert.IsType<IrSymbolConstant>(declarations[0].Value).Name);
        Assert.True(declarations[1].Variable.IsCompileTime);                  // un symbole littéral est connu à la compilation
        Assert.Equal(PrimitiveType.Bool, declarations[2].Value.Type);
    }

    [Fact]
    public void Deux_symboles_constants_se_comparent_a_la_compilation()
    {
        var (program, _) = Bind("const bool A: §x eq §x;\nconst bool B: §x eq §y;\ntrain |> done;");

        Assert.Equal([true, false], program!.Statements.OfType<IrVariableDeclaration>().Select(d => d.Variable.CompileTimeValue));
    }

    [Theory]
    [InlineData("var s: §a + §b;", "E040 1:8")]                 // pas d'arithmétique sur les symboles
    [InlineData("var s: §a eq \"a\";", "E040 1:8")]            // un symbole n'est pas une chaîne
    [InlineData("symbol s: \"a\";", "E003 1:11")]
    public void Un_symbole_ne_se_melange_pas_aux_autres_types(string content, string expected)
    {
        Assert.Equal([expected], GearErrors(content));
    }

    #endregion

    #region Guards, emits et console

    [Fact]
    public void Un_guard_et_un_emit_s_enchainent_dans_le_train()
    {
        var entry = Valid("guard positif(int n): int {\n    if (n < 0) {\n        fail \"négatif\";\n    }\n    done;\n}\n"
            + "emit afficher(int n): void {\n    n -> console;\n}\ntrain 5 |> positif !> [$ 2] |> afficher;");

        var step = Assert.Single(entry.Main.Steps);
        Assert.Equal(GearKind.Guard, step.Call.Gear.Kind);
        Assert.IsType<IrPipelineValue>(step.Call.Arguments[0]);
        var emit = Assert.IsType<IrEmit>(entry.Main.End);
        Assert.Equal("afficher", emit.Call.Gear.Name);
        Assert.IsType<IrExit>(step.Rail!.End);
    }

    [Fact]
    public void Done_renvoie_le_premier_parametre_du_guard()
    {
        var (program, diagnostics) = Bind("guard positif(int n): int {\n    done;\n}\ntrain 1 |> positif |> done;");

        Assert.Empty(diagnostics);
        var done = Assert.IsType<IrReturn>(Assert.Single(Assert.Single(program!.Gears).Body.Statements));
        Assert.Equal("n", Assert.IsType<IrParameterReference>(done.Value).Parameter.Name);
    }

    [Fact]
    public void Le_rail_recoit_l_erreur_de_son_guard()
    {
        var entry = Valid("guard g(int n): int {\n    fail;\n}\nemit signaler(Exception e): uint {\n    `erreur : {e}` -> console;\n    return 2;\n}\ntrain 1 |> g !> [signaler] |> done;");

        var emit = Assert.IsType<IrEmit>(Assert.Single(entry.Main.Steps).Rail!.End);
        Assert.Equal("Exception", Assert.IsType<IrPipelineValue>(emit.Call.Arguments[0]).Type.ToString());
    }

    [Theory]
    [InlineData("gear g(): void {\n    fail;\n}\ntrain |> done;", "E060 2:5")]                              // fail hors d'un guard
    [InlineData("gear g(int n): int {\n    done;\n}\ntrain |> done;", "E060 2:5")]                          // done hors d'un guard
    [InlineData("gear g(string s): int => s as! int;\ntrain |> done;", "E060 1:26")]                         // as! hors d'un guard
    [InlineData("emit e(): void {\n    fail;\n}\n!train |> e;", "E060 2:5")]                                 // un emit n'échoue pas
    [InlineData("guard ok(int n): int {\n    done;\n}\ngear g(): int => ok(1);\ntrain |> done;", "E062 4:18")]      // guard dans une expression
    [InlineData("emit e(): void {\n}\ngear g(): void {\n    e();\n}\ntrain |> done;", "E062 4:5")]                  // emit dans une expression
    [InlineData("emit afficher(): void {\n}\n!train |> afficher |> done;", "E063 3:23")]                    // étape après un emit
    [InlineData("guard ok(int n): int {\n    done;\n}\n!train 1 |> ok |> done;", "E064 4:1")]              // « ! » avec un guard
    [InlineData("emit e(): void {\n}\ntrain |> e;", "E037 3:1")]                                         // sans guard, « ! » obligatoire
    [InlineData("guard g(int n): string {\n    done;\n}\ntrain 1 |> g |> done;", "E065 2:5")]            // done : autre type que le paramètre
    [InlineData("guard g(): int {\n    done;\n}\ntrain |> g |> done;", "E065 2:5")]                       // done sans paramètre
    [InlineData("gear pure g(int n): int {\n    n -> console;\n    return n;\n}\ntrain |> done;", "E066 2:5")]     // écriture dans un gear pure
    [InlineData("gear pure g(): string {\n    var l <- console;\n    return l;\n}\ntrain |> done;", "E066 2:14")]   // lecture dans un gear pure
    [InlineData("emit e(): int {\n    return 1;\n}\n!train |> e;", "E067 4:11")]                         // emit qui ne donne pas de code de sortie
    [InlineData("guard g(int n): string {\n    if (n < 0) {\n        fail;\n    }\n}\ntrain 1 |> g |> done;", "E056 1:7")] // guard qui peut finir sans done, d'un autre type
    [InlineData("guard g(int n): int {\n    fail 42;\n}\ntrain 1 |> g |> done;", "E091 2:10")]               // fail : un texte ou une erreur
    [InlineData("!train |> fail 42;", "E091 1:16")]
    [InlineData("guard h(int n): int {\n    fail;\n}\ngear g(int n): void {\n}\ntrain 1 |> h !> [g |> done] |> done;", "E003 6:18")] // le rail reçoit l'erreur du guard
    [InlineData("const string L <- console;\ntrain |> done;", "E053 1:19")]                                // lecture au niveau du fichier
    [InlineData("train |> inconnu |> done;", "E035 1:10")]                                                  // pas de E037 en plus
    public void Les_regles_des_guards_et_des_emits_sont_verifiees(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Theory]
    [InlineData("1 -> ecran;", "E041 1:6")]                     // cible inconnue
    [InlineData("1 -> file;", "- 1:6")]                         // format qui viendra plus tard
    public void Les_cibles_inconnues_ou_pas_encore_prises_en_charge_sont_signalees(string content, string expected)
    {
        Assert.Equal([expected], GearErrors(content));
    }

    [Theory]
    [InlineData("switch (1) {\n    case 1:\n        return;\n    default:\n        break;\n}")]   // return quitte le cas
    [InlineData("var s: `{1}`;\n`{s} {2.5} {true} {§x}` -> console;")]                          // tout ce qui s'écrit en texte
    public void Ces_corps_sont_acceptes_sans_diagnostic(string body)
    {
        Assert.Empty(GearAll(body));
    }

    [Fact]
    public void Un_break_inaccessible_apres_return_ne_fait_pas_sortir_du_switch()
    {
        // Le break après return ne s'exécute jamais : chaque chemin renvoie bien un bool (pas de E056).
        Assert.Empty(All("gear estSymbole(string s): bool {\n    switch (s) {\n        case \"+\":\n            return true;\n            break;\n"
            + "        default:\n            return false;\n            break;\n    }\n}\ntrain |> done;"));
    }

    [Fact]
    public void Une_exception_s_ecrit_dans_un_template_et_sur_la_console()
    {
        Assert.Empty(All("gear g(Exception e): string {\n    e -> console;\n    return `{e}`;\n}\ntrain |> done;"));
    }

    #endregion

    #region Molds

    /// <summary>Deux molds de test, placés avant le gear de test : le corps commence toujours à la ligne 2 de celui-ci.</summary>
    private const string Molds = "mold Vec2 { double x; double y; }\nmold Vec3 { ...Vec2; double z: 0.0; }\n";

    [Theory]
    [InlineData("v.n", "string")]           // même type dans les deux molds
    [InlineData("v.x", "int | string")]     // types différents : leur union
    public void Un_champ_commun_a_tous_les_molds_d_une_union_se_lit_directement(string access, string expected)
    {
        var (program, diagnostics) = Bind($"mold A {{ string n; int x; }}\nmold B {{ string n; string x; bool b; }}\ngear g(A | B v): void {{\n    var r: {access};\n}}\ntrain |> done;");

        Assert.Empty(diagnostics);
        var declaration = Assert.IsType<IrVariableDeclaration>(Assert.Single(Assert.Single(program!.Gears).Body.Statements));
        Assert.Equal(expected, Assert.IsType<IrUnionField>(declaration.Value).Type.ToString());
    }

    [Theory]
    [InlineData("mold A { int x; }\nmold B { int y; }\ngear g(A | B v): int => v.x;\ntrain |> done;", "E074 3:27")]   // absent d'un membre
    [InlineData("mold A { int x; }\ngear g(A | int v): int => v.x;\ntrain |> done;", "E074 2:29")]                    // un membre n'est pas un mold
    public void Un_champ_absent_d_un_membre_de_l_union_est_signale(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    /// <summary>Deux molds dont l'un a plus de champs, et leurs versions avec un id.</summary>
    private const string Bestiary = "mold C { string n; }\nmold B { ...C; int w; }\nmold IC { ...C; uint id; }\nmold IB { ...B; uint id; }\n";

    [Fact]
    public void Les_bras_d_un_match_vont_directement_dans_le_type_attendu_sans_perdre_de_champ()
    {
        var (program, diagnostics) = Bind(Bestiary + "gear g(B | C v): IB | IC => match (v) {\n    B b |> ({...b, id: 1});\n    C c |> ({...c, id: 2});\n};\ntrain |> done;");

        Assert.Empty(diagnostics);
        var match = Assert.IsType<IrMatch>(Assert.IsType<IrReturn>(Assert.Single(Assert.Single(program!.Gears).Body.Statements)).Value);
        Assert.Equal("IB | IC", match.Type.ToString());
        Assert.Equal("IB", ((UnionType)match.Type).Members[Assert.IsType<IrToUnion>(match.Arms[0].Value).Index].ToString());   // w gardé
    }

    [Theory]
    [InlineData("gear g(B | C v): void {\n    var r: match (v) {\n        B b |> ({...b, id: 1});\n        C c |> ({...c, id: 2});\n    };\n}", "E040 8:17")] // sans type attendu : pas de perte silencieuse
    [InlineData("gear g(bool o, B b, C c): void {\n    var r: o ? b : c;\n}", "E040 6:12")]
    public void Deux_molds_ne_s_unifient_pas_en_perdant_des_champs(string gear, string expected)
    {
        Assert.Equal([expected], Errors(Bestiary + gear + "\ntrain |> done;"));
    }

    [Fact]
    public void Un_ternaire_prend_le_type_attendu()
    {
        Assert.Empty(All(Bestiary + "gear g(bool o, B b, C c): B | C {\n    B | C r: o ? b : c;\n    return r;\n}\ntrain |> done;"));
    }

    [Fact]
    public void Un_champ_nullable_sans_defaut_vaut_null_et_peut_etre_omis()
    {
        var (program, diagnostics) = Bind("mold M { string n; }\nmold C { uint atk; M? m1; M? m2; }\nconst M A: {n: \"a\"};\nconst C X: {atk: 2, m1: A};\ntrain |> done;");

        Assert.Empty(diagnostics);
        var value = Assert.IsType<ConstantFolder.MoldValue>(program!.Statements.OfType<IrVariableDeclaration>().Last().Variable.CompileTimeValue);
        Assert.Equal(ConstantFolder.NullValue.Instance, value.Fields[2]);   // m2 : null, et la constante reste connue
    }

    [Fact]
    public void Le_champ_signale_est_celui_qui_manque_pas_une_constante_qui_s_adapte()
    {
        var (_, diagnostics) = Bind("mold C { uint atk; string nom; }\nconst C X: {atk: 2};\ntrain |> done;");

        var error = Assert.Single(diagnostics);
        Assert.Contains("il manque le champ « nom »", string.Join(" ", error.Help!));
    }

    /// <summary>Erreurs de <paramref name="body"/> dans un gear, après les molds de test ; positions relatives au corps.</summary>
    private static string[] MoldErrors(string body) =>
        [.. Errors(Molds + InGear(body)).Select(d => Regex.Replace(d, @" (\d+):(\d+)$", m => $" {int.Parse(m.Groups[1].Value) - 3}:{m.Groups[2].Value}"))];

    /// <summary>Tous les diagnostics de <paramref name="body"/> dans un gear, après les molds de test.</summary>
    private static string[] MoldAll(string body) => All(Molds + InGear(body));

    [Theory]
    [InlineData("Vec2 a: {x: 1.0, y: 2.0};")]                          // littéral pris par le type déclaré
    [InlineData("var a: new Vec3 {x: 1, y: 2};")]                                // constantes adaptées, z par défaut
    [InlineData("Vec3 a: {x: 1.0, y: 2.0, z: 3.0};\nVec2 b: a;")]  // un champ en plus : laissé (comme TypeScript)
    [InlineData("var a: {x: 1.0, y: 2.0};\nVec3 b: a;")]                 // un champ en moins avec défaut
    [InlineData("var a: {x: 1, y: 2};\nvar b: {y: 3, x: 4};\na <- b;")]          // même forme, même type, quel que soit l'ordre
    [InlineData("Vec2 a: {x: 1.0, y: 2.0};\nvar b: a eq {x: 1.0, y: 2.0};")] // égalité champ par champ
    [InlineData("var x: 1.0;\nvar y: 2.0;\nVec2 a: {x, y};\nvar b: {...a, x: 3.0};\nvar c: b.x + a.y;")] // raccourci, spread, accès
    [InlineData("Vec2 a: {x: 1.0, y: 2.0};\n`{a}` -> console;")]       // un mold s'écrit en texte
    public void Les_molds_et_le_duck_typing_sont_acceptes(string body)
    {
        Assert.Empty(MoldAll(body));
    }

    [Theory]
    [InlineData("Vec2 a: {x: 1.0};", "E003 1:9")]                       // y manque, sans défaut
    [InlineData("Vec2 a: {x: 1.0, y: 2.0, w: 3.0};", "E003 1:9")]      // champ inconnu dans un littéral
    [InlineData("Vec2 a: {x: \"1\", y: 2.0};", "E003 1:9")]             // champ du mauvais type
    [InlineData("var a: {x: 1.0, x: 2.0};", "E071 1:17")]                         // champ en double dans une valeur
    [InlineData("Vec2 a: {x: 1.0, y: 2.0};\na.x <- 3.0;", "E013 2:1")]   // modification en place
    [InlineData("Vec2 a: {x: 1.0, y: 2.0};\nvar b: a.w;", "E074 2:10")]  // champ inconnu
    [InlineData("var a: 1;\nvar b: a.x;", "E074 2:10")]                           // « . » sur un int
    [InlineData("var a: {...1};", "E040 1:12")]                                   // spread d'autre chose qu'un mold
    [InlineData("var a: new int {x: 1};", "E040 1:12")]                           // new sur autre chose qu'un mold
    [InlineData("var a: {x: null};", "E001 1:12")]                                // type d'un champ impossible à déduire
    [InlineData("Inconnu a: {x: 1};", "E041 1:1")]
    public void Les_erreurs_de_valeurs_de_molds_sont_signalees(string body, string expected)
    {
        Assert.Equal([expected], MoldErrors(body));
    }

    [Theory]
    [InlineData("mold A { int x; double x; }\ntrain |> done;", "E071 1:24")]                      // champ en double
    [InlineData("mold A { int x; }\nmold B { ...A; int x; }\ntrain |> done;", "E071 2:20")]     // en double par le spread
    [InlineData("gear f({int x, int x} v): void { }\ntrain |> done;", "E071 1:20")]               // en double dans un type anonyme
    [InlineData("gear n(): int => 1;\nmold A { int x: n(); }\ntrain |> done;", "E072 2:17")]    // défaut calculé à l'exécution
    [InlineData("mold A { int x: \"a\"; }\ntrain |> done;", "E003 1:17")]                        // défaut du mauvais type
    [InlineData("mold A { A suivant; }\ntrain |> done;", "E073 1:6")]                              // se contient lui-même
    [InlineData("mold A { B b; }\nmold B { A a; }\ntrain |> done;", "E073 1:6")]                // par un autre mold
    [InlineData("mold A { ...B; }\nmold B { ...A; }\ntrain |> done;", "E073 1:6")]              // cycle de spreads
    [InlineData("mold A { int x; }\nmold A { int y; }\ntrain |> done;", "E075 2:6")]            // deux molds de même nom
    [InlineData("mold A { ...int; }\ntrain |> done;", "E040 1:13")]                               // spread d'un type qui n'est pas un mold
    public void Les_erreurs_de_declaration_de_molds_sont_signalees(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Fact]
    public void Un_mold_peut_utiliser_un_mold_declare_plus_bas_et_une_constante_du_fichier()
    {
        Assert.Empty(All("const double Z: 1.5;\nmold B { A a; double z: Z * 2.0; }\nmold A { int x; }\ngear f(B b): double => b.z;\ntrain |> done;"));
    }

    [Fact]
    public void Une_constante_de_mold_est_connue_a_la_compilation()
    {
        var (program, diagnostics) = Bind("mold P { int x; string s: \"a\"; }\nconst P A: {x: 1};\nconst bool B: A eq {x: 1, s: \"a\"};\ntrain |> done;");

        Assert.Empty(diagnostics);
        var constants = program!.Statements.OfType<IrVariableDeclaration>().ToArray();
        Assert.True(constants[0].Variable.IsCompileTime);
        Assert.Equal(true, constants[1].Variable.CompileTimeValue);
    }

    [Fact]
    public void Une_exception_donne_son_message_et_sa_position()
    {
        Assert.Empty(All("guard h(): void {\n    fail;\n}\nemit e(Exception x): uint {\n    `{x.message} {x.where}` -> console;\n    return 1;\n}\ntrain |> h !> [e] |> done;"));
    }

    #endregion

    #region Nullables

    [Theory]
    [InlineData("int? a: null;\nint? b: 3;\nvar c: a ?? 0;")]                        // null et une valeur dans un T?, ??
    [InlineData("int? a: null;\nif (a not eq null) {\n    var b: a + 1;\n}")]               // rétréci dans le if
    [InlineData("int? a: null;\nif (a eq null) {\n    return;\n}\nvar b: a + 1;")]         // rétréci après une sortie
    [InlineData("int? a: null;\nvar b: a not eq null and a > 0;\nvar c: a eq null or a > 0;")] // à droite de and et de or
    [InlineData("int? a: null;\nvar b: a not eq null ? a : 0;")]                              // dans la branche du ternaire
    [InlineData("int? a: null;\na <- 5;\nvar b: a + 1;")]                                     // après une valeur non null
    [InlineData("string? s: null;\nvar t: s ?° \"défaut\";\nvar u: \"\" ?° \"x\";")]       // ?° sur une chaîne et son nullable
    [InlineData("var n: \"12\" as? int;\nvar m: n ?? 0;")]                                         // as? donne un T?
    [InlineData("int? a: null;\n`{a}` -> console;")]                                        // un nullable s'écrit (« null »)
    public void Les_nullables_et_le_retrecissement_sont_acceptes(string body)
    {
        Assert.Empty(GearAll(body));
    }

    [Theory]
    [InlineData("int a: null;", "E003 1:8")]                                                  // null hors d'un nullable
    [InlineData("int? a: null;\nvar b: a + 1;", "E040 2:8")]                                   // un T? n'est pas un T
    [InlineData("int? a: null;\nint b: a;", "E003 2:8")]
    [InlineData("int? a: 1;\na <- null;\nvar b: a + 1;", "E040 3:8")]                        // l'affectation fait oublier
    [InlineData("int? a: null;\nif (a eq null) {\n    return;\n} else {\n    a <- null;\n}\nvar b: a + 1;", "E040 7:8")] // modifiée dans la branche qui continue
    [InlineData("int? a: null;\nif (a eq null) {\n    return;\n}\nswitch (1) {\n    case 1:\n        var b: a + 1;\n        a <- null;\n}", "E040 7:16")] // modifiée dans le switch
    [InlineData("var a: 1 ?° 2;", "E040 1:8")]                                                         // ?° sur un nombre
    [InlineData("int? a: null;\nvar b: a ?? \"x\";", "E040 2:13")]                            // secours du mauvais type
    [InlineData("void? a: null;", "E077 1:1")]
    public void Les_erreurs_de_nullables_sont_signalees(string body, string expected)
    {
        Assert.Equal([expected], GearErrors(body));
    }

    [Theory]
    [InlineData("int a: 1;\nvar b: a ?? 0;", "W011 2:8")]                                     // jamais null
    [InlineData("int a: 1;\nvar b: a eq null;", "W011 2:8")]                                  // comparé à null : toujours faux
    [InlineData("mold P { int x; }\ngear g(P p): int => p?.x;\ntrain |> done;", "W011 2:21")]         // ?. sur un mold jamais null
    [InlineData("mold P { int x; }\ngear g(P p): P => p ?° {x: 1};\ntrain |> done;", "W011 2:19")]    // un mold qui a des champs n'est jamais vide
    public void Une_coalescence_inutile_avertit(string content, string expected)
    {
        var diagnostics = content.Contains("train") ? All(content) : GearAll(content);
        Assert.Equal([expected], diagnostics);
    }

    [Fact]
    public void Un_mold_ne_peut_pas_contenir_un_nullable_de_lui_meme()
    {
        Assert.Equal(["E073 1:6"], Errors("mold Noeud { int v; Noeud? suivant; }\ntrain |> done;"));
    }

    #endregion

    #region Unions, résultats et match

    [Theory]
    [InlineData("int | string a: 42;\nint | string b: \"x\";")]                    // un membre entre dans l'union
    [InlineData("string | int a: 1;\nint | string b: a;")]                          // l'ordre des membres ne compte pas
    [InlineData("int | string a: 1;\nint | string | bool b: a;\nvar c: a eq b;")]   // élargie, et comparable
    [InlineData("int ! string r: ok(1);\nint ! int s: err(2);\nint ! string t: 3;\nvar u: r ?? 0;")] // ok, err, implicite, ??
    [InlineData("int | string a: 1;\nvar b: match (a) {\n    int i |> i;\n    string s |> 0;\n};")]    // match valeur
    [InlineData("int | string a: 1;\nmatch (a) {\n    int i |> { return; }\n    default |> return;\n}")] // match instruction, default
    [InlineData("int ! string r: ok(1);\nvar t: match (r) {\n    ok v |> `{v}`;\n    err e |> e;\n};")]      // match d'un résultat
    public void Les_unions_et_les_resultats_sont_acceptes(string body)
    {
        Assert.Empty(GearAll(body));
    }

    [Theory]
    [InlineData("long | double a: 1;", "E003 1:18")]                                      // ambigu : long ou double
    [InlineData("int ! int r: 1;", "E003 1:14")]                                          // ambigu : succès ou erreur
    [InlineData("var r: ok(1);", "E001 1:8")]                                                     // le type de l'erreur manque
    [InlineData("int | string? a: 1;", "E078 1:7")]                                      // pas de nullable dans une union
    [InlineData("int | string a: 1;\nvar b: a + 1;", "E040 2:8")]                          // une union se résout par match
    [InlineData("int | string a: 1;\nvar b: match (a) {\n    bool x |> 0;\n    default |> 1;\n};", "E080 3:5")]   // type absent
    [InlineData("int | string a: 1;\nvar b: match (a) {\n    ok x |> 0;\n    default |> 1;\n};", "E080 3:5")]     // ok sur une union
    [InlineData("int ! string r: ok(1);\nvar b: match (r) {\n    int x |> 0;\n    default |> 1;\n};", "E080 3:5")] // type sur un résultat
    [InlineData("int | string a: 1;\nvar b: match (a) {\n    int x |> 0;\n    int y |> 1;\n    default |> 2;\n};", "E081 4:5")]
    [InlineData("int | string a: 1;\nvar b: match (a) {\n    int x |> 0;\n};", "E082 2:8")]          // string non couvert
    [InlineData("int | string a: 1;\nvar b: match (a) {\n    int x |> 0;\n    string s |> \"z\";\n};", "E040 4:17")] // pas de type commun
    public void Les_erreurs_d_unions_et_de_match_sont_signalees(string body, string expected)
    {
        Assert.Equal([expected], GearErrors(body));
    }

    [Fact]
    public void Un_gear_dont_chaque_bras_renvoie_ne_finit_pas_sans_valeur()
    {
        Assert.Empty(All("gear f(int | string v): int {\n    match (v) {\n        int i |> return i;\n        string s |> { return 0; }\n    }\n}\ntrain |> done;"));
    }

    #endregion

    #region Enums

    [Theory]
    [InlineData(1, "bool")]
    [InlineData(2, "bool")]
    [InlineData(3, "ubyte")]
    [InlineData(256, "ubyte")]
    [InlineData(257, "ushort")]
    public void Le_type_d_un_enum_sans_type_est_le_plus_petit_qui_suffit(int count, string expected)
    {
        var variants = string.Join(", ", Enumerable.Range(0, count).Select(i => $"V{i}"));
        var (program, diagnostics) = Bind($"enum E {{ {variants} }}\nconst E X: E.V0;\ntrain |> done;");

        Assert.Empty(diagnostics);
        var type = Assert.IsType<EnumType>(program!.Statements.OfType<IrVariableDeclaration>().Single().Variable.Type);
        Assert.Equal(expected, type.Symbol.Underlying.ToString());
    }

    [Theory]
    [InlineData("enum D { N, S }\ngear f(D d): int {\n    switch (d) {\n        case N:\n            return 0;\n        case D.S:\n            return 1;\n    }\n}\ntrain |> done;")] // court, qualifié, complet sans default
    [InlineData("enum S: int { A: 1, B: -1 }\nenum C: string { R: \"r\", V: \"v\" }\nenum F: double { X: 1.5, Y: 2.5 }\ntrain |> done;")]               // types écrits
    [InlineData("const int UN: 1;\nenum S: int { A: UN, B: UN + 1 }\ntrain |> done;")]                                                              // constantes du fichier
    [InlineData("enum D { N, S }\nmold M { D d: D.N; }\ngear f(M m): bool => m.d eq D.S;\ntrain |> done;")]                                         // dans un mold, eq
    public void Les_enums_sont_acceptes(string content)
    {
        Assert.Empty(All(content));
    }

    [Theory]
    [InlineData("enum E { }\ntrain |> done;", "E083 1:6")]                              // vide
    [InlineData("enum E { A, A }\ntrain |> done;", "E083 1:13")]                         // variante en double
    [InlineData("enum E: mstring { A: m\"a\" }\ntrain |> done;", "E083 1:9")]             // mstring : pas une constante
    [InlineData("enum E: int { A: 1, B }\ntrain |> done;", "E084 1:21")]                 // valeur manquante avec un type écrit
    [InlineData("enum E { A: 1 }\ntrain |> done;", "E084 1:13")]                          // valeur sans type écrit
    [InlineData("enum E: int { A: 1, B: 1 }\ntrain |> done;", "E084 1:24")]              // valeur en double
    [InlineData("enum E: ubyte { A: 300 }\ntrain |> done;", "E003 1:20")]                // hors du type
    [InlineData("gear n(): int => 1;\nenum E: int { A: n() }\ntrain |> done;", "E084 2:18")] // pas connue à la compilation
    [InlineData("enum E { A }\nmold E { int x; }\ntrain |> done;", "E075 2:6")]          // nom déjà pris
    [InlineData("enum E { A }\nconst E X: E.B;\ntrain |> done;", "E074 2:14")]          // variante inconnue
    [InlineData("enum E { A, B }\nconst E X: E.A;\nconst int Y: X + 1;\ntrain |> done;", "E040 3:14")] // pas d'arithmétique
    [InlineData("enum E { A, B }\nconst E X: A;\ntrain |> done;", "E041 2:12")]         // le nom court n'existe que dans un case
    public void Les_erreurs_d_enums_sont_signalees(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Fact]
    public void Un_switch_incomplet_sur_un_enum_sans_default_avertit()
    {
        Assert.Equal(["W012 2:29"], All("enum D { N, S, E }\ngear f(D d): void { switch (d) { case N: break; } }\ntrain |> done;"));
    }

    #endregion

    #region Conversions

    [Theory]
    [InlineData("uint code: 40;\ncode <- code + 2;")]        // la constante prend le type de l'autre opérande
    [InlineData("byte b: 5;\ndouble d: 1;")]        // une constante qui tient dans le type attendu
    [InlineData("float f: 0.1;")]                             // flottant : arrondi accepté
    [InlineData("long l: 3_000_000_000;")]                    // trop grand pour un int, pas pour un long
    [InlineData("const ubyte U: 200;\nuint c: U;")]                            // ubyte vers uint, constante
    public void Une_constante_qui_tient_dans_le_type_attendu_s_y_adapte_sans_notice(string content)
    {
        Assert.Empty(GearAll(content));
    }

    [Theory]
    [InlineData("var i: 1;\ndouble d: i;", "I001 2:11")]               // int vers double
    [InlineData("var i: 1;\nlong l: 2;\nvar r: i + l;", "I001 3:8")]  // int + long : long
    [InlineData("uint u: 1;\nvar i: 1;\nvar r: u + i;", "I001 3:8,I001 3:12")] // uint + int : long, les deux convertis
    public void Une_conversion_sans_perte_est_implicite_avec_une_notice(string content, string expected)
    {
        Assert.Equal(expected, string.Join(",", GearAll(content)));
        Assert.NotNull(Bind(InGear(content)).Program);
    }

    [Theory]
    [InlineData("var d: 1.5;\nint n: d;", "E003 2:8")]      // double vers int : perte possible
    [InlineData("long l: 1;\nint n: l;", "E003 2:8")]
    [InlineData("var i: 1;\nuint u: i;", "E003 2:9")]       // signé vers non signé
    [InlineData("ubyte u: 300;", "E003 1:10")]                // constante qui ne tient pas
    [InlineData("var big: 3_000_000_000;", "E001 1:10")]              // un littéral entier est un int
    public void Une_conversion_avec_perte_possible_est_refusee(string content, string expected)
    {
        Assert.Equal([expected], GearErrors(content));
    }

    #endregion

    #region Erreurs, propagation et rails

    /// <summary>Les erreurs que peut envoyer le guard <paramref name="name"/> du programme.</summary>
    private static string ErrorsOf(string content, string name)
    {
        var (program, diagnostics) = Bind(content);
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
        return program.Gears.Single(g => g.Symbol.Name == name).Symbol.Errors?.ToString() ?? "aucune";
    }

    [Theory]
    [InlineData("guard g(int n): int {\n    fail;\n}", "Exception")]
    [InlineData("guard g(string t): int {\n    return t as! int;\n}", "CastError")]
    [InlineData("guard g(int n): int {\n    return 10 / n;\n}", "Exception")]                         // division par une valeur inconnue
    [InlineData("guard g(int n): int {\n    return 10 / 2;\n}", "aucune")]
    [InlineData("error E { int code; }\nguard g(int n): int {\n    fail new E {code: n};\n}", "E")]
    [InlineData("guard h(string t): int {\n    return t as! int;\n}\nguard g(string t): int {\n    var n: h(t);\n    if (n < 0) {\n        fail;\n    }\n    return n;\n}", "CastError | Exception")] // propagation
    [InlineData("guard g(int n): int {\n    fail {message: \"x\", where: \"\", code: n};\n}", "{int code, string message, string where}")] // duck typing
    public void Les_erreurs_d_un_guard_sont_les_siennes_et_celles_des_guards_qu_il_appelle(string guards, string expected)
    {
        Assert.Equal(expected, ErrorsOf(guards + "\ntrain |> done;", "g"));
    }

    [Fact]
    public void Une_error_recoit_message_et_where_d_office()
    {
        var (program, diagnostics) = Bind("error Introuvable { string chemin; }\nconst Introuvable I: new Introuvable {chemin: \"a\"};\ntrain |> done;");

        Assert.Empty(diagnostics);
        var mold = program!.Molds.Single(m => m.Name == "Introuvable");
        Assert.True(mold.IsError);
        Assert.Equal(["message", "where", "chemin"], mold.Fields.Select(f => f.Name));
    }

    [Theory]
    [InlineData("error E { string message; }\ntrain |> done;", "E071 1:18")]                   // message est déjà reçu d'office
    [InlineData("guard g(): void {\n    fail {x: 1};\n}\ntrain |> g |> done;", "E091 2:10")]  // pas une erreur
    [InlineData("guard g(int n): int {\n    fail \"x\";\n}\nguard h(string t): int {\n    return t as! int;\n}\nguard k(int n): int {\n    var a: g(n);\n    return h(\"1\");\n}\n"
        + "emit e(string x): uint {\n    return 1;\n}\ntrain 1 |> k !> [e] |> done;", "E003 14:18")]   // le rail reçoit une union : match
    [InlineData("guard ok(int n): int {\n    done;\n}\ngear g(): int => ok(1);\ntrain |> done;", "E062 4:18")]   // un gear n'appelle pas de guard
    public void Les_erreurs_mal_formees_sont_signalees(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Theory]
    [InlineData("gear g(int n): int => n;\n!train 1 |> g !> [$ 2] |> done;", "W004 2:15")]                       // un gear n'échoue pas
    [InlineData("guard g(int n): int {\n    done;\n}\ntrain 1 |> g !> [$ 2] |> done;", "W004 4:14")]            // un guard qui ne peut pas échouer
    [InlineData("guard g(int n): int {\n    if (n < 0) {\n        fail;\n    }\n}\ntrain 1 |> g |> done;", "W007 1:7")] // done implicite
    public void Un_rail_inutile_et_un_done_implicite_sont_signales(string content, string expected)
    {
        Assert.Equal([expected], All(content));
    }

    #endregion

    #region Casts, types bornés et ingots

    [Theory]
    [InlineData("var n: 300 as long;")]                                         // élargissement
    [InlineData("var i: 1;\nvar b: i as byte;")]                                // contrôlé à l'exécution
    [InlineData("var d: 2.5;\nvar n: d as int;")]                               // tronqué vers zéro
    [InlineData("var i: 1;\nvar n: i as? ubyte;\nvar m: n ?? 0;")]              // null s'il ne tient pas
    [InlineData("var t: \"5\";\nvar n: t as? int<0..10>;")]                     // texte, puis bornes
    [InlineData("var n: 3.7 as int;\nvar m: 2.5 as float;")]                     // constantes converties
    [InlineData("int<0..100> p: 50;\np <- p + 1;\nincrement p;\np +<- 2;")] // calcul sur la base, puis contrôle
    [InlineData("int<0..100> p: 50;\nint<-10..1000> q: p;\nvar i: p;")] // bornes contenues, base
    [InlineData("float<0.0..1.0> f: 0.5;\nint<-5..5> e: -5;")]
    [InlineData("var i: 1;\nint<0..10> x: i;")]                         // contrôlé à l'exécution
    public void Les_casts_et_les_types_bornes_sont_acceptes(string body)
    {
        Assert.Empty(GearAll(body));
    }

    [Theory]
    [InlineData("var n: 300 as byte;", "E086 1:8")]                     // constante hors du type
    [InlineData("var n: 1.0e10 as int;", "E086 1:8")]
    [InlineData("var n: 5 as int<0..3>;", "E086 1:8")]                  // hors des bornes visées
    [InlineData("var n: \"5\" as int;", "E085 1:8")]                     // as ne lit pas un texte
    [InlineData("var n: 5 as? string;", "E085 1:8")]                    // as? ne sert pas à écrire en texte
    [InlineData("var n: true as int;", "E085 1:8")]
    [InlineData("var i: 1;\nvar n: i as! byte;", "E060 2:8")]           // as! hors d'un guard
    [InlineData("int<0..100> p: 150;", "E086 1:16")]           // constante hors des bornes
    [InlineData("string<0..1> s: \"\";", "E087 1:1")]           // pas un nombre
    [InlineData("int<0.5..1> x: 1;", "E087 1:5")]             // borne flottante pour un entier
    [InlineData("int<10..1> x: 5;", "E087 1:1")]               // bornes inversées
    [InlineData("byte<0..300> x: 5;", "E087 1:9")]            // borne hors du type
    [InlineData("var i: 1;\nint<0..i> x: 0;", "E087 2:8")]    // borne inconnue à la compilation
    public void Les_casts_et_les_bornes_invalides_sont_signales(string body, string expected)
    {
        Assert.Equal([expected], GearErrors(body));
    }

    [Fact]
    public void Un_cast_vers_le_meme_type_est_signale_inutile()
    {
        Assert.Equal(["W013 2:8"], GearAll("var i: 1;\nvar n: i as int;"));
    }

    [Theory]
    [InlineData(false, "var i: 1;\nvar b: i as byte;", true)]     // suit overflow
    [InlineData(true, "var i: 1;\nvar b: i as byte;", false)]  // perd ses bits de poids fort
    [InlineData(true, "var d: 1.5;\nvar b: d as int;", true)]   // un flottant toujours contrôlé
    [InlineData(false, "var i: 1;\nvar b: i as long;", false)]    // tient toujours
    public void Un_cast_entre_nombres_n_est_controle_que_s_il_peut_echouer(bool wrapping, string body, bool expected)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", InGear(body)), diagnostics).Tokenize();
        var program = new Binder(diagnostics, wrapping ? OverflowMode.Unchecked : OverflowMode.Checked).Bind(new Parser(tokens, diagnostics).Parse());

        var declaration = Assert.IsType<IrVariableDeclaration>(Assert.Single(program!.Gears).Body.Statements[1]);
        var cast = Assert.IsType<IrNumericCast>(declaration.Value);
        Assert.Equal(expected, cast.Checked);
    }

    /// <summary>Ingots et mold de test, déclarés avant le gear de test (4 lignes).</summary>
    private const string Ingots = "ingot UserId: int;\ningot Autre: int;\nmold P { string nom; }\ningot Joueur: P;\n";

    /// <summary>Erreurs de <paramref name="body"/> dans un gear, après les ingots de test ; positions relatives au corps.</summary>
    private static string[] IngotErrors(string body) =>
        [.. Errors(Ingots + InGear(body)).Select(d => Regex.Replace(d, @" (\d+):(\d+)$", m => $" {int.Parse(m.Groups[1].Value) - 5}:{m.Groups[2].Value}"))];

    [Theory]
    [InlineData("UserId u: 42;\nvar b: u°;\nu °<- b;\nu <- UserId(b);")]                // constante, °, °<-, construction
    [InlineData("var u: UserId(1);\nvar s: u + 1;\nvar t: s * u;\nvar v: -u;\nincrement u;")] // le calcul garde la marque
    [InlineData("var u: UserId(1);\nvar e: u eq UserId(3);\nvar f: u < 5;")]                    // comparaisons
    [InlineData("var u: UserId(1);\n`{u}` -> console;")]                                        // s'écrit comme sa base
    [InlineData("Joueur j: {nom: \"a\"};\nvar n: j.nom;")]                               // champs d'un ingot de mold
    [InlineData("UserId | string v: UserId(1);\nmatch (v) {\n    UserId u |> { }\n    string s |> { }\n}")]
    public void Les_ingots_sont_acceptes(string body)
    {
        Assert.Empty(All(Ingots + InGear(body)));
    }

    [Fact]
    public void Un_calcul_sur_un_ingot_garde_sa_marque_et_une_comparaison_donne_un_bool()
    {
        var (program, _) = Bind(Ingots + InGear("var u: UserId(1);\nvar s: u + 1;\nvar e: u eq 2;"));
        var statements = Assert.Single(program!.Gears).Body.Statements;

        Assert.Equal("UserId", ((IrVariableDeclaration)statements[1]).Variable.Type.ToString());
        Assert.Equal(PrimitiveType.Bool, ((IrVariableDeclaration)statements[2]).Variable.Type);
    }

    [Theory]
    [InlineData("var u: UserId(1);\nvar i: 1;\nvar s: u + i;", "E040 3:8")]      // base non constante
    [InlineData("var u: UserId(1);\nvar a: Autre(1);\nvar e: u eq a;", "E040 3:8")] // deux ingots
    [InlineData("var i: 1;\nUserId u: i;", "E003 2:11")]                   // une variable de la base
    [InlineData("var u: UserId(1);\nint i: u;", "E003 2:8")]               // vers la base sans °
    [InlineData("var i: 1;\nvar b: i°;", "E088 2:8")]                              // pas un ingot
    [InlineData("var i: 1;\ni °<- 2;", "E088 2:1")]
    [InlineData("var u: UserId(1, 2);", "E058 1:8")]                               // une seule valeur
    [InlineData("var u: UserId(1);\nvar n: u as int;", "E085 2:8")]                // as ne retire pas la marque
    public void Les_melanges_d_ingots_sont_signales(string body, string expected)
    {
        Assert.Equal([expected], IngotErrors(body));
    }

    [Theory]
    [InlineData("ingot A: A;\ntrain |> done;", "E089 1:7")]                           // repose sur lui-même
    [InlineData("ingot A: [B];\ningot B: A;\ntrain |> done;", "E089 2:7")]             // par un tableau et un autre ingot
    [InlineData("ingot f: int;\ngear f(): int => 1;\ntrain |> done;", "E089 1:7")]    // nom d'un gear
    [InlineData("ingot U: int;\nmold U { int x; }\ntrain |> done;", "E075 2:6")]       // nom d'un type
    [InlineData("mold P { J j; }\ningot J: P;\ntrain |> done;", "E073 1:6")]           // un mold qui se contient par un ingot
    public void Les_declarations_d_ingots_invalides_sont_signalees(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    #endregion

    #region Collections

    [Theory]
    [InlineData("var a: [1, 2];\na.push(3);\nvar n: a.length;\nvar x: a[0];")]                       // tableau dynamique
    [InlineData("[int;3] t: [1, 2, 3];\nvar x: t[2];")]                                      // taille fixe
    [InlineData("[int;0..2] p: [];\np.push(1);")]                                          // borné
    [InlineData("[string+] s: [\"a\"];\nvar n: s.length;")]                                   // non vide
    [InlineData("[int] a: [];\nvar b: [...a, 2, ...a];")]                                   // vide et spread
    [InlineData("var d: [\"a\": 1];\nd.add(\"b\", 2);\nvar h: d.has(\"a\");\nvar v: d[\"a\"];")]   // dictionnaire
    [InlineData("var p: (x: 1, y: \"un\");\nvar a: p.x;\nvar b: p.Item2;")]                         // tuple nommé
    [InlineData("var a: [1, 2];\n[int;2] b: a as [int;2];\nvar c: a as? [int;3];")]          // casts entre sortes
    [InlineData("[int;2] a: [1, 2];\n[int] b: a;\nvar e: b eq [1, 2];")]             // vers [T] sans contrôle
    [InlineData("(int, string) p: (1, \"a\");\n(int n, string s) q: p;")]           // les noms ne comptent pas
    public void Les_collections_sont_acceptees(string body)
    {
        Assert.DoesNotContain(All(InGear(body)), d => d.StartsWith('E'));
    }

    [Theory]
    [InlineData("var a: [];", "E001 1:8")]                                              // type inconnu
    [InlineData("var a: [1];\na[0] <- 2;", "E013 2:1")]                                // pas de modification d'élément
    [InlineData("[int;2] a: [1, 2];\na.push(3);", "E040 2:3")]                // un [T;N] ne grandit pas
    [InlineData("[int;3] t: [1, 2, 3];\nvar x: t[-4];", "E086 2:10")]                    // avant le début
    [InlineData("[int;3] t: [1, 2, 3];\nvar x: t[3];", "E086 2:10")]                     // après la fin
    [InlineData("[int;2] a: [1, 2, 3];", "E086 1:12")]                         // trop d'éléments
    [InlineData("[int;3..1] a: [];", "E092 1:1")]                              // bornes inversées
    [InlineData("var d: [1.5: 1];", "E093 1:9")]                                       // clé flottante
    [InlineData("var d: [\"a\": 1, \"a\": 2];", "E094 1:17")]                          // clé en double
    [InlineData("var a: [1];\nvar n: a.taille;", "E074 2:10")]                          // membre inconnu
    [InlineData("var a: [1];\na.push(\"x\");", "E003 2:8")]                            // mauvais type d'élément
    [InlineData("const [int] a: [1];\na.push(2);", "E005 2:1")]                           // une constante
    [InlineData("var p: (1, 2);\nvar x: p.Item3;", "E074 2:10")]                        // élément de tuple inconnu
    [InlineData("var a: [1];\nvar b: a as [string];", "E085 2:8")]                     // éléments différents
    public void Les_erreurs_sur_les_collections_sont_signalees(string body, string expected)
    {
        Assert.Equal([expected], GearErrors(body));
    }

    [Fact]
    public void Un_parametre_ne_recoit_pas_de_push()
    {
        Assert.Equal(["E014 2:5"], Errors("gear f([int] a): void {\n    a.push(1);\n}\ntrain |> done;"));
    }

    [Fact]
    public void Un_tableau_de_taille_fixe_ecrit_est_connu_a_la_compilation()
    {
        var declaration = (IrVariableDeclaration)GearBody("const [int;3] t: [1, 2, 3];\nvar x: t[1];")[1];

        Assert.Equal(2L, ConstantFolder.Fold(declaration.Value));
    }

    #endregion

    #region Lambdas

    [Theory]
    [InlineData("var f: (int a) => a * 2;\nvar n: f(3);")]                                                   // type déduit
    [InlineData("var log: (string m) => { m -> console; };\nlog(\"a\");")]                                   // corps bloc, void
    [InlineData("pure (int) => int f: (a) => a + 1;\nvar n: f(1);")]                                // paramètre typé par le contexte
    [InlineData("(int) => int f: (int a) => a;")]                                                    // pure entre dans le type sans pure
    [InlineData("var f: (int a) => {\n    if (a > 0) {\n        return 1;\n    }\n    return 2;\n};")]          // retour du premier return
    [InlineData("var f: (int a) => {\n    var t: a;\n    increment t;\n    return t;\n};")]                      // une variable locale se modifie
    [InlineData("var n: 3;\nvar f: (int a) => a + n;\nvar g: (int b) => f(b) * 2;\nn <- 4;")]              // captures, et la variable extérieure change après
    [InlineData("((int) => bool)? f: null;\nvar vide: f eq null;")]                                  // nullable comparé à null
    [InlineData("[(int) => int] ops: [(x) => x + 1, (x) => x * 2];\nvar n: ops[1](4);")]              // tableau de lambdas
    [InlineData("var f: () => 7;\nvar n: f();\nvar g: (int a) => a;\nvar m: 2 |> g;")]                     // sans paramètre, et en pipeline
    [InlineData("var n: 2 |> (x) => x * 3 |> (x) => x + 1;")]                                      // lambda écrite directement dans le pipeline
    public void Les_lambdas_sont_acceptees(string body)
    {
        Assert.DoesNotContain(All(InGear(body)), d => d.StartsWith('E'));
    }

    [Theory]
    [InlineData("var n: 2 |> () => 3;", "E058")]
    [InlineData("var n: 2 |> (a, b) => a + b;", "E058")]
    public void Une_lambda_de_pipeline_recoit_exactement_la_valeur_courante(string body, string code)
    {
        Assert.Contains(All(InGear(body)), d => d.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void Une_lambda_sans_effet_est_pure()
    {
        var statements = GearBody("var f: (int a) => a * 2;\nvar g: (string m) => { m -> console; };");

        Assert.Equal("pure (int) => int", ((IrVariableDeclaration)statements[0]).Variable.Type.ToString());
        Assert.Equal("(string) => void", ((IrVariableDeclaration)statements[1]).Variable.Type.ToString());
    }

    [Theory]
    [InlineData("var f: (x) => x;", "E001 1:9")]                                                   // type du paramètre inconnu
    [InlineData("var f: () => null;", "E001 1:14")]                                                // retour inconnu
    [InlineData("var n: 1;\nvar f: (int a) => {\n    n <- 2;\n};", "E005 3:5")]                     // capturée par copie
    [InlineData("var n: 1;\nvar f: (int n) => n;", "E044 2:13")]                                     // masque une variable
    [InlineData("(int) => int f: (int a, int b) => a;", "E003 1:17")]                      // nombre de paramètres
    [InlineData("(int) => int f: (long a) => 1;", "E003 1:18")]                            // type de paramètre
    [InlineData("(int) => string f: (a) => a;", "E003 1:27")]                              // type de retour
    [InlineData("pure (int) => int f: (a) => {\n    a -> console;\n    return a;\n};", "E066 2:5")] // pure exigée
    [InlineData("var f: (int a) => a;\nvar b: f eq f;", "E040 2:8")]                               // pas d'égalité
    [InlineData("var n: 1;\nvar x: n(2);", "E040 2:8")]                                             // pas une lambda
    [InlineData("var f: (int a) => a;\nvar x: f(1, 2);", "E058 2:8")]                             // nombre d'arguments
    [InlineData("var f: (int a) => a;\nvar x: f(\"a\");", "E003 2:10")]                            // type d'argument
    [InlineData("var f: (int a) => {\n    fail;\n};", "E060 2:5")]                                      // pas un guard
    [InlineData("(int) => int f: (a) => {\n    if (a > 0) {\n        return 1;\n    }\n};", "E056 1:17")] // un chemin ne renvoie rien
    public void Les_erreurs_sur_les_lambdas_sont_signalees(string body, string expected)
    {
        Assert.Equal([expected], GearErrors(body));
    }

    [Theory]
    [InlineData("gear pure g((int) => int f): int => f(1);\ntrain |> done;", "E057 1:37")]                          // lambda impure dans un gear pure
    [InlineData("gear log(): int => 1;\ngear g(): void {\n    pure () => int f: () => log();\n}\ntrain |> done;", "E057 3:29")] // lambda pure exigée
    [InlineData("guard h(int a): int => a;\ngear g(): void {\n    var f: h;\n}\ntrain |> done;", "E062 3:12")]           // un guard n'est pas une valeur
    public void Les_lambdas_respectent_pure_et_les_guards(string content, string expected)
    {
        Assert.Equal([expected], Errors(content));
    }

    [Fact]
    public void Un_gear_passe_par_son_nom_a_le_type_de_sa_signature()
    {
        var (program, diagnostics) = Bind("gear pure doubler(int n): int => n * 2;\ngear g(): void {\n    var f: doubler;\n    var n: f(2);\n}\ntrain |> done;");

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        var declaration = (IrVariableDeclaration)program!.Gears.Single(g => g.Symbol.Name == "g").Body.Statements[0];
        Assert.Equal("pure (int) => int", declaration.Variable.Type.ToString());
    }

    #endregion

    #region Parcours

    [Theory]
    [InlineData("var d: [1, 2] ||> (x) => x * 2;", "[int]")]                                   // transforme
    [InlineData("var d: [1, 2] ||> (x) => x * 2 ||> (y) => y > 2;", "[bool]")]                  // enchaîné, élément par élément
    [InlineData("[int;2] f: [1, 2];\nvar d: f |!|> (x) => x + 1;", "[int]")]            // un [T;N] parcouru donne un [T]
    public void Un_parcours_donne_un_tableau_des_valeurs_obtenues(string body, string type)
    {
        var declaration = GearBody(body).OfType<IrVariableDeclaration>().Last();

        Assert.Equal(type, declaration.Variable.Type.ToString());
    }

    [Fact]
    public void Un_parcours_dont_la_derniere_etape_ne_renvoie_rien_est_une_instruction()
    {
        Assert.DoesNotContain(All(InGear("[1, 2] ||> (x) => {\n    x -> console;\n};")), d => d.StartsWith('E'));
    }

    [Theory]
    [InlineData("var n: 3;\nvar d: n ||> (x) => x;", "E040 2:8")]                                       // pas un tableau
    [InlineData("var d: [1] ||> (int a, int b, int c) => a;", "E003 1:16")]                                    // un seul paramètre
    [InlineData("var d: [1] ||> (string s) => s;", "E003 1:17")]                                        // type de l'élément
    [InlineData("[1] ||> (x) => {\n    x -> console;\n} ||> (y) => y;", "E040 3:3")]                       // plus rien après une étape void
    public void Les_erreurs_de_parcours_sont_signalees(string body, string expected)
    {
        Assert.Equal([expected], GearErrors(body));
    }

    /// <summary>Un guard qui peut échouer, un emit, et un gear pure, pour les parcours (5 lignes).</summary>
    private const string EachGears = "guard positif(int n): int {\n    if (n < 0) {\n        fail;\n    }\n    done;\n}\nemit montrer(int n): void {\n    n -> console;\n}\ngear pure double2(int n): int => n * 2;\n";

    [Theory]
    [InlineData("gear g([int] v): void {\n    var d: v |!|> positif;\n}\ntrain |> done;", "E062 12:19")]          // |!|> hors train et guard
    [InlineData("gear g([int] v): void {\n    v ||> montrer;\n}\ntrain |> done;", "E063 12:11")]                  // un emit par élément
    [InlineData("gear pure g([int] v): [int] => v ||> positif;\ntrain |> done;", "E057 11:38")]                // pure : pas de guard
    public void Les_etapes_de_parcours_respectent_guards_emits_et_pure(string content, string expected)
    {
        Assert.Equal([expected], Errors(EachGears + content));
    }

    [Theory]
    [InlineData("gear g([int] v): [int] => v ||> positif;\ntrain |> done;")]                                     // ||> écarte : permis partout
    [InlineData("guard g([int] v): [int] => v |!|> positif;\ntrain [1] |> g |> done;")]                         // |!|> dans un guard
    [InlineData("train [1, -2] ||> positif !> [done] ||> double2 |!|> positif |> done;")]                        // rails dans un train
    public void Les_parcours_avec_des_guards_sont_acceptes(string content)
    {
        Assert.DoesNotContain(All(EachGears + content), d => d.StartsWith('E'));
    }

    [Fact]
    public void Un_guard_applique_par_un_parcours_qui_ecarte_ne_fait_pas_echouer_le_guard_englobant()
    {
        var (program, _) = Bind(EachGears + "guard ecarte([int] v): [int] => v ||> positif;\nguard abandonne([int] v): [int] => v |!|> positif;\ntrain [1] |> ecarte |> abandonne |> done;");
        var gears = program!.Gears.ToDictionary(g => g.Symbol.Name, g => g.Symbol);

        Assert.Null(gears["ecarte"].Errors);
        Assert.NotNull(gears["abandonne"].Errors);
    }

    [Fact]
    public void Un_rail_sur_une_etape_qui_ne_peut_pas_echouer_et_un_tableau_de_tableaux_sont_signales()
    {
        var warnings = All(EachGears + "mold P { string nom; [int] notes; }\n!train [1] ||> double2 !> [done] |> done;\n");
        Assert.Contains("W004 12:24", warnings);
        Assert.DoesNotContain(warnings, d => d.StartsWith('E'));

        var nested = All("mold P { string nom; [int] notes; }\ngear g([P] ps): void {\n    var n: ps ||> (p) => p.nom;\n}\ntrain |> done;");
        Assert.Contains("W041 3:12", nested);
    }

    #endregion

    #region Boucles de guard et points de reprise

    /// <summary>Guards et gears des tests de boucles et de reprises : le train est à la ligne 19.</summary>
    private const string LoopGears = """
        guard moitie(int n): int {
            if (n <= 1) {
                done;
            }
            if (n eq 7) {
                fail "sept";
            }
            return n / 2;
        }
        guard sur(int n): int => n;
        gear double2(int n): int => n * 2;
        gear deux(int a, int b): int => a + b;
        guard texte(int n): string => "x";
        emit fin(int n): uint => 0;
        gear reparer(Exception e): int => 1;
        gear tous([int] v): [int] => v;
        gear repli(Exception e): [int] => [1];
        gear relancer(LoopError e): int => 2;

        """;

    [Theory]
    [InlineData("train 64 |> moitie* |> done;")]                                                       // sans limite : done ou fail
    [InlineData("train 64 |> moitie*<3> |> done;")]                                                    // la dernière valeur passe
    [InlineData("train 64 |> moitie*<!3> !> [$ 2] |> done;")]                                          // LoopError sur le rail
    [InlineData("train 64 |> sur*<!3> !> [$ 2] |> done;")]                                             // seul <!3> peut échouer
    [InlineData("train 64 |> ^1 moitie !> [reparer |> ^1] |> done;")]                                  // depuis le rail du point
    [InlineData("train 64 |> ^1 double2 |> moitie !> [reparer |> ^1<3> |> $ 4] |> done;")]             // limite atteinte : la suite
    [InlineData("train 64 |> ^1 sur |> ^2 moitie !> [reparer |> ^1<!2>] |> done;")]                    // limite atteinte : échec
    [InlineData("train 64 |> ^1 moitie !> [reparer |> moitie !> [reparer |> ^1<2> |> $ 3] |> $ 5] |> done;")] // d'un rail imbriqué
    [InlineData("train 64 |> ^1 moitie*<5> !> [reparer |> ^1<2> |> $ 3] |> done;")]                    // point et boucle sur la même étape
    [InlineData("train [64, 7] |> ^1 tous ||> moitie !> [repli |> ^1<2> |> done] |> done;"  )]         // d'un rail par élément
    public void Les_boucles_et_les_reprises_sont_acceptees(string train)
    {
        Assert.DoesNotContain(All(LoopGears + train), d => d.StartsWith('E') || d.StartsWith("W004"));
    }

    [Theory]
    [InlineData("train 64 |> double2* |> done;", "E095 19:20")]                                        // un gear ne boucle pas
    [InlineData("train 64 |> fin*;", "E095 19:16")]                                                    // un emit non plus
    [InlineData("train 64 |> texte* |> done;", "E096 19:18")]                                          // la valeur ne peut pas revenir
    [InlineData("train 64 |> moitie*<0> |> done;", "E099 19:21")]                                      // limite nulle
    [InlineData("train 64 |> ^1 moitie |> ^1 sur |> done;", "E097 19:26")]                             // point déjà posé
    [InlineData("train 1 |> ^1 deux(2) |> done;", "E097 19:12")]                                       // deux paramètres
    [InlineData("train 64 |> moitie !> [reparer |> ^1] |> done;", "E098 19:35")]                       // point inconnu
    [InlineData("train 64 |> moitie !> [reparer |> ^1] |> ^1 sur |> done;", "E098 19:35")]             // point en avant
    [InlineData("train 64 |> ^1 double2 |> moitie !> [^1] |> done;", "E003 19:38")]                    // l'erreur n'est pas un int
    [InlineData("train 64 |> ^1 sur |> ^1 |> done;", "E039 19:29")]                                    // rien après une reprise
    [InlineData("train 64 |> ^1 sur |> ^1<0> |> done;", "E099 19:26")]                                 // limite de reprises nulle
    public void Les_boucles_et_les_reprises_mal_formees_sont_signalees(string train, string expected)
    {
        Assert.Equal([expected], Errors(LoopGears + train));
    }

    [Fact]
    public void Une_reprise_limitee_sans_suite_doit_terminer_son_chemin()
    {
        Assert.Equal(["E038 19:28"], Errors(LoopGears + "train 64 |> ^1 sur |> ^1<3>;"));
    }

    [Fact]
    public void Un_rail_inutile_derriere_une_boucle_qui_ne_peut_pas_echouer_est_signale()
    {
        Assert.Contains("W004 19:21", All(LoopGears + "train 64 |> sur*<3> !> [$ 2] |> done;"));
    }

    [Fact]
    public void Une_reprise_revient_au_point_avec_la_valeur_courante()
    {
        var entry = Valid(LoopGears + "train 64 |> ^1 double2 |> sur*<!4> !> [relancer |> ^1<3> |> $ 4] |> done;");

        var marked = entry.Main.Steps[0];
        var looped = entry.Main.Steps[1];
        Assert.NotNull(marked.Entry?.Checkpoint);
        Assert.IsType<IrStepInput>(marked.Call!.Arguments[0]);
        Assert.Equal(new IrLimit(4, true), looped.Entry?.Repeat?.Limit);
        Assert.Equal("LoopError", looped.Errors?.ToString());

        var resume = Assert.IsType<IrResume>(looped.Rail!.End);
        Assert.Same(marked.Entry!.Checkpoint, resume.Target);
        Assert.Equal(new IrLimit(3, false), resume.Limit);
        Assert.IsType<IrExit>(resume.Fallback!.End);
        Assert.Equal(1u, resume.Target.LimitedResumes);
    }

    #endregion

    #region Intervalles et accumulation

    /// <summary>Le type de la variable <paramref name="name"/> déclarée dans le corps du gear de test.</summary>
    private static string TypeOf(string body, string name)
    {
        var (program, diagnostics) = Bind(InGear(body));
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        var declaration = program!.Gears[0].Body.Statements.OfType<IrVariableDeclaration>().Single(d => d.Variable.Name == name);
        return declaration.Value.Type.ToString();
    }

    [Theory]
    [InlineData("var r: [0..10];", "[int;11]")]                                  // fin comprise
    [InlineData("var r: [0..<10];", "[int;10]")]                                 // fin exclue
    [InlineData("var r: [10..0];", "[int;11]")]                                  // bornes connues qui descendent
    [InlineData("var r: [0..10 by 3];", "[int;4]")]                              // 0, 3, 6, 9
    [InlineData("var r: [10..<0 by -3];", "[int;4]")]                            // 10, 7, 4, 1
    [InlineData("var r: [3..<3];", "[int]")]                                     // vide : pas de taille fixe
    [InlineData("var n: 5;\nvar r: [0..n];", "[int]")]                          // borne connue à l'exécution
    [InlineData("var u: 3 as uint;\nvar r: [0..u];", "[uint]")]                 // la constante prend le type de l'autre borne
    [InlineData("var t: [1, 2] |+> 0 with (acc, x) => acc + x;", "int")]         // accumulateur du type du départ
    [InlineData("var t: [1..4] ||> (x) => x * x |+> 0 with (acc, x) => acc + x;", "int")] // après un parcours
    [InlineData("(int, int) => int f: (a, x) => a + x;\nvar t: [1, 2] |+> 0 with f;", "int")] // variable lambda
    public void Un_intervalle_et_une_accumulation_ont_leur_type(string body, string expected)
    {
        Assert.Equal(expected, TypeOf(body, body.Contains("var t") ? "t" : "r"));
    }

    [Fact]
    public void Le_type_de_l_accumulateur_est_celui_du_premier_parametre_du_gear()
    {
        var (program, diagnostics) = Bind("gear pure ajouter(long acc, int x): long => acc + x;\ngear test(): void {\n    var t: [1, 2] |+> 0 with ajouter;\n}\ntrain |> done;");
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        var each = Assert.IsType<IrEach>(program!.Gears[1].Body.Statements.OfType<IrVariableDeclaration>().Single().Value);
        Assert.Equal("long", each.Type.ToString());
        Assert.Equal("long", each.Fold!.Seed.Type.ToString());
    }

    [Fact]
    public void Un_intervalle_sans_pas_descend_seulement_si_ses_bornes_connues_descendent()
    {
        var (program, _) = Bind(InGear("var n: 0;\nvar a: [10..0];\nvar b: [10..n];"));
        var ranges = program!.Gears[0].Body.Statements.OfType<IrVariableDeclaration>().Select(d => d.Value).OfType<IrRange>().ToArray();

        Assert.Equal(-1L, Assert.IsType<IrIntegerConstant>(ranges[0].Step).Value);
        Assert.Equal(1L, Assert.IsType<IrIntegerConstant>(ranges[1].Step).Value);
    }

    [Theory]
    [InlineData("var r: [0.5..2];", "E100 1:9")]                                 // pas de flottants
    [InlineData("var r: [0..10 by 0];", "E100 1:18")]                            // pas nul
    [InlineData("var r: [0..10 by 1.5];", "E100 1:18")]                          // pas entier
    [InlineData("var r: [10..0 by 2];", "E100 1:18")]                            // sens opposé aux bornes
    [InlineData("var l: 5 as long;\nvar u: 5 as ulong;\nvar r: [l..u];", "E100 3:8")] // sans type commun
    [InlineData("var t: [1, 2] |+> null with (a, x) => x;", "E001 1:19")]        // départ sans type
    [InlineData("var t: [1, 2] |+> 0 with (a, x) => \"x\";", "E003 1:36")]      // l'étape renvoie un autre type
    [InlineData("var t: 5 |+> 0 with (a, x) => a;", "E040 1:8")]                 // pas un tableau
    public void Les_intervalles_et_accumulations_mal_formes_sont_signales(string body, string expected)
    {
        Assert.Equal([expected], GearErrors(body));
    }

    /// <summary>Guards et emits des tests d'accumulation : le train est à la ligne 8.</summary>
    private const string FoldGears = """
        guard positif(int acc, int n): int {
            if (n < 0) {
                fail "négatif";
            }
            return acc + n;
        }
        emit fin(int total): uint => 0;

        """;

    [Theory]
    [InlineData("train [1, 2] |+> 0 with positif !> [$ 2] |> fin;")]                       // un guard dans un train, avec son rail
    [InlineData("guard somme([int] v): int => v |+> 0 with positif;\ntrain [1] |> somme |> fin;")] // un guard dans un guard
    public void Un_guard_accumule_dans_un_train_ou_un_guard(string train)
    {
        Assert.DoesNotContain(All(FoldGears + train), d => d.StartsWith('E'));
    }

    [Theory]
    [InlineData("gear g(): int => [1] |+> 0 with positif;\n!train |> done;", "E062 8:33")]    // son échec n'irait nulle part
    [InlineData("guard autre(int acc, int n): long => 1;\ntrain [1] |+> 0 with autre |> done;", "E003 9:22")] // un guard renvoie l'accumulateur
    [InlineData("emit deux(int acc, int n): uint => 0;\ntrain [1] |+> 0 with deux;", "E063 9:22")]      // un emit n'accumule pas
    public void Les_guards_et_emits_mal_places_dans_une_accumulation_sont_signales(string train, string expected)
    {
        Assert.Equal([expected], Errors(FoldGears + train));
    }

    [Fact]
    public void Un_guard_qui_accumule_dans_un_guard_lui_transmet_ses_erreurs()
    {
        var (program, _) = Bind(FoldGears + "guard somme([int] v): int => v |+> 0 with positif;\ntrain [1] |> somme |> fin;");
        Assert.NotNull(program!.Gears.Single(g => g.Symbol.Name == "somme").Symbol.Errors);
    }

    [Fact]
    public void Un_rail_derriere_une_accumulation_qui_ne_peut_pas_echouer_est_signale()
    {
        Assert.Contains("W004 9:27", All(FoldGears + "gear pure add(int a, int n): int => a + n;\n!train [1] |+> 0 with add !> [$ 2] |> fin;"));
    }

    #endregion

    #region Mutabilité

    /// <summary>Molds et gears des tests de mutabilité : des gears qui modifient un mold ou font grandir un tableau.</summary>
    private const string MutGears = """
        mold Vec2 { float x; float y; }
        mold Corps { Vec2 pos; [int] trace; }
        gear bouger(mut Vec2 v, float dx): void {
            v.x +<- dx;
        }
        gear ajouter(mut [int] liste, int n): void => liste.push(n);
        gear echanger(mut Vec2 a, mut Vec2 b): void {
            var t: a;
            a <- b;
            b <- t;
        }

        """;

    /// <summary>Erreurs d'un corps de gear écrit après <see cref="MutGears"/>, positions relatives au corps.</summary>
    private static string[] MutErrors(string body)
    {
        var offset = MutGears.Count(c => c == '\n') + 1;
        return [.. Errors(MutGears + "gear test(): void {\n" + body + "\n}\ntrain |> done;")
            .Select(e => Regex.Replace(e, @" (\d+):(\d+)$", m => $" {int.Parse(m.Groups[1].Value) - offset}:{m.Groups[2].Value}"))];
    }

    [Fact]
    public void Le_contenu_d_une_variable_mut_se_modifie_en_place()
    {
        var (program, diagnostics) = Bind(MutGears + """
            gear g(): void {
                mut Corps c: {pos: {x: 0.0, y: 0.0}, trace: []};
                c.pos.x <- 1.0;
                c.pos.y +<- 2.0;
                c.trace.push(3);
                c.trace[0] <- 4;
                mut [string:int] ages: ["a": 1];
                ages["a"] *<- 2;
                mut var vs: [{x: 1.0, y: 2.0} as Vec2];
                vs[0].x <- 5.0;
            }
            train |> done;
            """);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        var body = program!.Gears.Single(g => g.Symbol.Name == "g").Body.Statements;
        var assignments = body.OfType<IrContentAssignment>().ToArray();
        Assert.Equal(5, assignments.Length);
        Assert.IsType<IrTargetValue>(Assert.IsType<IrBinary>(assignments[1].Value).Left);   // la valeur d'avant, sans réévaluer la cible
        Assert.IsType<IrMemberAccess>(Assert.IsType<IrPush>(((IrExpressionStatement)body[3]).Expression).Target);
    }

    [Fact]
    public void Un_parametre_mut_recoit_une_variable_mut_et_la_modifie()
    {
        var (program, diagnostics) = Bind(MutGears + """
            gear g(): void {
                mut var v: {x: 1.0, y: 2.0} as Vec2;
                mut var w: v;
                mut [int] liste: [];
                bouger(v, 1.0);
                echanger(v, w);
                liste |> ajouter(4);
            }
            train |> done;
            """);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        var calls = IrWalk.AllExpressions(program!.Gears.Single(g => g.Symbol.Name == "g").Body).OfType<IrCall>().ToArray();
        Assert.Equal(3, calls.Length);
        Assert.All(calls, c => Assert.All(IrMutation.MutableArguments(c), a => Assert.IsType<IrVariableReference>(a)));
        Assert.True(program.Gears.Single(g => g.Symbol.Name == "ajouter").Symbol.Parameters[0].IsMutable);
    }

    [Theory]
    [InlineData("mut int n: 3;", "E101 1:1")]                                                       // seulement tableaux, molds, dictionnaires
    [InlineData("mut var s: \"texte\";", "E101 1:1")]
    [InlineData("Vec2 v: {x: 1.0, y: 2.0};\nbouger(v, 1.0);", "E102 2:8")]                         // l'argument n'est pas mut
    [InlineData("bouger({x: 1.0, y: 2.0}, 1.0);", "E102 1:8")]                                       // une valeur calculée
    [InlineData("mut var v: {x: 1.0, y: 2.0} as Vec2;\nechanger(v, v);", "E102 2:13")]              // deux fois la même variable
    [InlineData("mut [int;2] t: [1, 2];\najouter(t, 3);", "E102 2:9")]                              // pas exactement le type du paramètre
    [InlineData("const Vec2 C: {x: 1.0, y: 2.0};\nbouger(C, 1.0);", "E102 2:8")]                    // une constante
    [InlineData("[int] t: [1];\nt[0] <- 2;", "E013 2:1")]                                           // pas mut
    [InlineData("mut [int] t: [1];\nt.length <- 2;", "E013 2:1")]                                      // pas un contenu
    [InlineData("Corps c: {pos: {x: 0.0, y: 0.0}, trace: []};\nc.trace.push(1);", "E013 2:1")]       // un champ ne grandit qu'avec mut
    [InlineData("var f: (mut Vec2 v) => 1;", "E101 1:9")]                                            // une lambda ne reçoit que des copies
    [InlineData("var f: bouger;", "E101 1:8")]                                                       // un gear à paramètre mut n'est pas une lambda
    [InlineData("[Vec2] vs: [];\nvs ||> (mut v) => { v.x <- 1.0; };", "E102 2:9")]                   // le parcours d'une variable qui n'est pas mut
    [InlineData("mut [Vec2] vs: [];\nvar k: vs ||> (v) => v ||> (mut w) => 1;", "E102 2:29")]        // pas à la deuxième étape
    [InlineData("mut [Vec2] vs: [];\nvar k: vs ||> (v) => v;\nk ||> (mut v) => 1;", "E102 3:8")]    // seulement le parcours d'une variable mut
    [InlineData("mut [Vec2] vs: [];\nvs ||> (v) => v ||> bouger(1.0);", "E102 2:21")]
    [InlineData("mut [Vec2] vs: [];\nvar f: () => { vs[0].x <- 1.0; };", "E005 2:16")]              // capturée par copie
    public void Les_erreurs_de_mutabilite_sont_signalees(string body, string expected)
    {
        Assert.Equal([expected], MutErrors(body));
    }

    [Fact]
    public void Un_parcours_modifie_sur_place_une_variable_mut_a_sa_premiere_etape()
    {
        var (program, diagnostics) = Bind(MutGears + """
            gear g(mut [Vec2] param): void {
                mut [Vec2] vs: [{x: 1.0, y: 2.0}];
                vs ||> (mut v) => { v.x <- 0.0; };
                vs ||> bouger(2.0);
                param ||> (mut Vec2 v) => { v.y <- 1.0; };
                var xs: vs ||> (mut v) => v.x;
                mut [[int]] grille: [[1], [2]];
                grille ||> ajouter(0);
            }
            train |> done;
            """);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        var eaches = IrWalk.AllExpressions(program!.Gears.Single(g => g.Symbol.Name == "g").Body).OfType<IrEach>().ToArray();
        Assert.Equal(5, eaches.Length);
        Assert.All(eaches, e => Assert.True(e.Modifies));
        Assert.Equal(PrimitiveType.Void, eaches[0].Type);                                   // modifier ne renvoie rien
        Assert.Equal(new IR.Types.ArrayType(new PrimitiveType(PrimitiveKind.Float)), eaches[3].Type);
    }

    [Fact]
    public void Un_deplacement_libere_la_variable_deplacee()
    {
        var body = GearBody("var a: [1, 2];\nvar b:: a;\nmut [int] c: [];\nc <-| b;");

        Assert.IsType<IrMove>(((IrVariableDeclaration)body[1]).Value);
        Assert.IsType<IrMove>(((IrAssignment)body[3]).Value);
        Assert.Equal(["E006 3:8"], GearErrors("var a: [1, 2];\nvar b:: a;\nvar c: a;"));   // a est libérée
    }

    [Theory]
    [InlineData("const [int] C: [1];\nvar b:: C;", "W014 2:9")]                     // une constante se copie
    [InlineData("var a: [1];\nvar f: () => { var b:: a; };", "W014 2:24")]          // capturée par la lambda
    [InlineData("[int;1] a: [1];\n[int] b:: a;", "W014 2:11")]                      // la valeur est convertie
    public void Un_deplacement_impossible_copie_et_le_signale(string body, string expected)
    {
        Assert.Contains(expected, GearAll(body));
        Assert.DoesNotContain(GearAll(body), d => d.StartsWith('E'));
    }

    [Theory]
    [InlineData("const int a: 5; var b:: a;")]
    [InlineData("int a: 5; long b:: a;")]
    public void Le_repli_conserve_une_copie_dans_l_ir_et_explique_la_liberation(string body)
    {
        var (program, diagnostics) = Bind(InGear(body));
        Assert.NotNull(program);
        var warning = Assert.Single(diagnostics, d => d.Code == "W014");
        Assert.Contains("nom source est libéré", Assert.Single(warning.Help!));
        var statements = Assert.Single(program!.Gears).Body.Statements;
        Assert.IsNotType<IrMove>(Assert.IsType<IrVariableDeclaration>(statements[1]).Value);
    }

    [Theory]
    [InlineData("const int a: 5; var b:: a; var c: a;")]
    [InlineData("int a: 5; long b:: a; var c: a;")]
    [InlineData("const int a: 5; int b: 0; b <-| a; var c: a;")]
    [InlineData("int a: 5; long b: 0; b <-| a; var c: a;")]
    [InlineData("const [int] a: [1]; var b:: a; var c: a[0];")]
    [InlineData("[int;1] a: [1]; [int] b:: a; var c: a[0];")]
    [InlineData("var a: [1]; var f: () => { var b:: a; var c: a; };")]
    [InlineData("var a: [1]; var f: () => { { var b:: a; } var c: a; };")]
    [InlineData("int a: 5; { long b:: a; } var c: a;")]
    public void Une_intention_de_deplacement_libere_la_source_meme_si_elle_est_copiee(string body)
    {
        var (_, diagnostics) = Bind(InGear(body));
        Assert.Contains(diagnostics, d => d.Code == "W014");
        Assert.Equal(["E006"], diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code));
    }

    [Theory]
    [InlineData("const int a: 5; var b:: a; string a: 'nouveau'; var c: a;")]
    [InlineData("int a: 5; long b:: a; string a: 'nouveau'; var c: a;")]
    [InlineData("const int a: 5; int b: 0; b <-| a; string a: 'nouveau'; var c: a;")]
    public void Une_source_copiee_par_intention_de_deplacement_peut_etre_redeclaree(string body)
    {
        var (_, diagnostics) = Bind(InGear(body));
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Contains(diagnostics, d => d.Code == "W014");
        Assert.Contains(diagnostics, d => d.Code == "W001");
    }

    [Theory]
    [InlineData("var b:: a; var c: a;")]
    [InlineData("int b: 0; b <-| a; var c: a;")]
    [InlineData("var b:: a; a <- 6;")]
    [InlineData("var b:: a; increment a;")]
    [InlineData("{ var b:: a; } var c: a;")]
    public void Le_nom_d_un_parametre_copie_par_deplacement_est_libere(string body)
    {
        var (_, diagnostics) = Bind("gear f(int a): void { " + body + " }\n!train |> done;");
        Assert.Contains(diagnostics, d => d.Code == "W014");
        Assert.Equal(["E006"], diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code));
    }

    [Fact]
    public void Un_parametre_de_lambda_peut_reprendre_un_nom_libere()
    {
        var (_, diagnostics) = Bind(InGear("const int a: 5; var b:: a; var f: (int a) => a;"));
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Une_capture_copiee_ne_libere_pas_la_variable_exterieure()
    {
        var (_, diagnostics) = Bind(InGear("var a: [1]; var f: () => { var b:: a; }; var c: a;"));
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Contains(diagnostics, d => d.Code == "W014");
    }

    [Fact]
    public void Une_constante_globale_copiee_ne_disparait_pas_des_autres_gears()
    {
        var (_, diagnostics) = Bind("const int A: 5;\ngear f(): int { var b:: A; return b; }\ngear g(): int => A;\n!train |> done;");
        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.Contains(diagnostics, d => d.Code == "W014");
    }

    [Fact]
    public void Une_source_liberee_ne_revele_pas_une_variable_masquee()
    {
        var (_, diagnostics) = Bind(InGear("var a: 1; { const int a: 2; var b:: a; var c: a; }"));
        Assert.Contains(diagnostics, d => d.Code == "W014");
        Assert.Equal(["E006"], diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code));
    }

    [Fact]
    public void Un_second_deplacement_d_une_source_liberee_est_une_erreur_sans_exception_interne()
    {
        var (_, diagnostics) = Bind(InGear("const int a: 5; var b:: a; var c:: a;"));
        Assert.Equal(["E006"], diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code));
    }

    [Fact]
    public void Un_parametre_du_train_n_est_jamais_mut()
    {
        Assert.Equal(["E101 1:8"], Errors("!train(mut [string] args) |> done;"));
    }

    #endregion

}
