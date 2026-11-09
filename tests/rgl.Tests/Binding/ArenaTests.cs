using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Text;
using Rgl.Tests.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests.Binding;

/// <summary>Les arènes (<c>arena { … }</c>) et le pool (<c>ArenaPool.set(…)</c>) : liaison, règles et exécution (slice B.3).</summary>
public class ArenaTests
{
    private static (IrProgram? Program, IReadOnlyList<Diagnostic> Diagnostics) Bind(string content)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        Assert.False(diagnostics.HasErrors, "erreur de syntaxe dans le cas de test : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        return (new Binder(diagnostics).Bind(unit), diagnostics.Items);
    }

    private static string[] Errors(string content)
    {
        var (program, diagnostics) = Bind(content);
        Assert.Null(program);
        return [.. diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code)];
    }

    private const string Frames = """
        gear pure doubler(int n): int => n * 2;

        gear tour(mut [int] monde, int numero): string {
            arena {
                var suivantes: monde ||> doubler;
                var rapport: `tour {numero} : {suivantes.length} valeurs, dernier {suivantes[suivantes.length - 1]}, avec un texte assez long`;
                monde <- suivantes;
                return rapport;
            }
        }

        emit run(): uint {
            mut [int] monde: [1, 2, 3];
            var a: tour(monde, 1);
            var b: tour(monde, 2);
            `{a}|{b}|{monde}` -> console;
            return 0;
        }

        !train |> run;
        """;

    [Fact]
    public void Un_bloc_arena_et_un_ArenaPool_set_se_lient()
    {
        var (program, diagnostics) = Bind("""
            gear f(int n): uint {
                arena(4096) {
                    var xs: [0..<10] ||> (i) => i * n;
                    var texte: `{xs.length}`;
                }
                var pool: ArenaPool.set([1, 2, 3]);
                var entier: ArenaPool.set(5);
                return 0;
            }
            !train |> $ f(1);
            """);
        Assert.True(program is not null, string.Join(", ", diagnostics.Select(d => d.Format("."))));
    }

    [Theory]
    [InlineData("gear f(): uint {\narena {\nawait sleep(1ms);\n}\nreturn 0;\n}\n!train |> $ f();", "E154")]                                         // une attente dans l'arène
    [InlineData("@async gear g(): int => 1;\ngear f(): uint {\narena {\nvar p: g();\n}\nreturn 0;\n}\n!train |> $ f();", "E154")]                      // un gear asynchrone appelé dans l'arène
    [InlineData("gear f(int n): uint {\narena(n) {\nvar x: 1;\n}\nreturn 0;\n}\n!train |> $ f(1);", "E154")]                                           // une taille qui n'est pas connue à la compilation
    [InlineData("gear f(): uint {\narena(-5) {\nvar x: 1;\n}\nreturn 0;\n}\n!train |> $ f();", "E154")]                                                 // une taille négative
    [InlineData("@async gear g(): int => 1;\n@async gear f(): int {\nvar p: ArenaPool.set(await g());\nreturn 0;\n}\n!train |> $ 0;", "E154")]         // une attente dans ArenaPool.set
    public void Ce_qui_ne_se_place_pas_dans_une_arene_est_refuse(string source, string code) => Assert.Contains(code, Errors(source));

    [Fact]
    public void Une_arene_ouvre_un_arena_scope_et_detache_ce_qui_sort()
    {
        var cpp = CppEmitterTests.Emit(Frames);

        Assert.Contains("rgl::standard::arena_scope rgl_arena", cpp);
        Assert.Contains("rgl::standard::detach(std::move(rgl_v_rapport))", cpp);
    }

    [Fact]
    public void ArenaPool_set_construit_sa_valeur_sous_le_pool_et_un_entier_reste_un_entier()
    {
        var cpp = CppEmitterTests.Emit("emit run(): uint {\nvar xs: ArenaPool.set([1, 2, 3]);\nvar n: ArenaPool.set(5);\n`{xs} {n}` -> console;\nreturn 0;\n}\n!train |> run;");

        Assert.Contains("rgl::standard::allocator_scope", cpp);
        Assert.Contains("rgl::standard::pool_allocator()", cpp);
    }

    [ZigFact]
    public void Ce_qui_sort_de_l_arene_reste_valable_apres_sa_remise_a_zero()
    {
        var (code, output) = CppEmitterTests.CompileAndRun(Frames);

        Assert.Equal((0, "tour 1 : 3 valeurs, dernier 6, avec un texte assez long|tour 2 : 3 valeurs, dernier 12, avec un texte assez long|[4, 8, 12]"), (code, output));
    }

    [ZigFact]
    public void Des_milliers_de_frames_dans_une_arene_se_suivent_sans_rien_garder()
    {
        var (code, output) = CppEmitterTests.CompileAndRun("""
            gear pure doubler(int n): int => n * 2;

            gear frame(int i): int {
                arena {
                    var xs: [0..<200] ||> doubler;
                    var ys: xs ||> (x) => x + i;
                    return ys[199] + (ys.length as int);
                }
            }

            emit run(): uint {
                var total: [0..<2000] ||> frame |+> 0 with (acc, v) => acc + v;
                `{total}` -> console;
                return 0;
            }

            !train |> run;
            """);

        // frame(i) = 398 + i + 200 ; somme pour i de 0 à 1999.
        Assert.Equal((0, (2000 * 598 + 1999 * 2000 / 2).ToString()), (code, output));
    }

    [ZigFact]
    public void Des_arenes_emboitees_et_un_pool_qui_sort_de_la_fonction_gardent_leurs_valeurs()
    {
        var (code, output) = CppEmitterTests.CompileAndRun("""
            gear pure doubler(int n): int => n * 2;

            gear construit(): [int] {
                var xs: ArenaPool.set([0..<6] ||> doubler);
                return xs;
            }

            gear imbrique(): string {
                arena {
                    var dehors: [0..<5] ||> doubler;
                    arena {
                        var dedans: [0..<3] ||> doubler;
                        return `{dehors}{dedans}{construit()}`;
                    }
                }
            }

            emit run(): uint {
                `{imbrique()}` -> console;
                return 0;
            }

            !train |> run;
            """);

        Assert.Equal((0, "[0, 2, 4, 6, 8][0, 2, 4][0, 2, 4, 6, 8, 10]"), (code, output));
    }
}
