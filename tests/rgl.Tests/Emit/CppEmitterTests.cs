using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Parsing;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests.Emit;

/// <summary>
/// Tests de l'émetteur C++ : le texte produit, puis des trains compilés pour de vrai avec le runtime
/// et Zig, et exécutés, pour vérifier que Clang accepte le C++ et que le code de sortie est le bon.
/// </summary>
public class CppEmitterTests
{
    /// <summary>C++ de <paramref name="content"/> ; le programme doit être valide.</summary>
    internal static string Emit(string content) => EmitProgram(content).Code;

    /// <summary>
    /// Un programme dont <paramref name="body"/> est le corps d'un gear de test : variables, if,
    /// switch… s'écrivent dans un gear. Le corps commence à la ligne 2.
    /// </summary>
    private static string InGear(string body) => "gear test(): void {\n" + body + "\n}\ntrain |> done;";

    /// <summary>C++ et composants du runtime de <paramref name="content"/> ; le programme doit être valide.</summary>
    private static CppProgram EmitProgram(string content, OverflowMode overflow = OverflowMode.Checked, uint? recursionLimit = 200)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText("main.rgl", content), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).Parse();
        var program = new Binder(diagnostics, overflow, recursionLimit).Bind(unit);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Select(d => d.Format("."))));
        return CppEmitter.Emit(program, "main.rgl");
    }

    [Fact]
    public void La_demo_V0_n_utilise_rien_du_runtime()
    {
        var program = EmitProgram("train |> done;");

        Assert.Empty(program.Components);
        Assert.Equal("""
            // Généré par rgl à partir de main.rgl : ne pas modifier.
            #include <cstdint>

            static std::uint32_t rgl_entry_train() {
            #line 1 "main.rgl"
                return 0u;
            }

            int main() { return static_cast<int>(rgl_entry_train()); }

            """.ReplaceLineEndings("\n"), program.Code);
    }

    [Theory]
    [InlineData("!train |> $ 3;", "")]                                   // rien du runtime
    [InlineData("!train |> fail \"x\";", "")]                          // un message que personne ne lit : rien
    [InlineData("gear test(): void {\nvar b: 1 + 2 > 0;\n}\ntrain |> done;", "")]               // des entiers et des bool : toujours rien
    [InlineData("gear test(): void {\nvar s: \"x\";\n}\ntrain |> done;", "String")]          // une chaîne : seulement string
    [InlineData("gear test(): void {\n\"x\" -> console;\n}\ntrain |> done;", "Console,String")] // la console
    [InlineData("guard positif(int n): int {\nif (n < 0) {\nfail;\n}\ndone;\n}\ntrain |> done;", "String")] // un guard : son Exception
    [InlineData("!train([string] args) args |> done;", "Arguments,Memory,String")] // les arguments : un tableau de string, et leur lecture
    public void Seules_les_parties_du_runtime_utilisees_sont_incluses(string content, string components)
    {
        var program = EmitProgram(content);

        Assert.Equal(components, string.Join(",", program.Components.Select(c => c.ToString()).Order()));
        Assert.Equal(program.Components.Contains(RuntimeComponent.String), program.Code.Contains("#include \"rgl/standard/string.hpp\""));
        Assert.Equal(program.Components.Contains(RuntimeComponent.Arguments), program.Code.Contains("#include \"rgl/standard/arguments.hpp\""));
        Assert.Equal(program.Components.Contains(RuntimeComponent.Arguments), program.Code.Contains("read_arguments(argc, argv)"));
    }

    [Fact]
    public void Le_rail_d_un_guard_est_un_bloc_qui_recoit_son_erreur()
    {
        var cpp = Emit("guard g([string] a): void {\n    fail \"oups\";\n}\ntrain([string] args) args\n    |> g\n    !> [$ 2]\n    |> done;");

        Assert.Contains("static std::uint32_t rgl_entry_train([[maybe_unused]] const rgl::standard::array<rgl::standard::string>& rgl_p_args)", cpp);
        Assert.Contains("#line 4 \"main.rgl\"\n    [[maybe_unused]] const auto& rgl_start = rgl_p_args;", cpp);
        Assert.Contains("[[maybe_unused]] auto rgl_t2 = rgl_g_g(rgl_start);\n    if (rgl_t2.failed()) { // rail de g\n#line 6 \"main.rgl\"\n        return 2u;\n    }", cpp);
        Assert.Contains("static rgl::standard::guarded<void, rgl_m_std_d_Errors_d_Exception> rgl_g_g(", cpp);
    }

    [Fact]
    public void Un_guard_sans_rail_fait_echouer_le_train()
    {
        var cpp = Emit("guard g(): void {\n    fail;\n}\ntrain |> g |> done;");

        Assert.Contains("if (rgl_t2.failed()) return 1u;", cpp);
    }

    [Fact]
    public void Les_chaines_sont_ecrites_octet_par_octet()
    {
        // « é » fait deux octets en UTF-8 ; « \0 » reste dans la chaîne grâce à la longueur explicite.
        var cpp = Emit(InGear("var s: \"é\\\"\\\\\\0?\";"));

        Assert.Contains("rgl::standard::string(\"\\303\\251\\\"\\\\\\000\\077\", 6)", cpp);
    }

    [Fact]
    public void Les_operations_sont_entierement_parenthesees()
    {
        Assert.Contains("rgl_v_b = ((1 + (2 * 3)) > 4);", Emit(InGear("var b: 1 + 2 * 3 > 4;")));
    }

    [Fact]
    public void Le_chemin_du_source_est_echappe_dans_les_line()
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(new SourceText(@"C:\projet ""x""\main.rgl", "train |> done;"), diagnostics).Tokenize();
        var program = new Binder(diagnostics).Bind(new Parser(tokens, diagnostics).Parse())!;

        Assert.Contains(@"#line 1 ""C:\\projet \""x\""\\main.rgl""", CppEmitter.Emit(program, @"C:\projet ""x""\main.rgl").Code);
    }

    [Fact]
    public void Les_constantes_connues_a_la_compilation_sont_constexpr()
    {
        var cpp = Emit(InGear("const int A: 2 + 3;\nconst int B: A * 2;\nconst int C: 2147483647 + A;\nconst string S: \"x\";\nint d: B;\nconst int E: d;"));

        Assert.Contains("[[maybe_unused]] constexpr std::int32_t rgl_v_A = (2 + 3);", cpp);
        Assert.Contains("[[maybe_unused]] constexpr std::int32_t rgl_v_B = (rgl_v_A * 2);", cpp);
        Assert.Contains("[[maybe_unused]] const std::int32_t rgl_v_C = rgl::standard::checked_add<std::int32_t>(2147483647, rgl_v_A, 1049u);", cpp); // déborde : pas constexpr
        Assert.Contains("[[maybe_unused]] constexpr rgl::standard::string rgl_v_S = rgl::standard::string(\"x\", 1);", cpp);
        Assert.Contains("[[maybe_unused]] std::int32_t rgl_v_d = rgl_v_B;", cpp);
        Assert.Contains("[[maybe_unused]] const std::int32_t rgl_v_E = rgl_v_d;", cpp);           // connue à l'exécution
    }

    [Fact]
    public void Chaque_variable_a_son_propre_nom_cpp()
    {
        var cpp = Emit(InGear("var a: 1;\nundefine a;\nvar a: 2;\n{\n    var a: 3;\n    a <- 4;\n}"));

        Assert.Contains("std::int32_t rgl_v_a = 1;", cpp);
        Assert.Contains("// undefine a : rien à libérer", cpp);
        Assert.Contains("std::int32_t rgl_v_a_2 = 2;", cpp);
        Assert.Contains("std::int32_t rgl_v_a_3 = 3;", cpp);
        Assert.Contains("rgl_v_a_3 = 4;", cpp);
    }

    [Fact]
    public void Une_constante_s_ecrit_directement_dans_son_type()
    {
        var cpp = Emit(InGear("uint u: 40;\nlong l: 4_000_000_000;\nulong m: 7;\nubyte b: 200;\ndouble d: 3;"));

        Assert.Contains("rgl_v_u = 40u;", cpp);
        Assert.Contains("rgl_v_l = 4000000000ll;", cpp);
        Assert.Contains("rgl_v_m = 7ull;", cpp);
        Assert.Contains("rgl_v_b = static_cast<std::uint8_t>(200);", cpp);   // pas de suffixe pour 8 bits
        Assert.Contains("rgl_v_d = 3.0;", cpp);
    }

    [Fact]
    public void Un_template_appelle_interpolate_avec_ses_morceaux()
    {
        var program = EmitProgram(InGear("var a: 1;\nvar t: `a = {a}!`;"));

        Assert.Contains("#include \"rgl/standard/format.hpp\"", program.Code);
        Assert.Contains("rgl_v_t = rgl::standard::interpolate(rgl::standard::string(\"a = \", 4), rgl_v_a, rgl::standard::string(\"!\", 1));", program.Code);
        Assert.Contains(RuntimeComponent.String, program.Components);
    }

    [Fact]
    public void Chaque_symbole_litteral_a_une_seule_entree_statique()
    {
        var program = EmitProgram(InGear("var a: §pret;\nconst symbol B: §pret;\nvar c: §fini;\nvar ok: a eq B;"));

        Assert.Contains("#include \"rgl/standard/symbol.hpp\"", program.Code);
        Assert.Contains("inline constexpr rgl::standard::symbol_entry rgl_s_fini{\"fini\", 4};\ninline constexpr rgl::standard::symbol_entry rgl_s_pret{\"pret\", 4};", program.Code);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program.Code, "symbol_entry rgl_s_pret"));
        Assert.Contains("[[maybe_unused]] rgl::standard::symbol rgl_v_a = rgl::standard::symbol(rgl_s_pret);", program.Code);
        Assert.Contains("[[maybe_unused]] constexpr rgl::standard::symbol rgl_v_B = rgl::standard::symbol(rgl_s_pret);", program.Code);
        Assert.Contains("rgl_v_ok = (rgl_v_a == rgl_v_B);", program.Code);
        Assert.Empty(program.Components);                                   // les littéraux n'ont pas besoin de la table
    }

    [Fact]
    public void Un_operande_qui_modifie_une_variable_est_evalue_dans_l_ordre()
    {
        var cpp = Emit(InGear("var a: 1;\nvar r: a + post_increment a;\nvar s: a > 0 and increment a > 1;"));

        // + n'ordonne pas ses opérandes en C++ : chacun est rangé dans une constante, de gauche à droite.
        Assert.Contains("rgl_v_r = [&]() -> std::int32_t { const auto rgl_t1 = rgl_v_a; const auto& rgl_t2 = rgl::standard::checked_post_increment<std::int32_t>(rgl_v_a, 1, 780u); "
            + "return rgl::standard::checked_add<std::int32_t>(rgl_t1, rgl_t2, 778u); }();", cpp);
        // and ordonne déjà, et n'évalue la droite que si la gauche est vraie : rien à ranger.
        Assert.Contains("rgl_v_s = ((rgl_v_a > 0) && (rgl::standard::checked_increment<std::int32_t>(rgl_v_a, 1, 1042u) > 1));", cpp);
    }

    [Fact]
    public void Les_operations_qui_peuvent_echouer_passent_par_le_runtime()
    {
        string content = InGear("var a: 7;\nvar b: a / a;\nvar c: a % 2;\nvar d: a shl a;\nvar e: invert a;\nincrement<2> a;\nvar f: 10 / 4;\nvar g: a shr 2;");
        var checkedCpp = Emit(content);

        Assert.Contains("rgl_v_b = rgl::standard::checked_div<std::int32_t>(rgl_v_a, rgl_v_a, 778u);", checkedCpp);
        Assert.Contains("rgl_v_c = (rgl_v_a % 2);", checkedCpp);   // diviseur connu, ni 0 ni -1 : rien à vérifier
        Assert.Contains("rgl_v_d = rgl::standard::checked_shl<std::int32_t>(rgl_v_a, rgl_v_a, 1290u);", checkedCpp);
        Assert.Contains("rgl_v_g = (rgl_v_a >> 2);", checkedCpp);   // décalage constant valide : direct
        Assert.Contains("rgl_v_e = static_cast<std::int32_t>(~rgl_v_a);", checkedCpp);
        Assert.Contains("rgl::standard::checked_increment<std::int32_t>(rgl_v_a, 2, 1793u);", checkedCpp);
        Assert.Contains("rgl_v_f = (10 / 4);", checkedCpp);   // calculable : rien à vérifier

        var wrappingCpp = EmitProgram(content, OverflowMode.Unchecked).Code;
        Assert.Contains("rgl::standard::wrapping_div<std::int32_t>(rgl_v_a, rgl_v_a, 778u)", wrappingCpp);   // division par zéro : arrêt dans les deux modes
        Assert.Contains("rgl::standard::wrapping_shl<std::int32_t>(rgl_v_a, rgl_v_a)", wrappingCpp);
        Assert.Contains("rgl::standard::wrapping_increment<std::int32_t>(rgl_v_a, 2)", wrappingCpp);
    }

    [ZigTheory]
    [InlineData("checked")]
    [InlineData("unchecked")]
    public void Une_division_par_zero_arrete_le_programme_dans_les_deux_modes(string mode)
    {
        var (code, output) = CompileAndRun("gear calcul(): uint {\nint z: 0;\nvar q: 10 / z;\nreturn 3;\n}\n!train |> $ calcul();", Enum.Parse<OverflowMode>(mode, ignoreCase: true));

        Assert.NotEqual(3, code);
        Assert.Contains("division entière par zéro (main.rgl:3:11)", output);
    }

    [ZigTheory]
    [InlineData("checked", -1)]          // arrêt
    [InlineData("unchecked", 7)]         // min / -1 fait le tour : min
    public void Min_divise_par_moins_un_deborde(string mode, int expected)
    {
        var (code, output) = CompileAndRun("gear calcul(): uint {\nint m: -2147483647 - 1;\nint d: -1;\nvar q: m / d;\nuint oui: 7;\nuint non: 8;\nreturn q eq m ? oui : non;\n}\n!train |> $ calcul();",
            Enum.Parse<OverflowMode>(mode, ignoreCase: true));

        if (expected < 0) Assert.Contains("débordement d'entier (main.rgl:4:10)", output);
        else Assert.Equal(expected, code);
    }

    [ZigTheory]
    [InlineData("checked", -1)]          // arrêt
    [InlineData("unchecked", 8)]         // 1 shl (35 % 32)
    public void Un_decalage_hors_limites_depend_du_mode(string mode, int expected)
    {
        var (code, output) = CompileAndRun("gear calcul(): uint {\nuint n: 35;\nuint un: 1;\nuint v: un shl n;\nreturn v;\n}\n!train |> $ calcul();", Enum.Parse<OverflowMode>(mode, ignoreCase: true));

        if (expected < 0) Assert.Contains("décalage hors limites (main.rgl:4:12)", output);
        else Assert.Equal(expected, code);
    }

    [Fact]
    public void Un_switch_devient_des_tests_et_des_sauts_vers_ses_cas()
    {
        var cpp = Emit(InGear("var n: 2;\nswitch (n) {\n    case 1:\n        break;\n    case 2, 3:\n        goto 1;\n    default:\n        break;\n}"));

        Assert.Contains("[[maybe_unused]] const auto& rgl_sw1 = rgl_v_n;", cpp);
        Assert.Contains("if (rgl_sw1 == 1) goto rgl_sw1_c0;", cpp);
        Assert.Contains("if ((rgl_sw1 == 2) || (rgl_sw1 == 3)) goto rgl_sw1_c1;", cpp);
        Assert.Contains("goto rgl_sw1_c2;\n", cpp);                     // sinon : default
        Assert.Contains("[[maybe_unused]] rgl_sw1_c1: {", cpp);
        Assert.Contains("goto rgl_sw1_c0;", cpp);                         // goto 1
        Assert.Contains("goto rgl_sw1_end;", cpp);                        // break
        Assert.Contains("[[maybe_unused]] rgl_sw1_end: ;", cpp);
    }

    [Fact]
    public void Un_gear_devient_une_fonction_cpp_declaree_avant_d_etre_definie()
    {
        var cpp = Emit("const uint MAX: 10;\ngear pure borne(uint n): uint => n > MAX ? MAX : n;\ngear salue(string nom): void {\n    return;\n}\n!train |> $ borne(3);");

        Assert.Contains("[[maybe_unused]] static constexpr std::uint32_t rgl_v_MAX = 10u;", cpp);                         // constante du fichier
        Assert.Contains("[[maybe_unused]] static std::uint32_t rgl_g_borne([[maybe_unused]] std::uint32_t rgl_p_n);\n", cpp); // déclaration d'abord
        Assert.Contains("[[maybe_unused]] static void rgl_g_salue([[maybe_unused]] const rgl::standard::string& rgl_p_nom);\n", cpp); // chaîne : référence constante
        Assert.Contains("return ((rgl_p_n > rgl_v_MAX) ? rgl_v_MAX : rgl_p_n);", cpp);
        Assert.DoesNotContain("__builtin_unreachable();", cpp);                                                            // le corps finit déjà par return
        Assert.Contains("return rgl_g_borne(3u);", cpp);                                                                  // appel dans le train
        Assert.DoesNotContain("recursion.hpp", cpp);                                                                       // aucun gear récursif
    }

    [Fact]
    public void Un_switch_dont_chaque_cas_renvoie_finit_par_unreachable()
    {
        // Clang ne voit pas toujours, à travers les sauts du switch, que la fin du corps est inaccessible.
        var cpp = Emit("gear signe(int n): int {\n    switch (n) {\n        case 0:\n            return 0;\n        default:\n            return 1;\n    }\n}\n!train |> done;");

        Assert.Contains("__builtin_unreachable();", cpp);
    }

    [Fact]
    public void Un_gear_recursif_compte_sa_profondeur_sauf_sans_limite()
    {
        const string content = "gear fact(uint n): uint => n <= 1 ? 1 : n * fact(n - 1);\n!train |> $ fact(5);";

        Assert.Contains("[[maybe_unused]] const rgl::standard::recursion_guard rgl_recursion(200u, \"main.rgl:1:6\");", Emit(content));
        Assert.DoesNotContain("recursion_guard", EmitProgram(content, recursionLimit: null).Code);
    }

    [ZigFact]
    public void Une_recursion_trop_profonde_arrete_le_programme()
    {
        var (code, output) = CompileAndRun("gear descend(uint n): uint => n eq 0 ? 0 : descend(n - 1);\n!train |> $ descend(10);", recursionLimit: 5);

        Assert.NotEqual(0, code);
        Assert.Contains("récursion trop profonde, plus de 5 appels imbriqués (main.rgl:1:6)", output);
    }

    [Fact]
    public void Undefine_libere_la_memoire_d_une_chaine()
    {
        Assert.Contains("rgl_v_s = rgl::standard::string();", Emit(InGear("var s: \"texte\";\nundefine s;")));
    }

    [ZigTheory]
    [InlineData("gear calcul(): uint {\nconst uint BASE: 40;\nconst uint DEUX: 2;\nuint code: BASE;\ncode <- code + DEUX;\nreturn code;\n}\n!train |> $ calcul();", 42)]
    [InlineData("gear calcul(): uint {\nuint a: 1;\n{\n    uint a: 2;\n    a <- 3;\n}\nreturn a;\n}\n!train |> $ calcul();", 1)]                 // masquage dans un bloc
    [InlineData("gear calcul(): uint {\nuint code: 40;\ncode <- code + 2;\nvar i: 3;\ndouble d: i;\nlong l: i + 4_000_000_000;\nreturn code;\n}\n!train |> $ calcul();", 42)] // conversions                                                    // après le train : jamais exécuté
    [InlineData("gear calcul(): uint {\nvar s: \"une chaîne bien trop longue pour tenir en place\";\nundefine s;\nvar s: \"ok\";\n"
        + "const string L: \"une constante plus longue que vingt-deux octets\";\n"
        + "{\n    mstring m: m\"x\";\n    undefine m;\n}\nreturn 5;\n}\n!train |> $ calcul();", 5)]                                  // chaînes, constexpr long, undefine
    [InlineData("gear calcul(): uint {\nvar a: 7;\nvar r: 0.1;\nvar t: `a = {a}, r = {r * 3.0}, ok = {a > 2}, {\"texte\"} \\{\\}`;\nundefine t;\nreturn 3;\n}\n!train |> $ calcul();", 3)] // template
    [InlineData("gear calcul(): uint {\nvar a: 2;\nvar t: `dehors {`dedans {a * 3}`} et {\"`\"}`;\nreturn 6;\n}\n!train |> $ calcul();", 6)]                // templates imbriqués
    [InlineData("gear calcul(): uint {\nvar monSymbol: §mykey;\nconst symbol K: §mykey;\nvar ok: §mykey eq monSymbol and K eq monSymbol;\nvar t: `clé = {K}, ok = {ok}`;\nreturn 9;\n}\n!train |> $ calcul();", 9)] // symboles
    [InlineData("gear calcul(): uint {\nuint a: 1;\nuint r: a + post_increment a;\nuint s: post_increment a + a;\nreturn r * 100 + s * 10 + a;\n}\n!train |> $ calcul();", 253)]   // de gauche à droite
    [InlineData("gear calcul(): uint {\nuint a: 1;\nuint b: a > 0 ? post_increment a : post_decrement a;\nuint c: a > 5 ? increment<10> a : increment<3> a;\n"
        + "var ok: a > 100 and increment a > 0;\nreturn b * 100 + a * 10 + c;\n}\n!train |> $ calcul();", 155)]                                                        // une seule branche évaluée
    [InlineData("gear calcul(): uint {\nuint x: 100;\nx /<- 7;\nx +<- 3;\nx *<- 2;\nx -<- 4;\nuint r: x % 7;\nuint s: 1 shl 4 bitor (x shr 1);\nreturn x + r + s;\n}\n!train |> $ calcul();", 63)] // composées, reste, décalages
    [InlineData("gear calcul(): uint {\nvar f: 1.5;\nincrement<2> f;\nuint oui: 7;\nuint non: 8;\nvar g: invert 0b1010 bitand 0xFF;\nreturn f eq 3.5 and g eq 245 ? oui : non;\n}\n!train |> $ calcul();", 7)] // flottant, invert
    [InlineData("gear calcul(): uint {\nubyte b: 200;\nubyte c: b shl 2;\nvar t: `{b shl 2}`;\nreturn c;\n}\n!train |> $ calcul();", 32)]          // décalage constant : ramené à 8 bits
    [InlineData("gear calcul(): uint {\nvar n: 0;\nuint r: 0;\nif (n < 0) {\n    r <- 1;\n} else if (n eq 0) {\n    r <- 2;\n} else {\n    r <- 3;\n}\nreturn r;\n}\n!train |> $ calcul();", 2)]   // if / else if / else
    [InlineData("gear calcul(): uint {\nuint i: 0;\nuint somme: 0;\nswitch (0) {\n    case 0:\n        increment i;\n        somme +<- i;\n        if (i < 10) {\n            goto 0;\n        }\n        break;\n}\nreturn somme;\n}\n!train |> $ calcul();", 55)] // boucle par goto
    [InlineData("gear calcul(): uint {\nvar mot: \"beta\";\nuint r: 0;\nswitch (mot) {\n    case \"alpha\":\n        r <- 1;\n        break;\n    case \"beta\", \"gamma\":\n        switch (§x) {\n            case §y:\n                r <- 2;\n                break;\n"
        + "            case eval $ eq §x:\n                r <- 40;\n                break 1;\n            default:\n                r <- 3;\n                break;\n        }\n        r <- 99;\n    default:\n        r <- 100;\n        break;\n}\nreturn r + 2;\n}\n!train |> $ calcul();", 42)] // chaînes, symboles, case eval, break 1
    [InlineData("gear calcul(): uint {\nuint r: 0;\nswitch (1) {\n    case 1:\n        r +<- 1;\n    case 2:\n        r +<- 10;\n        break;\n    case 3:\n        r +<- 100;\n}\nreturn r;\n}\n!train |> $ calcul();", 11)] // poursuite dans le cas suivant
    [InlineData("gear calcul(): uint {\nvar x: 2.5;\nuint r: 0;\nswitch (x) {\n    case 1.0:\n        r <- 1;\n        break;\n    case eval $ > 2.0:\n        r <- 7;\n        break;\n}\nreturn r;\n}\n!train |> $ calcul();", 7)] // flottant
    [InlineData("gear fact(uint n): uint => n <= 1 ? 1 : n * fact(n - 1);\n!train |> $ fact(5) - 78;", 42)]                                      // récursion
    [InlineData("gear pair(uint n): bool => n eq 0 ? true : impair(n - 1);\ngear impair(uint n): bool => n eq 0 ? false : pair(n - 1);\n"
        + "gear calcul(): uint {\n    if (pair(10) and impair(7)) {\n        return 7;\n    }\n    return 8;\n}\n!train |> $ calcul();", 7)]                    // récursion croisée
    [InlineData("gear twice(uint n): uint => n * 2;\ngear pure add(uint a, uint b): uint => a + b;\ngear calcul(): uint => 20 |> twice |> add(2);\n!train |> $ calcul();", 42)] // pipeline
    [InlineData("gear calcul(): int => 10 |> (n) => n * 4 |> (n) => n + 2;\n!train |> $ calcul() as uint;", 42)] // lambdas de pipeline dans une expression
    [InlineData("emit code(int n): uint => n as uint;\n!train 20 |> (n) => n * 2 |> (n) => n + 2 |> code;", 42)] // lambda comme étape du train
    [InlineData("gear compte([string] a): void { }\ngear message(string m): string => `m = {m}`;\n!train([string] args) args |> compte |> $ 5;", 5)]  // le train appelle un gear
    [InlineData("const uint BASE: 40;\ngear calcul(uint n): uint {\n    switch (n) {\n        case 1:\n            return BASE;\n        default:\n            return BASE + n;\n    }\n}\n!train |> $ calcul(2);", 42)] // return dans un switch
    [InlineData("train |> done;", 0)]
    [InlineData("!train |> $ 3;", 3)]
    [InlineData("!train |> fail;", 1)]
    [InlineData("guard g(): void {\n    fail \"é\";\n}\ntrain |> g !> [$ 2] |> done;", 2)]
    [InlineData("guard g(): void {\n    fail;\n}\nguard h(Exception e): void {\n    fail;\n}\ntrain |> g !> [h !> [done] |> fail] |> done;", 0)]        // rail dans un rail
    [InlineData("guard g([string] a): void {\n    fail `{1 + 2 * 3 > 4 and not false}`;\n}\ntrain([string] args) args |> g !> [$ 7] |> done;", 7)]
    public void Le_cpp_genere_compile_sans_avertissement_et_renvoie_le_bon_code(string content, int expected)
    {
        Assert.Equal(expected, CompileAndRun(content).Code);
    }

    /// <summary>
    /// Compile <paramref name="content"/> avec le runtime et Zig (avertissements interdits), puis
    /// l'exécute, avec <paramref name="input"/> sur son entrée standard s'il est donné.
    /// </summary>
    internal static (int Code, string Output) CompileAndRun(string content, OverflowMode overflow = OverflowMode.Checked, uint? recursionLimit = 200, string? input = null)
    {
        using var output = new TempDirectory();
        var main = Path.Combine(output.Path, "main.cpp");
        File.WriteAllText(main, EmitProgram(content, overflow, recursionLimit).Code);

        var executable = ZigBuild.Compile(output.Path, "programme", warningsAsErrors: true,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"), main);

        return ZigBuild.RunWithInput(executable, input, "un argument");
    }

    [Fact]
    public void Un_mold_devient_un_struct_avec_l_egalite_par_defaut()
    {
        var cpp = Emit("mold Vec2 { double x; double y; }\nmold Inutile { int a; }\nconst Vec2 O: {x: 0.0, y: 0.0};\n"
            + "gear pure egal(Vec2 a, Vec2 b): bool => a eq b;\ntrain |> done;");

        Assert.Contains("struct rgl_m_Vec2 {\n    double rgl_f_x;\n    double rgl_f_y;\n\n    friend bool operator==(const rgl_m_Vec2&, const rgl_m_Vec2&) = default;\n};", cpp);
        Assert.Contains("[[maybe_unused]] static constexpr rgl_m_Vec2 rgl_v_O = rgl_m_Vec2{0.0, 0.0};", cpp);   // connue à la compilation
        Assert.Contains("const rgl_m_Vec2& rgl_p_a", cpp);                                                       // passé par référence constante
        Assert.Contains("return (rgl_p_a == rgl_p_b);", cpp);
        Assert.DoesNotContain("rgl_m_Inutile", cpp);                                                             // jamais utilisé : pas de struct
        Assert.DoesNotContain("rgl_write", cpp);                                                                 // jamais écrit en texte
    }

    [Fact]
    public void Une_valeur_avec_un_effet_evalue_ses_champs_dans_l_ordre_d_ecriture()
    {
        var cpp = Emit(InGear("int n: 0;\nvar a: {y: increment n, x: increment n};"));

        // Le mold anonyme range ses champs par nom (x, y), mais y est écrit, donc évalué, en premier.
        Assert.Contains("const auto& rgl_t1 = rgl::standard::checked_increment<std::int32_t>(rgl_v_n, 1, 780u); const auto& rgl_t2 = rgl::standard::checked_increment<std::int32_t>(rgl_v_n, 1, 796u); return rgl_a_1{rgl_t2, rgl_t1};", cpp);
    }

    /// <summary>Molds : défauts, new, raccourci, spread, duck typing, égalité, texte, constante, switch, Exception.</summary>
    private const string MoldTrain = """
        mold Vec2 { double x; double y; }
        mold Vec3 { ...Vec2; double z: 0.0; }
        mold Fiche { string nom; Vec2 position; int niveau: 1; }

        const Vec2 ORIGINE: {x: 0.0, y: 0.0};

        gear pure longueur2(Vec2 v): double => v.x * v.x + v.y * v.y;

        gear deplacer(Vec2 v, double dx): Vec2 => {...v, x: v.x + dx};

        gear classer(Vec2 v): int {
            switch (v) {
                case ORIGINE:
                    return 0;
                default:
                    return 1;
            }
        }

        guard verifier(Vec3 p): Vec3 {
            if (p.z > 0.0) {
                fail `z positif : {p}`;
            }
            done;
        }

        emit afficher(Vec3 p): uint {
            var x: 3.0;
            var y: 4.0;
            Vec2 r: {x, y};
            var f: new Fiche {nom: "Héros", position: p};
            `{p} {p eq {x: 1.0, y: 2.0}} {longueur2(p)} {deplacer(r, 1.0)} {classer(ORIGINE)} {classer(r)}|` -> console;
            f -> console;
            return 0;
        }

        emit signaler(Exception e): uint {
            `{e.message} @ {e.where}` -> console;
            return 3;
        }

        train new Vec3 {x: 1.0, y: 2.0, z: Z} |> verifier !> [signaler] |> afficher;
        """;

    [ZigTheory]
    [InlineData("0.0", 0, "{x: 1.0, y: 2.0, z: 0.0} true 5.0 {x: 4.0, y: 4.0} 0 1|{nom: \"Héros\", position: {x: 1.0, y: 2.0}, niveau: 1}")]
    [InlineData("1.0", 3, "z positif : {x: 1.0, y: 2.0, z: 1.0} @ main.rgl:22:9")]
    public void Les_molds_se_construisent_se_comparent_et_s_ecrivent(string z, int code, string output)
    {
        var result = CompileAndRun(MoldTrain.Replace("Z}", z + "}"));

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    /// <summary>Nullables : as?, rétrécissement, ??, ?°, ?., null dans un mold et dans un template.</summary>
    private const string NullableTrain = """
        mold Profil { string nom; int? age: null; }

        const int? AUCUN: null;

        gear lire(string texte): int {
            var n: texte as? int;
            if (n eq null) {
                return -1;
            }
            return n * 2;
        }

        gear decrire(Profil? p): string => `{p?.nom ?? "inconnu"} ({p?.age ?? 0})`;

        gear positif(int? n): bool => n not eq null and n > 0;

        guard moitie(string texte): int {
            var n: (texte as? int) ?? (texte as! int);
            var d: ("x" as? int) ?? 10;
            return n / d;
        }

        emit afficher(int m): uint {
            string? vide: "";
            string? rien: null;
            Profil? personne: {nom: "Ada", age: 36};
            int? compte: null;
            compte <- 5;
            `{m}|{lire("21")} {lire("abc")} {decrire(personne)} {decrire(null)} {positif(3)} {positif(null)}|` -> console;
            `{vide ?° "défaut"} {rien ?° "défaut"} {AUCUN} {AUCUN ?? 7} {personne} {rien} {compte + 1}` -> console;
            return 0;
        }

        train "20" |> moitie |> afficher;
        """;

    [ZigFact]
    public void Les_nullables_se_testent_se_remplacent_et_s_ecrivent()
    {
        var result = CompileAndRun(NullableTrain);

        Assert.Equal((0, "2|42 -1 Ada (36) inconnu (0) true false|défaut défaut null 7 {nom: \"Ada\", age: 36} null 6"), (result.Code, result.Output));
    }

    /// <summary>Unions, résultats et match : valeur, instruction avec return, default dans un guard, ok/err, ??, élargissement.</summary>
    private const string UnionTrain = """
        mold Point { int x; int y; }

        gear decrire(int | string | Point v): string => match (v) {
            int i |> `entier {i}`;
            string s |> `texte {s}`;
            Point p |> `point {p}`;
        };

        gear taille(int | string v): int {
            match (v) {
                int i |> { return i; }
                string s |> return 0;
            }
        }

        gear diviser(int a, int b): int ! string {
            if (b eq 0) {
                return err("division par zéro");
            }
            return a / b;
        }

        gear expliquer(int ! string r): string => match (r) {
            ok v |> `résultat {v}`;
            err e |> `erreur : {e}`;
        };

        guard premier(int | bool v): int {
            var n: match (v) {
                int i |> i;
                default |> 0;
            };
            if (n < 0) {
                fail "négatif";
            }
            return n;
        }

        emit afficher(int n): uint {
            int | string a: 42;
            int | string | Point c: {x: 1, y: 2};
            int | string | Point d: a;
            var r: diviser(7, 2);
            `{n}|{decrire(a)}|{decrire("x")}|{decrire(c)}|{decrire(d)}|{taille("x")} {taille(7)}|` -> console;
            `{expliquer(r)}|{expliquer(diviser(1, 0))}|{r ?? -1} {diviser(1, 0) ?? -1}|{r} {a} {a eq d}` -> console;
            return 0;
        }

        train 5 |> premier |> afficher;
        """;

    [ZigFact]
    public void Les_unions_et_les_resultats_se_resolvent_par_match()
    {
        var result = CompileAndRun(UnionTrain);

        Assert.Equal((0, "5|entier 42|texte x|point {x: 1, y: 2}|entier 42|0 7|résultat 3|erreur : division par zéro|3 -1|ok(3) 42 true"), (result.Code, result.Output));
    }

    /// <summary>Enums : sans type (ubyte, bool), avec type int et string, case court et qualifié, goto, mold, texte.</summary>
    private const string EnumTrain = """
        enum Direction { North, East, South, West }
        enum Interrupteur { Off, On }
        enum Status: int { Idle: 0, Running: 1, Error: -1 }
        enum Couleur: string { Rouge: "rouge", Vert: "vert" }

        mold Robot { string nom; Direction cap: Direction.North; Status etat: Status.Idle; }

        const Direction DEPART: Direction.East;

        gear tourner(Direction d): Direction {
            switch (d) {
                case North:
                    return Direction.East;
                case Direction.East:
                    return Direction.South;
                case South:
                    goto West;
                case West:
                    return Direction.North;
            }
        }

        gear code(Status s): int {
            switch (s) {
                case Running:
                    return 1;
                default:
                    return 0;
            }
        }

        emit afficher(): uint {
            var r: new Robot {nom: "R2", etat: Status.Running};
            var i: Interrupteur.On;
            `{tourner(DEPART)} {tourner(Direction.South)} {r} {i} {i eq Interrupteur.Off} {code(r.etat)} {Couleur.Vert}` -> console;
            return 0;
        }

        !train |> afficher;
        """;

    [ZigFact]
    public void Les_enums_se_comparent_et_s_ecrivent_par_leur_nom()
    {
        var result = CompileAndRun(EnumTrain);

        Assert.Equal((0, "South North {nom: \"R2\", cap: North, etat: Running} On false 1 Vert"), (result.Code, result.Output));
    }

    [Fact]
    public void Un_enum_devient_un_enum_class_sur_son_type()
    {
        var cpp = Emit("enum Status: int { Idle: 0, Error: -1 }\nenum Pile { Face, Dos }\nenum Inutile { A }\n"
            + "gear f(Status s, Pile p): bool => s eq Status.Error and p eq Pile.Dos;\ntrain |> done;");

        Assert.Contains("enum class rgl_e_Status : std::int32_t {\n    rgl_c_Idle = 0,\n    rgl_c_Error = -1,\n};", cpp);
        Assert.Contains("enum class rgl_e_Pile : bool {\n    rgl_c_Face = false,\n    rgl_c_Dos = true,\n};", cpp);
        Assert.Contains("(rgl_p_s == rgl_e_Status::rgl_c_Error)", cpp);
        Assert.DoesNotContain("rgl_e_Inutile", cpp);   // jamais utilisé
        Assert.DoesNotContain("rgl_write", cpp);       // jamais écrit en texte : aucun nom dans le binaire
    }

    [Fact]
    public void Une_chaine_litterale_longue_est_une_constante_ecrite_une_fois()
    {
        var cpp = Emit(InGear("var a: \"une phrase de plus de vingt-deux octets\";\nvar b: \"une phrase de plus de vingt-deux octets\";\nvar c: \"courte\";"));

        // Une vue sur le littéral : aucune allocation à l'exécution, et une seule constante pour deux usages.
        Assert.Contains("static constexpr rgl::standard::string rgl_l_1 = rgl::standard::string(\"une phrase de plus de vingt-deux octets\", 39);", cpp);
        Assert.Contains("rgl_v_a = rgl_l_1;", cpp);
        Assert.Contains("rgl_v_b = rgl_l_1;", cpp);
        Assert.DoesNotContain("rgl_l_2", cpp);
        Assert.Contains("rgl_v_c = rgl::standard::string(\"courte\", 6);", cpp);   // petite : déjà sans allocation
    }

    [Fact]
    public void Un_template_dans_un_template_ecrit_ses_morceaux_directement()
    {
        var cpp = Emit(InGear("var n: 1;\nvar t: `a{`b{n}`}c`;"));

        Assert.Contains("rgl::standard::interpolate(rgl::standard::string(\"a\", 1), rgl::standard::string(\"b\", 1), rgl_v_n, rgl::standard::string(\"c\", 1))", cpp);
    }

    [Fact]
    public void Un_match_qui_reaffecte_sa_valeur_en_garde_une_copie()
    {
        var cpp = Emit(InGear("int | string v: 1;\nmatch (v) {\n    int i |> { v <- \"x\"; }\n    default |> { }\n}"));

        Assert.Contains("const auto rgl_t1 = rgl_v_v;", cpp);   // une référence verrait la nouvelle valeur
    }

    [ZigTheory]
    [InlineData("var a: 1;\nvar m: {x: a, y: increment a};\n`{m.x} {m.y}` -> console;", "1 2")]         // x lu avant l'incrément
    [InlineData("var a: 1;\nvar s: `{a} {increment a} {a}`;\ns -> console;", "1 2 2")]                   // de gauche à droite
    [InlineData("var t: `{\"une phrase de plus de vingt-deux octets\"}{1}`;\nt -> console;", "une phrase de plus de vingt-deux octets1")]
    public void Les_valeurs_sont_lues_dans_l_ordre_et_les_chaines_construites_une_fois(string body, string output)
    {
        var result = CompileAndRun("emit test(): uint {\n" + body + "\nreturn 0;\n}\n!train |> test;");

        Assert.Equal((0, output), (result.Code, result.Output));
    }

    [ZigTheory]
    [InlineData("var i: 300;\nvar b: i as ubyte;\n`{b}` -> console;", "checked", "conversion impossible : la valeur ne tient pas dans un ubyte (main.rgl:3:8)")]
    [InlineData("var i: 300;\nvar b: i as ubyte;\n`{b}` -> console;", "unchecked", "=44")]              // bits de poids fort perdus
    [InlineData("var d: -2.9;\nvar n: d as int;\n`{n}` -> console;", "unchecked", "=-2")]              // tronqué vers zéro
    [InlineData("var d: 1.0e10;\nvar n: d as int;\n`{n}` -> console;", "unchecked", "conversion impossible : la valeur ne tient pas dans un int (main.rgl:3:8)")]
    [InlineData("var i: 300;\nvar b: i as? ubyte;\nvar c: 30 as? ubyte;\n`{b} {c}` -> console;", "checked", "=null 30")]
    [InlineData("var d: 1.0e300;\nvar f: d as float;\n`{f}` -> console;", "checked", "=inf")]          // au-delà du plus grand float
    [InlineData("var t: \"12\";\nvar n: t as? int<0..10>;\nvar m: \"7\" as? int<0..10>;\n`{n} {m}` -> console;", "checked", "=null 7")]
    [InlineData("int<0..10> p: 9;\nincrement p;\nincrement p;", "checked", "valeur hors des bornes de int<0..10> (main.rgl:4:1)")]
    [InlineData("var i: 11;\nint<0..10> p: i;", "checked", "valeur hors des bornes de int<0..10> (main.rgl:3:15)")]
    public void Un_cast_ou_une_borne_qui_echoue_arrete_le_programme(string body, string mode, string expected)
    {
        var (code, output) = CompileAndRun("emit test(): uint {\n" + body + "\nreturn 0;\n}\n!train |> test;", Enum.Parse<OverflowMode>(mode, ignoreCase: true));

        if (expected.StartsWith('=')) Assert.Equal((0, expected[1..]), (code, output));
        else
        {
            Assert.NotEqual(0, code);
            Assert.Contains(expected, output);
        }
    }

    [ZigTheory]
    [InlineData("5", "ok 5")]
    [InlineData("300", "conversion impossible : la valeur ne tient pas dans un byte (main.rgl:4:12)")]
    [InlineData("20", "valeur hors des bornes de int<0..10> (main.rgl:5:23)")]
    public void Dans_un_guard_un_cast_ou_une_borne_qui_echoue_fait_echouer_le_guard(string input, string expected)
    {
        var (code, output) = CompileAndRun(
            "guard octet(): byte {\n    var t <- console;\n    var n: t as! int;\n    var b: n as! byte;\n    int<0..10> petit: n;\n    return b;\n}\n"
            + "emit afficher(byte b): uint {\n    `ok {b}` -> console;\n    return 0;\n}\n"
            + "emit erreur(CastError e): uint {\n    `{e}` -> console;\n    return 3;\n}\n"
            + "train |> octet !> [erreur] |> afficher;", input: input);

        Assert.Equal((expected.StartsWith("ok") ? 0 : 3, expected), (code, output));
    }

    [ZigFact]
    public void Un_ingot_s_execute_comme_sa_base()
    {
        var (code, output) = CompileAndRun(
            "ingot UserId: int;\ningot Angle: float<0.0..360.0>;\nmold P { string nom; }\ningot Joueur: P;\n"
            + "emit test(): uint {\n    UserId uid: 42;\n    var brut: uid°;\n    uid °<- brut + 1;\n    var suivant: uid + 1;\n"
            + "    Joueur j: {nom: \"Héros\"};\n    var a: Angle(90.0);\n    `{uid} {suivant} {j.nom} {j} {a}` -> console;\n    return 0;\n}\n!train |> test;");

        Assert.Equal((0, "43 44 Héros {nom: \"Héros\"} 90.0"), (code, output));
    }

    [Fact]
    public void Un_cast_qui_tient_toujours_ou_connu_s_ecrit_sans_controle()
    {
        var cpp = Emit(InGear("var i: 1;\nvar l: i as long;\nvar n: 3.7 as int;\nint<0..10> p: 5;\nint<0..100> q: p;\nvar b: p as byte;"));

        Assert.Contains("rgl_v_l = static_cast<std::int64_t>(rgl_v_i);", cpp);
        Assert.Contains("rgl_v_n = 3;", cpp);
        Assert.Contains("rgl_v_q = rgl_v_p;", cpp);                                 // bornes contenues
        Assert.Contains("rgl_v_b = static_cast<std::int8_t>(rgl_v_p);", cpp);        // 0..10 tient dans un byte
        Assert.DoesNotContain("checked_bounds", cpp);
    }

    /// <summary>
    /// Une error déclarée, la propagation d'un guard à l'autre, et un rail par guard : celui de
    /// doubler reçoit l'union de ses erreurs et la lit avec match.
    /// </summary>
    private const string ErrorTrain = """
        error Negatif { int valeur; }

        guard lire(): int {
            var t <- console;
            return t as! int;
        }

        guard positif(int n): int {
            if (n < 0) {
                fail new Negatif {valeur: n, message: "négatif"};
            }
        }

        guard doubler(int n): int {
            var p: positif(n);
            if (p > 1000) {
                fail "trop grand";
            }
            return p * 2;
        }

        emit afficher(int n): uint {
            `ok {n}` -> console;
            return 0;
        }

        emit lecture(CastError e): uint {
            `lecture : {e}` -> console;
            return 2;
        }

        emit rapport(Exception | Negatif e): uint {
            match (e) {
                Negatif n |> { `négatif {n.valeur} : {n}` -> console; }
                Exception x |> { `autre : {x}` -> console; }
            }
            return 3;
        }

        train |> lire !> [lecture] |> doubler !> [rapport] |> afficher;
        """;

    [ZigTheory]
    [InlineData("5", 0, "ok 10")]
    [InlineData("abc", 2, "lecture : « abc » n'est pas un int (main.rgl:5:12)")]
    [InlineData("-4", 3, "négatif -4 : négatif (main.rgl:10:9)")]           // propagé depuis positif
    [InlineData("2000", 3, "autre : trop grand (main.rgl:17:9)")]
    public void Chaque_guard_envoie_ses_erreurs_a_son_rail(string input, int code, string output)
    {
        var result = CompileAndRun(ErrorTrain, input: input);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    [ZigFact]
    public void Un_champ_commun_d_une_union_de_molds_se_lit_sur_le_membre_present()
    {
        var (code, output) = CompileAndRun("mold A { string name; int x; uint lvl; }\nmold B { string name; string x; uint lvl; bool boss: true; }\n"
            + "gear decrire(A | B v): string => `{v.name} {v.lvl} {v.x}`;\n"
            + "emit afficher(): uint {\n    A | B a: new A {name: \"Slime\", x: 3, lvl: 1};\n    A | B b: new B {name: \"Dragon\", x: \"feu\", lvl: 75};\n"
            + "    `{decrire(a)} / {decrire(b)}` -> console;\n    return b.lvl;\n}\n!train |> afficher;");

        Assert.Equal((75, "Slime 1 3 / Dragon 75 \"feu\""), (code, output));
    }

    /// <summary>Un guard qui valide un nombre, un emit qui l'écrit, et un rail qui écrit l'erreur et renvoie 3.</summary>
    private const string PositiveTrain = """
        guard positif(int n): int {
            if (n < 0) {
                fail `{n} est négatif`;
            }
            done;
        }

        emit afficher(int n): void {
            `ok {n}` -> console;
        }

        emit signaler(Exception e): uint {
            `erreur : {e}` -> console;
            return 3;
        }

        train VALEUR |> positif !> [signaler] |> afficher;
        """;

    [ZigTheory]
    [InlineData("5", 0, "ok 5")]
    [InlineData("-5", 3, "erreur : -5 est négatif (main.rgl:3:9)")]
    public void Un_guard_qui_echoue_envoie_son_exception_au_rail(string value, int code, string output)
    {
        var result = CompileAndRun(PositiveTrain.Replace("VALEUR", value));

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    /// <summary>Lit deux nombres sur la console et écrit leur quotient : saisie invalide et division par zéro partent sur le rail.</summary>
    private const string DivisionTrain = """
        gear lire(): string {
            var ligne <- console;
            return ligne;
        }

        guard diviser(): int {
            var a: lire() as! int;
            return a / (lire() as! int);
        }

        emit afficher(int n): void {
            `{n}` -> console;
        }

        emit signaler(CastError | Exception e): uint {
            `{e}` -> console;
            return 2;
        }

        train |> diviser !> [signaler] |> afficher;
        """;

    [ZigTheory]
    [InlineData("84\n2\n", 0, "42")]
    [InlineData("1\r\n0\r\n", 2, "division entière par zéro (main.rgl:8:14)")]
    [InlineData("douze\n2\n", 2, "« douze » n'est pas un int (main.rgl:7:12)")]
    [InlineData("12\n", 2, "«  » n'est pas un int (main.rgl:8:17)")]            // fin d'entrée : une ligne vide
    public void Un_guard_lit_la_console_et_echoue_proprement(string input, int code, string output)
    {
        var result = CompileAndRun(DivisionTrain, input: input);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    [ZigTheory]
    [InlineData("guard echoue(): void {\n    fail;\n}\ntrain |> echoue |> done;", 1, "")]                       // aucun rail : code 1
    [InlineData("guard echoue(): void {\n    fail \"a\";\n}\nguard relance(Exception e): void {\n    fail `b, après {e}`;\n}\n"
        + "emit signaler(Exception e): uint {\n    `{e}` -> console;\n    return 4;\n}\n"
        + "train |> echoue !> [relance !> [signaler] |> done] |> done;", 4, "b, après a (main.rgl:2:5) (main.rgl:5:5)")] // un rail dans le rail
    [InlineData("guard rien(): void {\n}\nemit code(): uint {\n    return 9;\n}\ntrain |> rien |> code;", 9, "")]   // guard void, emit uint
    [InlineData("gear pure doubler(int n): int => n * 2;\nguard garde(int n): int => n + 1;\nemit ecrire(int n): void {\n    n -> console;\n}\n"
        + "train 20 |> doubler |> garde |> doubler |> ecrire;", 0, "82")]                                                      // gears et guards mêlés
    [InlineData("guard compte(int n): int {\n    var i: n;\n    return increment i + (i / (n - 3));\n}\n"
        + "emit ecrire(int n): void {\n    n -> console;\n}\ntrain 3 |> compte !> [$ 5] |> ecrire;", 5, "")]                   // division séquencée dans un guard
    public void Les_guards_les_emits_et_les_rails_s_executent(string content, int code, string output)
    {
        var result = CompileAndRun(content);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    [Fact]
    public void Une_operation_entiere_qui_peut_deborder_passe_par_le_runtime()
    {
        string content = InGear("var a: 1;\nvar b: a + 2;\nvar c: -a;\nvar d: 1 + 2;\nvar e: 1.5 * 2.0;");

        var checkedCpp = EmitProgram(content).Code;
        Assert.Contains("#include \"rgl/standard/arithmetic.hpp\"", checkedCpp);
        Assert.Contains("rgl::standard::checked_add<std::int32_t>(rgl_v_a, 2, 778u)", checkedCpp);
        Assert.Contains("rgl::standard::checked_negate<std::int32_t>(rgl_v_a, 1032u)", checkedCpp);
        Assert.Contains("rgl_v_d = (1 + 2);", checkedCpp);        // calculée à la compilation : ne déborde pas
        Assert.Contains("rgl_v_e = (1.5 * 2.0);", checkedCpp);    // flottant : pas de débordement d'entier

        Assert.Contains("rgl::standard::wrapping_add<std::int32_t>(rgl_v_a, 2)", EmitProgram(content, OverflowMode.Unchecked).Code);
        Assert.DoesNotContain("arithmetic.hpp", Emit(InGear("var d: 1 + 2;")));
    }

    [ZigFact]
    public void En_mode_checked_un_debordement_arrete_le_programme_avec_sa_position()
    {
        var (code, output) = CompileAndRun("gear calcul(): uint {\nubyte b: 250;\nb <- b + 10;\nreturn b;\n}\n!train |> $ calcul();");

        Assert.NotEqual(0, code);
        Assert.Contains("débordement d'entier (main.rgl:3:8)", output);
    }

    [ZigFact]
    public void En_mode_unchecked_un_debordement_fait_le_tour_du_type()
    {
        Assert.Equal(4, CompileAndRun("gear calcul(): uint {\nubyte b: 250;\nb <- b + 10;\nreturn b;\n}\n!train |> $ calcul();", OverflowMode.Unchecked).Code);
    }

    /// <summary>Tableaux, dictionnaire, tuple et mold qui en contient, écrits en texte.</summary>
    private const string CollectionTrain = """
        mold Equipe { string nom; [string] membres; }

        gear somme([int] valeurs, uint i): int {
            if (i >= valeurs.length) {
                return 0;
            }
            return valeurs[i] + somme(valeurs, i + 1);
        }

        emit afficher(): uint {
            var nums: [1, 2, 3];
            nums.push(4);
            var plus: [...nums, 10, ...nums];
            [int;3] trois: [7, 8, 9];
            [string+] chefs: ["Alix"];
            [int;0..2] petit: [];
            petit.push(5);
            var ages: ["Zoé": 7, "Alix": 12];
            ages.add("Bob", 30);
            var paire: (x: 1, y: "un");
            var equipe: new Equipe {nom: "rouge", membres: ["a", "b"]};
            `{nums} {plus.length} {somme(plus, 0)} {trois[2]} {chefs} {petit} {ages} {ages["Bob"]} {ages.has("Eve")} {paire} {paire.x} {paire.Item2} {equipe}` -> console;
            return 0;
        }

        !train |> afficher;
        """;

    [ZigFact]
    public void Les_collections_s_executent_et_s_ecrivent_en_texte()
    {
        var result = CompileAndRun(CollectionTrain);

        Assert.Equal((0, "[1, 2, 3, 4] 9 30 9 [\"Alix\"] [5] [\"Alix\": 12, \"Bob\": 30, \"Zoé\": 7] 30 false (1, \"un\") 1 un {nom: \"rouge\", membres: [\"a\", \"b\"]}"),
            (result.Code, result.Output));
    }

    [ZigFact]
    public void Un_index_negatif_compte_depuis_la_fin_d_un_tableau_ou_d_un_texte()
    {
        var result = CompileAndRun("""
            gear dernier([int] xs, int i): int => xs[i];

            emit afficher(): uint {
                var nums: [10, 20, 30];
                [int;3] trois: [7, 8, 9];
                string mot: "héros";
                `{nums[0]} {nums[-1]} {nums[-3]} {trois[-1]} {trois[-3]} {dernier(nums, -2)} {mot.length} {mot[0]} {mot[1]} {mot[-1]} {mot[-5]}` -> console;
                return 0;
            }

            !train |> afficher;
            """);

        Assert.Equal((0, "10 30 10 9 7 20 5 h é s h"), (result.Code, result.Output));
    }

    [ZigFact]
    public void Un_index_negatif_hors_limites_arrete_le_programme()
    {
        var result = CompileAndRun("gear lire([int] xs, int i): int => xs[i];\n!train |> $ lire([1, 2, 3], -4) as uint;");

        Assert.NotEqual(0, result.Code);
    }

    [Fact]
    public void Un_tableau_de_taille_fixe_constant_est_constexpr()
    {
        var cpp = Emit(InGear("const [int;3] t: [1, 2, 3];\nvar x: t[1];"));

        Assert.Contains("constexpr rgl::standard::fixed_array<std::int32_t, 3> rgl_v_t = rgl::standard::fixed_array<std::int32_t, 3>{1, 2, 3};", cpp);
        Assert.Contains("rgl_v_x = (rgl_v_t)[1];", cpp);   // index connu et valide : lecture directe
    }

    [ZigTheory]
    [InlineData("var a: [1, 2];\nvar i: 2;\nvar x: a[i];", "index 2 hors limites pour un tableau de 2 élément(s) (main.rgl:4:8)")]
    [InlineData("var d: [\"a\": 1];\nvar x: d[\"b\"];", "clé absente du dictionnaire (main.rgl:3:8)")]
    [InlineData("[int;0..1] p: [];\np.push(1);\np.push(2);", "ajout dans un tableau borné déjà plein (1 élément(s)) (main.rgl:4:1)")]
    [InlineData("var d: [\"a\": 1];\nd.add(\"a\", 2);", "clé déjà présente dans le dictionnaire (main.rgl:3:1)")]
    [InlineData("var a: [1, 2];\n[int;3] t: a as [int;3];", "(main.rgl:3:12)")]
    [InlineData("var a: [1, 2];\nvar t: a as? [int;3];\nvar u: a as? [int;2];\n`{t} {u}` -> console;", "=null [1, 2]")]
    public void Une_lecture_ou_un_ajout_hors_limites_arrete_le_programme(string body, string expected)
    {
        var (code, output) = CompileAndRun("emit test(): uint {\n" + body + "\nreturn 0;\n}\n!train |> test;");

        if (expected.StartsWith('=')) Assert.Equal((0, expected[1..]), (code, output));
        else
        {
            Assert.NotEqual(0, code);
            Assert.Contains(expected, output);
        }
    }

    /// <summary>Un guard qui lit un index sur la console et un autre qui veut exactement trois éléments.</summary>
    private const string IndexTrain = """
        guard lire([int] valeurs): int {
            var t <- console;
            var i: t as! int;
            return valeurs[i];
        }

        guard trois([int] valeurs): [int;3] {
            return valeurs;
        }

        emit afficher(int n): uint {
            `ok {n}` -> console;
            return 0;
        }

        emit erreur(CastError | Exception e): uint {
            `{e}` -> console;
            return 2;
        }

        emit triple([int;3] t): uint {
            `{t}` -> console;
            return 0;
        }

        train VALEURS |> ETAPE !> [erreur] |> SUITE;
        """;

    [ZigTheory]
    [InlineData("[10, 20, 30]", "lire", "afficher", "1", 0, "ok 20")]
    [InlineData("[10, 20, 30]", "lire", "afficher", "7", 2, "index hors limites (main.rgl:4:12)")]
    [InlineData("[10, 20, 30]", "lire", "afficher", "x", 2, "« x » n'est pas un int (main.rgl:3:12)")]
    [InlineData("[1, 2, 3]", "trois", "triple", "", 0, "[1, 2, 3]")]
    [InlineData("[1, 2]", "trois", "triple", "", 2, "la taille du tableau ne convient pas à [int;3] (main.rgl:8:12)")]
    public void Dans_un_guard_un_index_ou_une_taille_invalide_fait_echouer_le_guard(string values, string step, string next, string input, int code, string output)
    {
        var result = CompileAndRun(IndexTrain.Replace("VALEURS", values).Replace("ETAPE", step).Replace("SUITE", next), input: input);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    /// <summary>Lambdas : captures par copie, gear passé par son nom, champ de mold, tableau, pure, nullable.</summary>
    private const string LambdaTrain = """
        mold Bouton { string nom; (int) => int action; }

        gear pure estPair(int n): bool => n % 2 eq 0;

        gear garder([int] valeurs, (int) => bool test): [int] {
            [int] gardes: [];
            return garder2(valeurs, test, 0, gardes);
        }

        gear garder2([int] valeurs, (int) => bool test, uint i, [int] acc): [int] {
            if (i >= valeurs.length) {
                return acc;
            }
            if (test(valeurs[i])) {
                return garder2(valeurs, test, i + 1, [...acc, valeurs[i]]);
            }
            return garder2(valeurs, test, i + 1, acc);
        }

        gear fabrique(int n): (int) => int {
            return (int x) => x + n;
        }

        gear pure applique(pure (int) => int f, int v): int => f(v);

        emit afficher(): uint {
            var seuil: 10;
            var grand: (int v) => v > seuil;
            seuil <- 100;
            var nums: [3, 12, 8, 40];
            var log: (string m) => { `[{m}]` -> console; };
            log("début");
            [(int) => int] ops: [(x) => x + 1, fabrique(10), (x) => x * x];
            var b: new Bouton {nom: "ok", action: (x) => x - 1};
            ((int) => bool)? peutEtre: null;
            var vide: peutEtre eq null;
            `{garder(nums, grand)} {garder(nums, estPair)} {ops[1](5)} {ops[2](7)} {b.action(3)} {applique((x) => x * 3, 5)} {fabrique(2)(5)} {vide}` -> console;
            return 0;
        }

        !train |> afficher;
        """;

    [ZigFact]
    public void Les_lambdas_capturent_par_copie_et_s_appellent()
    {
        var result = CompileAndRun(LambdaTrain);

        Assert.Equal((0, "[début][12, 40] [12, 8, 40] 15 49 2 15 7 true"), (result.Code, result.Output));
    }

    /// <summary>Une constante lambda du fichier, une lambda dans un guard, une grande capture, des lambdas imbriquées, un switch.</summary>
    private const string NestedLambdaTrain = """
        const (int) => int carre: (x) => x * x;

        guard choisir([int] valeurs): int {
            var t <- console;
            var i: t as! int;
            var lire: (int k) => valeurs[k];
            return lire(i);
        }

        emit afficher(int n): uint {
            var a: 1;
            var b: 2;
            var c: 3;
            var d: 4;
            var e: "un texte de plus de vingt-deux octets";
            var gros: (int x) => `{e} {a + b + c + d + x}`;
            var ajouteur: (int k) => (int x) => x + k + n;
            var plus5: ajouteur(5);
            var nom: (int v) => {
                switch (v) {
                    case 1:
                        return "un";
                    case 2:
                        return "deux";
                    default:
                        return "beaucoup";
                }
            };
            var compte: 0;
            var tic: (int v) => {
                if (v < 0) {
                    return;
                }
                `<{v}>` -> console;
            };
            tic(-1);
            tic(9);
            `{gros(10)} {plus5(1)} {nom(2)} {nom(7)} {carre(n)}` -> console;
            return 0;
        }

        train [10, 20, 30] |> choisir |> afficher;
        """;

    [ZigTheory]
    [InlineData("1", 0, "<9>un texte de plus de vingt-deux octets 20 26 deux beaucoup 400")]
    [InlineData("5", -1, "index 5 hors limites pour un tableau de 3 élément(s) (main.rgl:6:26)")]   // une lambda n'est pas un guard : elle arrête le programme
    public void Les_lambdas_imbriquees_et_dans_un_guard_s_executent(string input, int code, string output)
    {
        var result = CompileAndRun(NestedLambdaTrain, input: input);

        if (code == 0) Assert.Equal((0, output), (result.Code, result.Output));
        else
        {
            Assert.NotEqual(0, result.Code);
            Assert.Contains(output, result.Output);
        }
    }

    [Fact]
    public void Une_lambda_devient_une_function_du_runtime()
    {
        var program = EmitProgram(InGear("var n: 2;\nvar f: (int x) => x * n;\nvar m: f(3);"));

        Assert.Contains("#include \"rgl/standard/function.hpp\"", program.Code);
        Assert.Contains("rgl::standard::function<std::int32_t(std::int32_t)>([=]([[maybe_unused]] std::int32_t rgl_p_x) mutable -> std::int32_t {", program.Code);
        Assert.Contains("rgl_v_m = (rgl_v_f)(3);", program.Code);
        Assert.Contains(RuntimeComponent.Memory, program.Components);
    }

    /// <summary>Parcours dans un train (rail par élément, |!|> avec rail) et dans un emit (guard, lambda, gear void).</summary>
    private const string EachTrain = """
        gear pure doubler(int n): int => n * 2;
        gear pure ajouter(int n, int k): int => n + k;

        guard positif(int n): int {
            if (n < 0) {
                fail `{n} est négatif`;
            }
            done;
        }

        guard tous([int] v): [int] {
            return v |!|> positif;
        }

        gear afficher(int n): void {
            `<{n}>` -> console;
        }

        emit journal(Exception e): void {
            `[{e.message}]` -> console;
        }

        emit rapport(Exception e): uint {
            `rapport : {e.message}` -> console;
            return 4;
        }

        emit fin([int] v): uint {
            `{v} ` -> console;
            var gardes: [3, -1, 4] ||> positif;
            var carres: v ||> (x) => x * x ||> ajouter(1);
            `{gardes} {carres} ` -> console;
            v ||> afficher;
            return 0;
        }

        train [1, -2, 3, -4] ||> positif !> [journal] ||> doubler |!|> positif !> [rapport] |> fin;
        """;

    [ZigFact]
    public void Un_parcours_applique_chaque_etape_a_chaque_element()
    {
        var result = CompileAndRun(EachTrain);

        Assert.Equal((0, "[-2 est négatif][-4 est négatif][2, 6] [3, 4] [5, 37] <2><6>"), (result.Code, result.Output));
    }

    [ZigTheory]
    [InlineData("train [1, -2] |!|> positif |> fin;", 1, "")]                                                 // |!|> sans rail : le train échoue
    [InlineData("train [1, -2] |!|> positif !> [rapport] |> fin;", 4, "rapport : -2 est négatif")]            // le rail de |!|> termine le train
    [InlineData("train [1, -2, 3] ||> positif !> [$ 5] |> fin;", 5, "")]                                     // un rail par élément peut terminer le train
    public void Un_echec_dans_un_parcours_suit_son_rail(string train, int code, string output)
    {
        var content = EachTrain[..EachTrain.LastIndexOf("train ", StringComparison.Ordinal)] + train;
        var result = CompileAndRun(content);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    /// <summary>Un |!|> dans un guard, un ||> qui écarte dans un guard, un parcours dans une constante du fichier et dans une lambda.</summary>
    private const string GuardEachTrain = """
        const [int] DOUBLES: [1, 2, 3] ||> doubler;

        gear pure doubler(int n): int => n * 2;

        guard positif(int n): int {
            if (n < 0) {
                fail `{n} négatif`;
            }
            done;
        }

        guard tous([int] v): [int] {
            return v |!|> positif;
        }

        guard gardes([int] v): [int] {
            return v ||> positif;
        }

        gear somme([int] v): int {
            var f: (int k) => [k, k] ||> doubler;
            return f(v[0])[1];
        }

        emit afficher([int] v): uint {
            `{v} {DOUBLES} {somme(v)}` -> console;
            return 0;
        }

        emit rapport(Exception e): uint {
            `rapport : {e.message}` -> console;
            return 3;
        }

        train ENTREE |> tous !> [rapport] |> gardes |> afficher;
        """;

    [ZigTheory]
    [InlineData("[1, 2, 3]", 0, "[1, 2, 3] [2, 4, 6] 2")]
    [InlineData("[1, -2, 3]", 3, "rapport : -2 négatif")]          // |!|> fait échouer le guard tous
    public void Un_parcours_dans_un_guard_fait_echouer_le_guard(string values, int code, string output)
    {
        var result = CompileAndRun(GuardEachTrain.Replace("ENTREE", values));

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    /// <summary>Des tableaux de molds en colonnes : sous-molds aplatis, parcours, push, spread, index, champ [mold], mold récursif.</summary>
    private const string SoaTrain = """
        mold Vec2 { double x; double y; }
        mold Particule { string nom; Vec2 position; Vec2 vitesse; float masse; bool active; }
        mold Groupe { string nom; [Particule] membres; }
        mold Noeud { int valeur; [Noeud] enfants; }

        gear pure avancer(Particule p): Vec2 => {x: p.position.x + p.vitesse.x, y: p.position.y + p.vitesse.y};

        guard lourde(Particule p): Particule {
            if (p.masse < 1.0) {
                fail `{p.nom} trop légère`;
            }
            done;
        }

        emit afficher(): uint {
            [Particule] ps: [
                {nom: "a", position: {x: 0.0, y: 0.0}, vitesse: {x: 1.0, y: 2.0}, masse: 2.0, active: true},
                {nom: "b", position: {x: 5.0, y: 5.0}, vitesse: {x: -1.0, y: 0.5}, masse: 0.5, active: false}
            ];
            ps.push({nom: "c", position: {x: 1.0, y: 1.0}, vitesse: {x: 0.0, y: 0.0}, masse: 3.0, active: true});
            var bougees: ps ||> avancer;
            var lourdes: ps ||> lourde;
            var xs: ps ||> (p) => p.position.x;
            var tous: [...ps, ...lourdes];
            var g: new Groupe {nom: "g", membres: lourdes};
            var arbre: new Noeud {valeur: 1, enfants: [{valeur: 2, enfants: []}]};
            `{bougees[0]} {bougees[1]} {xs} {tous.length} {g.membres.length} {lourdes[1].nom} {arbre.enfants[0].valeur}` -> console;
            return 0;
        }

        !train |> afficher;
        """;

    [ZigFact]
    public void Un_tableau_de_molds_en_colonnes_se_lit_comme_un_tableau()
    {
        var result = CompileAndRun(SoaTrain);

        Assert.Equal((0, "{x: 1.0, y: 2.0} {x: 4.0, y: 5.5} [0.0, 5.0, 1.0] 5 2 c 2"), (result.Code, result.Output));
    }

    [Fact]
    public void Un_tableau_de_molds_est_range_en_colonnes_chaudes_puis_froides()
    {
        var cpp = Emit(SoaTrain);

        Assert.Contains("#include \"rgl/standard/soa.hpp\"", cpp);
        Assert.Contains("rgl::standard::soa_array<rgl_l_m_Particule>", cpp);
        // Sous-molds aplatis ; nom, lu seulement hors des parcours, vient en dernier.
        Assert.Contains("rgl::standard::array<double> rgl_k1;   // position.y", cpp);
        Assert.Contains("rgl::standard::array<rgl::standard::string> rgl_k5;   // nom", cpp);
        // Un mold qui se contient par un tableau reste un tableau de structs.
        Assert.Contains("rgl::standard::array<rgl_m_Noeud>", cpp);
        Assert.DoesNotContain("rgl_l_m_Noeud", cpp);
    }

    [Fact]
    public void Un_champ_jamais_lu_n_a_pas_de_colonne()
    {
        var cpp = Emit(SoaTrain);

        // active n'est lu nulle part, et aucune valeur entière d'une Particule n'est observée.
        Assert.DoesNotContain("// active", cpp);
    }

    [Fact]
    public void Un_champ_jamais_lu_garde_sa_colonne_si_le_mold_est_observe_en_entier()
    {
        var cpp = Emit("""
            mold P { int x; int jamais; }
            emit e(): uint {
                [P] ps: [{x: 1, jamais: 2}];
                `{ps[0]}` -> console;
                return 0;
            }
            !train |> e;
            """);

        Assert.Contains("// jamais", cpp);
    }

    [Fact]
    public void Un_champ_lu_derriere_une_branche_d_un_parcours_est_tiede_entre_le_chaud_et_le_froid()
    {
        var cpp = Emit("""
            mold P { int a; int b; int d; int c; }
            gear pure f(P p): int => p.a + (p.b > 0 ? p.c : 0);
            emit e(): uint {
                [P] ps: [{a: 1, b: 1, d: 3, c: 2}];
                var r: ps ||> f;
                `{r} {ps[0].d}` -> console;
                return 0;
            }
            !train |> e;
            """);

        Assert.Contains("rgl_k0;   // a", cpp);
        Assert.Contains("rgl_k1;   // b", cpp);
        Assert.Contains("rgl_k2;   // c", cpp);
        Assert.Contains("rgl_k3;   // d", cpp);
    }

    [ZigFact]
    public void Un_champ_supprime_se_reconstruit_a_vide_sans_changer_le_resultat()
    {
        var result = CompileAndRun("""
            mold P { int x; int jamais; string aussi; }
            gear pure double_x(P p): int => p.x * 2;
            emit e(): uint {
                [P] ps: [{x: 1, jamais: 2, aussi: "a"}];
                ps.push({x: 4, jamais: 5, aussi: "b"});
                var r: ps ||> double_x;
                `{r}` -> console;
                return 0;
            }
            !train |> e;
            """);

        Assert.Equal((0, "[2, 8]"), (result.Code, result.Output));
    }

    [Fact]
    public void Un_tableau_de_molds_dont_les_parcours_lisent_tous_les_champs_reste_un_tableau_de_structs()
    {
        var cpp = Emit("""
            mold P { int x; int vie; }
            gear pure vieillir(P p): P => {x: p.x + 1, vie: p.vie - 1};
            emit e(): uint {
                [P] ps: [{x: 0, vie: 3}];
                var suivantes: ps ||> vieillir;
                `{suivantes}` -> console;
                return 0;
            }
            !train |> e;
            """);

        Assert.Contains("rgl::standard::array<rgl_m_P>", cpp);   // toutes les colonnes chaudes : pas de colonnes
        Assert.DoesNotContain("soa_array", cpp);
    }

    /// <summary>Guards bouclés, points de reprise et reprises : chaque guard écrit ce qu'il reçoit.</summary>
    private const string LoopTrain = """
        guard moitie(int n): int {
            `{n} ` -> console;
            if (n <= 1) {
                done;
            }
            if (n eq 7) {
                fail "sept";
            }
            return n / 2;
        }

        guard sur(int n): int => n;

        guard essai(int n): int {
            `<{n}>` -> console;
            if (n < 3) {
                fail "petit";
            }
            done;
        }

        gear pure relance(Exception e): int => 5;
        gear pure garde(Exception e): int => 0;
        gear pure deux(Exception | LoopError e): int => 2;
        gear pure un(int n): int => 1;
        gear pure tous([int] v): [int] => v;
        gear pure repli(Exception e): [int] => [7];

        emit fin(int n): uint {
            `= {n}` -> console;
            return 0;
        }

        emit fins([int] v): uint {
            `= {v}` -> console;
            return 0;
        }

        emit rapport(Exception | LoopError e): uint {
            `[{e.message}]` -> console;
            return 3;
        }

        """;

    [ZigTheory]
    [InlineData("train 64 |> moitie* |> fin;", 0, "64 32 16 8 4 2 1 = 1")]                                // jusqu'au done
    [InlineData("train 64 |> moitie*<3> |> fin;", 0, "64 32 16 = 8")]                                      // la dernière valeur passe
    [InlineData("train 64 |> moitie*<!3> !> [rapport] |> fin;", 3, "64 32 16 [3 tours dépassés]")]               // LoopError sur le rail
    [InlineData("train 64 |> moitie*<!7> |> fin;", 0, "64 32 16 8 4 2 1 = 1")]                             // done au dernier tour permis
    [InlineData("train 64 |> moitie*<!6> |> fin;", 1, "64 32 16 8 4 2 ")]                                  // limite atteinte sans rail
    [InlineData("train 14 |> moitie* !> [rapport] |> fin;", 3, "14 7 [sept]")]                                    // un fail sort de la boucle
    [InlineData("train 1 |> ^1 essai !> [relance |> ^1] |> fin;", 0, "<1><5>= 5")]                          // reprise avec la valeur du rail
    [InlineData("train 1 |> ^1 essai !> [garde |> ^1<2> |> $ 4] |> fin;", 4, "<1><0><0>")]                  // limite atteinte : la suite
    [InlineData("train 1 |> ^1 essai !> [garde |> ^1<!2>] |> fin;", 1, "<1><0><0>")]                        // limite atteinte : échec
    [InlineData("train 1 |> ^2 sur |> ^1 essai !> [garde |> ^1<1> |> un |> ^2<1> |> $ 4] |> fin;", 4, "<1><0><1><0>")] // compteur remis à zéro
    [InlineData("train [5, 1] |> ^1 tous ||> essai !> [repli |> ^1<1> |> done] |> fins;", 0, "<5><1><7>= [7]")]      // depuis un rail par élément
    [InlineData("train 64 |> ^1 moitie*<!3> !> [deux |> ^1<1> |> $ 4] |> fin;", 0, "64 32 16 2 1 = 1")]     // point et boucle sur une étape
    public void Les_boucles_de_guard_et_les_reprises_s_executent(string train, int code, string output)
    {
        var result = CompileAndRun(LoopTrain + train);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    [Fact]
    public void Une_boucle_appelle_repeat_et_un_point_de_reprise_est_une_etiquette()
    {
        var cpp = Emit(LoopTrain + "train 1 |> ^1 essai !> [garde |> ^1<2> |> $ 4] |> moitie*<3> |> fin;");

        Assert.Contains("#include \"rgl/standard/loop.hpp\"", cpp);
        Assert.Contains("[[maybe_unused]] std::int32_t rgl_cp1 = rgl_start;\n    std::uint32_t rgl_cp1_k0 = 0u;\n    [[maybe_unused]] rgl_cp1_at:;", cpp);
        Assert.Contains("if (rgl_cp1_k0 < 2u) {", cpp);
        Assert.Contains("rgl_cp1 = rgl_t", cpp);
        Assert.Contains("goto rgl_cp1_at;", cpp);
        Assert.Contains("rgl::standard::repeat<std::int32_t>([&](const std::int32_t& rgl_v) { return rgl_g_moitie(rgl_v); }, ", cpp);
        Assert.Contains("return rgl::standard::done<std::int32_t>(rgl_p_n);", cpp);
    }

    /// <summary>Intervalles et accumulations : en valeur, comme source d'un parcours, avec des guards.</summary>
    private const string RangeTrain = """
        gear pure carre(int n): int => n * n;
        gear pure ajouter(long acc, int n): long => acc + n;

        guard jusquAuNegatif(int acc, int n): int {
            if (n eq 13) {
                fail "treize";
            }
            if (n < 0) {
                done;
            }
            return acc + n;
        }

        guard somme([int] v): int => v |+> 0 with jusquAuNegatif;

        emit valeurs(int n): uint {
            var u: n as uint;
            (int, int) => int plus: (a, x) => a + x;
            `{[0..5]} {[10..<0 by -3]} {[3..<3]} {[0..n]} {[n..0 by -2]} {[n..0]} {[0..u]} ` -> console;
            `{[1..10] |+> 0 with (acc, x) => acc + x} {[1..4] ||> carre |+> 0 with ajouter} {[1..3] |+> 10 with plus}` -> console;
            return 0;
        }

        emit fin(int total): uint {
            `= {total}` -> console;
            return 0;
        }

        emit rapport(Exception e): uint {
            `[{e.message}]` -> console;
            return 3;
        }

        """;

    [ZigTheory]
    [InlineData("!train 4 |> valeurs;", 0, "[0, 1, 2, 3, 4, 5] [10, 7, 4, 1] [] [0, 1, 2, 3, 4] [4, 2, 0] [] [0, 1, 2, 3, 4] 55 30 16")]
    [InlineData("train [1, 2, 3, -1, 50] |+> 0 with jusquAuNegatif !> [rapport] |> fin;", 0, "= 6")]  // done arrête l'accumulation
    [InlineData("train [1, 13, 3] |+> 0 with jusquAuNegatif !> [rapport] |> fin;", 3, "[treize]")]  // fail : le rail
    [InlineData("train [1, 13, 3] |+> 0 with jusquAuNegatif |> fin;", 1, "")]                         // sans rail, le train échoue
    [InlineData("train [5, 13] |> somme !> [rapport] |> fin;", 3, "[treize]")]                        // propagé par le guard englobant
    [InlineData("train [0..<4] ||> carre |+> 0 with jusquAuNegatif |> fin;", 0, "= 14")]               // intervalle, parcours, accumulation
    public void Les_intervalles_et_les_accumulations_s_executent(string train, int code, string output)
    {
        var result = CompileAndRun(RangeTrain + train);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    [ZigFact]
    public void Un_pas_nul_connu_a_l_execution_arrete_le_programme()
    {
        var result = CompileAndRun("emit e(int pas): uint {\n    `{[0..3 by pas]}` -> console;\n    return 0;\n}\n!train 0 |> e;");

        Assert.NotEqual(0, result.Code);
        Assert.Contains("rgl : pas d'intervalle nul (main.rgl:2:7)", result.Output);
    }

    /// <summary>
    /// La mutabilité : contenu modifié en place (tableaux de toutes sortes, dictionnaire, tuple, colonnes
    /// d'une SoA), paramètres mut, parcours qui modifie, copies indépendantes, et un guard dont l'index
    /// hors limites part sur le rail.
    /// </summary>
    private const string MutTrain = """
        mold Vec2 { float x; float y; }
        mold Particule { Vec2 pos; Vec2 vit; int vie; [int] trace; string nom: "p"; }

        gear avancer(mut Particule p): void {
            p.pos.x +<- p.vit.x;
            p.pos.y <- p.pos.y + p.vit.y;
            p.vie -<- 1;
            p.trace.push(p.vie);
        }

        gear naitre(mut [Particule] ps, float x): void => ps.push({pos: {x, y: 0.0}, vit: {x: 1.0, y: 2.0}, vie: 3, trace: []});

        gear echanger(mut Vec2 a, mut Vec2 b): void {
            var t: a;
            a <- b;
            b <- t;
        }

        guard viser([int] entree, int i): int {
            mut var t: entree;
            t[i] <- 7;
            return t[i];
        }

        emit demo(): uint {
            mut [Particule] ps: [];
            naitre(ps, 0.0);
            naitre(ps, 10.0);
            ps ||> avancer;
            ps ||> (mut p) => { p.vie *<- 10; };
            ps[1].pos <- {x: -1.0, y: -1.0};
            ps[0].trace[0] <- 99;
            ps[1].vit.y <- 5.0;
            mut var copie: ps;
            copie[0].vie <- 0;
            `{ps}\n{copie[0].vie} {ps[0].vie}\n` -> console;

            mut [int] nombres: [1, 2, 3];
            mut [int;2] paire: [4, 5];
            mut [int;0..3] bornes: [6];
            mut [int+] plein: [8];
            nombres[0] <- 10;
            paire[1] +<- 1;
            bornes[0] <- bornes[0] * 2;
            plein[0] <- 9;
            mut [[int]] grille: [[1], [2, 3]];
            grille ||> (mut ligne) => ligne.push(0);
            grille[1][0] <- 20;
            mut [string:bool] drapeaux: ["a": false, "b": false];
            drapeaux["b"] <- true;
            mut var v: {x: 1.0, y: 2.0} as Vec2;
            mut var w: {x: 3.0, y: 4.0} as Vec2;
            echanger(v, w);
            `{nombres} {paire} {bornes} {plein} {grille} {drapeaux} {v} {w}\n` -> console;
            return 0;
        }

        emit fin(int n): uint {
            `= {n}` -> console;
            return 0;
        }

        emit rapport(Exception e): uint {
            `[{e.message}]` -> console;
            return 3;
        }

        """;

    [ZigFact]
    public void Le_contenu_d_une_variable_mut_se_modifie_en_place()
    {
        var result = CompileAndRun(MutTrain + "!train |> demo;");

        Assert.Equal(0, result.Code);
        Assert.Equal("[{pos: {x: 1.0, y: 2.0}, vit: {x: 1.0, y: 2.0}, vie: 20, trace: [99], nom: \"p\"}, {pos: {x: -1.0, y: -1.0}, vit: {x: 1.0, y: 5.0}, vie: 20, trace: [2], nom: \"p\"}]\n"
            + "0 20\n"
            + "[10, 2, 3] [4, 6] [12] [9] [[1, 0], [20, 3, 0]] [\"a\": false, \"b\": true] {x: 3.0, y: 4.0} {x: 1.0, y: 2.0}\n", result.Output);
    }

    [ZigTheory]
    [InlineData("train [1, 2] |> viser(1) !> [rapport] |> fin;", 0, "= 7")]
    [InlineData("train [1, 2] |> viser(5) !> [rapport] |> fin;", 3, "[index hors limites]")]   // dans un guard : il échoue
    public void Un_index_modifie_dans_un_guard_le_fait_echouer_hors_limites(string train, int code, string output)
    {
        var result = CompileAndRun(MutTrain + train);

        Assert.Equal((code, output), (result.Code, result.Output));
    }

    /// <summary>Un guard qui modifie la valeur du train : une fois, puis en boucle (la valeur lui est déplacée à chaque tour).</summary>
    private const string MutStepTrain = """
        guard grandir(mut [int] t): [int] {
            if (t.length >= 4) {
                done;
            }
            t.push(t.length as int);
            return t;
        }

        emit taille([int] t): uint {
            `{t}` -> console;
            return 0;
        }

        """;

    [ZigTheory]
    [InlineData("train [9] |> grandir |> taille;", "[9, 1]")]
    [InlineData("train [9] |> grandir*<10> |> taille;", "[9, 1, 2, 3]")]
    [InlineData("train [9] |> grandir |> grandir |> taille;", "[9, 1, 2]")]
    public void Une_etape_de_train_modifie_la_valeur_du_train_par_un_parametre_mut(string train, string output)
    {
        var result = CompileAndRun(MutStepTrain + train);

        Assert.Equal((0, output), (result.Code, result.Output));
    }

    [ZigFact]
    public void Un_deplacement_ne_copie_pas_et_garde_la_valeur()
    {
        const string program = """
            mold Sac { [int] items; string nom; }
            gear remplir(int n): Sac {
                mut Sac s: {items: [], nom: "sac"};
                s.items.push(n);
                return s;
            }
            emit e(): uint {
                Sac a:: remplir(3);
                var b:: a;
                mut [int] liste: [1, 2];
                [int] autre: [9];
                liste <-| autre;
                liste[0] <-| 4;
                `{b} {liste}` -> console;
                return 0;
            }
            !train |> e;
            """;

        var cpp = Emit(program);
        Assert.Contains("rgl_m_Sac rgl_v_b = std::move(rgl_v_a);", cpp);
        Assert.Contains("rgl_v_liste = std::move(rgl_v_autre);", cpp);
        Assert.Equal((0, "{items: [3], nom: \"sac\"} [4]"), CompileAndRun(program));
    }

    [ZigFact]
    public void Un_index_modifie_hors_limites_arrete_le_programme()


    {
        var result = CompileAndRun("emit e(): uint {\n    mut [int] t: [1];\n    t[3] <- 2;\n    return 0;\n}\n!train |> e;");

        Assert.NotEqual(0, result.Code);
        Assert.Contains("rgl : index 3 hors limites pour un tableau de 1 élément(s) (main.rgl:3:5)", result.Output);
    }

    [Fact]
    public void Une_modification_en_place_ecrit_dans_la_colonne_ou_remet_l_element_a_sa_place()
    {
        var cpp = Emit(MutTrain + "!train |> demo;");

        Assert.Contains("[[maybe_unused]] static void rgl_g_avancer([[maybe_unused]] rgl_m_Particule& rgl_p_p)", cpp);   // paramètre mut : une référence
        Assert.Contains("rgl_p_p.rgl_f_pos.rgl_f_x = (rgl_p_p.rgl_f_pos.rgl_f_x + rgl_p_p.rgl_f_vit.rgl_f_x);", cpp);
        Assert.Contains("rgl_p_p.rgl_f_trace.push(", cpp);
        Assert.Contains("static constexpr void store(", cpp);                                                                     // le Layout sait remettre un élément
        Assert.Contains(".columns().rgl_k", cpp);                                                                       // ps[1].vit.y : sa colonne
        Assert.Contains(".set(rgl_t", cpp);                                                                             // ps[1].pos : un sous-mold aplati
        Assert.Contains("auto& rgl_t", cpp);                                                                            // le parcours qui modifie lit par référence
        Assert.Contains("rgl_g_echanger(rgl_v_v, rgl_v_w);", cpp);
    }

    [Fact]
    public void Une_variable_lue_une_derniere_fois_est_deplacee()
    {
        var cpp = Emit("""
            gear g(): void {
                var a: [1, 2, 3];
                var b: a;
                [[int]] tout: [];
                tout.push(b);
                var c: [4];
                var d: c;
                `{c}` -> console;
                var s: "texte";
                switch (1) {
                    case 1:
                        var e: s;
                        goto 2;
                    case 2:
                        break;
                }
            }
            train |> done;
            """);

        Assert.Contains("rgl_v_b = std::move(rgl_v_a);", cpp);
        Assert.Contains("rgl_v_tout.push(std::move(rgl_v_b));", cpp);
        Assert.Contains("rgl_v_d = rgl_v_c;", cpp);                        // c est relue ensuite
        Assert.Contains("rgl_v_e = rgl_v_s;", cpp);                        // un goto peut revenir : pas de déplacement
    }



    [Fact]
    public void Un_intervalle_parcouru_ou_accumule_ne_construit_aucun_tableau()

    {
        var cpp = Emit(RangeTrain + "train [0..<4] ||> carre |+> 0 with jusquAuNegatif |> fin;");

        Assert.Contains("#include \"rgl/standard/range.hpp\"", cpp);
        Assert.Contains("const auto& rgl_t", cpp);
        Assert.Contains(" = rgl::standard::range<std::int32_t>(0, 4, 1ll, true, \"main.rgl:", cpp);
        Assert.Contains("if (rgl_t", cpp);
        Assert.Contains(".finished()) break;", cpp);
        Assert.Contains("rgl::standard::fixed_array<std::int32_t, 6>::from(rgl::standard::range<std::int32_t>(0, 5, 1ll, false, ", cpp);   // [0..5] en valeur
    }
}
