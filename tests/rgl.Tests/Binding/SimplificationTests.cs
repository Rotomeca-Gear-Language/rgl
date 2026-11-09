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
/// Tests de la slice 4.10 : <c>panic</c>, handler d'erreurs plus étroit que l'union reçue, <c>p with {…}</c> et
/// <c>default x</c> dans un match d'union.
/// </summary>
public class SimplificationTests
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
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code} {d.Span?.StartPosition.Line}:{d.Span?.StartPosition.Column}")];
    }

    private static (int Code, string Output) CompileAndRun(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Select(d => d.Format("."))));

        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, CppEmitter.Emit(program, "main.rgl").Code);
        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "string.cpp"), main);
        var result = ZigBuild.RunWithInput(executable, null, "");
        return (result.Code, result.Output.Replace("\r\n", "\n"));
    }

    #region panic

    [ZigFact]
    public void Panic_arrete_le_programme_avec_le_code_101_et_vide_la_console()
    {
        const string program = """
            error Oups { int code; }

            gear pure verifie(int n): int {
                if (n > 5) { panic "trop grand"; }
                return n;
            }

            gear second(int n): int {
                if (n > 50) { panic; }
                if (n > 5) { panic new Oups {message: "m", where: "", code: 3}; }
                return n;
            }

            emit Start(): uint {
                `avant ` -> console;
                `{verifie(2) + second(1)}` -> console;
                `{second(9)}` -> console;
                return 0;
            }

            !train |> Start;
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(101, code);
        Assert.Contains("avant 3", output);
        Assert.Contains("panic : m (main.rgl:", output);
    }

    [ZigFact]
    public void Panic_avec_un_texte_ecrit_le_message_et_la_position()
    {
        const string program = """
            gear pure verifie(int n): int {
                if (n > 5) { panic "trop grand"; }
                return n;
            }

            emit Start(): uint {
                `{verifie(9)}` -> console;
                return 0;
            }

            !train |> Start;
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(101, code);
        Assert.Contains("panic : trop grand (main.rgl:2:", output);
    }

    [Fact]
    public void Panic_termine_un_gear_qui_retourne_une_valeur()
    {
        const string program = """
            gear f(int n): int {
                if (n > 1) { return 1; }
                panic "impossible";
            }
            !train |> done;
            """;

        var (result, diagnostics) = Bind(program);

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(result);
    }

    [Fact]
    public void Panic_refuse_ce_qui_n_est_ni_un_texte_ni_une_erreur()
    {
        var errors = Errors("gear f(): void {\n    panic 3;\n}\n!train |> done;");

        Assert.Equal("E091 2:11", errors[0]);
    }

    #endregion

    #region Erreurs d'un handler

    private const string TwoErrors = """
        error Oups { int code; }

        guard test(int x): int {
            if (x eq 1) { fail new Oups {code: 3}; }
            if (x eq 2) { fail "deux"; }
            done;
        }

        gear start(): int { return START; }

        """;

    [Fact]
    public void Un_handler_plus_etroit_que_l_union_des_erreurs_se_lie()
    {
        var (program, diagnostics) = Bind(TwoErrors.Replace("START", "1") + "emit handle(Exception e): uint { return 7; }\nemit fin(int v): uint { return 0; }\ntrain |> start |> test !> [handle] |> fin;");

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void Un_handler_qui_n_accepte_pas_tous_les_membres_est_refuse()
    {
        var errors = Errors(TwoErrors.Replace("START", "1") + "emit handle(string e): uint { return 7; }\nemit fin(int v): uint { return 0; }\ntrain |> start |> test !> [handle] |> fin;");

        Assert.StartsWith("E003", errors[0]);
    }

    [ZigTheory]
    [InlineData(1, "échec")]
    [InlineData(2, "deux")]
    public void Un_handler_exception_recoit_chaque_erreur_sans_les_champs_en_trop(int start, string expected)
    {
        const string handler = """
            emit handle(Exception e): uint {
                e.message -> console;
                return 7;
            }
            emit fin(int v): uint { return 0; }
            train |> start |> test !> [handle] |> fin;
            """;

        var (code, output) = CompileAndRun(TwoErrors.Replace("START", start.ToString()) + handler);

        Assert.Equal(7, code);
        Assert.Equal(expected, output);
    }

    #endregion

    #region with

    private const string Hero = "mold Vec { int x; int y; }\nmold Hero { string name; Vec pos; int hp; [int] cells; }\n";

    [ZigFact]
    public void With_donne_une_copie_dont_certaines_parties_changent()
    {
        const string program = Hero + """
            gear hit(Hero h): Hero => h with {hp: h.hp - 1, pos.x: 10};

            emit Start(): uint {
                Hero a: {name: 'a', pos: {x: 1, y: 2}, hp: 5, cells: [1, 2, 3]};
                var b: hit(a);
                var c: b with {cells[1]: 42, name: 'c', cells[-1]: 7};
                `{a.hp} {b.hp} {b.pos.x} {a.pos.x} {c.cells[1]} {c.cells[2]} {c.name} {b.cells[1]}` -> console;
                return 0;
            }

            !train |> Start;
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(0, code);
        Assert.Equal("5 4 10 1 42 7 c 2", output);
    }

    [ZigFact]
    public void With_applique_les_changements_dans_l_ordre_ecrit()
    {
        const string program = Hero + """
            emit Start(): uint {
                Hero a: {name: 'a', pos: {x: 1, y: 2}, hp: 5, cells: [1]};
                var b: a with {hp: 1, hp: 2};
                `{b.hp}` -> console;
                return 0;
            }

            !train |> Start;
            """;

        var (_, output) = CompileAndRun(program);

        Assert.Equal("2", output);
    }

    [ZigFact]
    public void With_dans_un_guard_fait_echouer_le_guard_sur_un_index_hors_limites()
    {
        const string program = Hero + """
            guard place(Hero h, int i): Hero {
                return h with {cells[i]: 9};
            }

            emit Handle(Exception e): uint {
                `hors limites` -> console;
                return 3;
            }

            emit Fin(Hero h): uint => 0;

            gear start(): Hero => {name: 'a', pos: {x: 1, y: 2}, hp: 5, cells: [1, 2]};

            train |> start |> place(5) !> [Handle] |> Fin;
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(3, code);
        Assert.Equal("hors limites", output);
    }

    [Theory]
    [InlineData("int n: 3; var m: n with {x: 1};", "E040 2:")]                         // pas un mold
    [InlineData("Hero a: {name: 'a', pos: {x: 1, y: 2}, hp: 5, cells: []}; var m: a with {inconnu: 1};", "E074 2:")]   // champ inconnu
    [InlineData("Hero a: {name: 'a', pos: {x: 1, y: 2}, hp: 5, cells: []}; var m: a with {hp: 'texte'};", "E003 2:")]   // mauvais type
    [InlineData("Hero a: {name: 'a', pos: {x: 1, y: 2}, hp: 5, cells: []}; a.hp <- 3;", "E013 2:")]                       // pas mut : with en est l'alternative
    public void With_mal_ecrit_est_signale(string body, string expected)
    {
        var errors = Errors(Hero + "gear test(): void {\n" + body + "\n}\n!train |> done;");

        Assert.Contains(errors, e => e.StartsWith(expected[..4], StringComparison.Ordinal));
    }

    #endregion

    #region default x

    private const string Union = "gear name(int | string | bool v): string {\n    return match (v) {\n        int i |> 'int';\n        default rest |> BODY;\n    };\n}\n!train |> done;";

    [Fact]
    public void Default_nomme_recoit_l_union_des_membres_restants()
    {
        var (program, diagnostics) = Bind(Union.Replace("BODY", "match (rest) { string s |> s; bool b |> 'bool'; }"));

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Fact]
    public void Default_nomme_recoit_le_type_seul_quand_il_n_en_reste_qu_un()
    {
        var (program, diagnostics) = Bind("gear f(int | bool v): string {\n    return match (v) {\n        int i |> 'int';\n        default b |> b ? 'T' : 'F';\n    };\n}\n!train |> done;");

        Assert.DoesNotContain(diagnostics, d => d.Level == DiagnosticLevel.Error);
        Assert.NotNull(program);
    }

    [Theory]
    [InlineData("gear f(int | bool v): string {\n    return match (v) {\n        int i |> 'int';\n        bool b |> 'bool';\n        default r |> 'x';\n    };\n}\n!train |> done;", "E080 5:")]   // rien ne reste
    [InlineData("gear f(int | bool v): string {\n    return match (v) {\n        default r |> 'x';\n        default |> 'y';\n    };\n}\n!train |> done;", "E081 4:")]       // deux default
    [InlineData("gear f(int n): string {\n    return match (n) {\n        case 1 |> 'a';\n        default r |> 'x';\n    };\n}\n!train |> done;", "E080 4:")]       // pas une union
    public void Default_nomme_mal_ecrit_est_signale(string program, string expected)
    {
        var errors = Errors(program);

        Assert.StartsWith(expected, errors[0]);
    }

    [ZigFact]
    public void Default_nomme_s_execute()
    {
        const string program = """
            gear name(int | string | bool v): string {
                return match (v) {
                    int i |> 'int';
                    default rest |> match (rest) {
                        string s |> s;
                        bool b |> 'bool';
                    };
                };
            }

            gear one(int | string | bool v): string {
                return match (v) {
                    int i |> 'int';
                    string s |> 'str';
                    default b |> b ? 'T' : 'F';
                };
            }

            emit Start(): uint {
                int | string | bool a: 'abc';
                int | string | bool c: true;
                `{name(a)} {name(c)} {one(c)}` -> console;
                return 0;
            }

            !train |> Start;
            """;

        var (code, output) = CompileAndRun(program);

        Assert.Equal(0, code);
        Assert.Equal("abc bool T", output);
    }

    #endregion
}
