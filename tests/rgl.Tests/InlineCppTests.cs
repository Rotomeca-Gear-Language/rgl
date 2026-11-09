using Rgl.Binding;
using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.IR;
using Rgl.Lsp;
using Rgl.Parsing;
using Rgl.Project;
using Rgl.Syntax;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

public class InlineCppTests
{
    private static (IrProgram? Program, DiagnosticBag Diagnostics) Bind(string text, bool safe = false)
    {
        var bag = new DiagnosticBag();
        var unit = new Parser(new Lexer(new SourceText("main.rgl", text), bag).Tokenize(), bag).Parse();
        return (bag.HasErrors ? null : new Binder(bag, safe: safe).Bind(unit), bag);
    }

    [Theory]
    [InlineData("E147", "gear f(): int => cpp { int rgl() { return 2; } }(): int; !train |> f |> $ 0;")]
    [InlineData("E149", "unsafe f(): int => cpp { int rgl() { return 2; }")]
    [InlineData("E149", "unsafe f(): int => cpp { int rgl(int a, int b) { return 2; } }(int a: 1, int a: 2): int; !train |> $ 0;")]
    [InlineData("E151", "@trusted @trust gear f(): int => 2; !train |> f |> $ 0;")]
    [InlineData("E066", "@trusted gear pure f(): int { 2 -> console; return 2; } !train |> $ 0;")]
    [InlineData("E057", "gear g(): int => 2; @trusted gear pure f(): int => g(); !train |> $ 0;")]
    public void Les_contrats_invalides_sont_diagnostiques(string expectedCode, string text)
    {
        var (program, bag) = Bind(text);
        Assert.Null(program);
        Assert.Contains(bag.Items, d => d.Code == expectedCode);
    }

    [Fact]
    public void La_frontiere_trusted_arrete_unsafe_sans_perdre_la_purete()
    {
        var (program, bag) = Bind("@trusted gear pure f(): int => cpp { int rgl() { return 2; } }(): int; !train |> f |> $ 0;", safe: true);
        Assert.NotNull(program);
        Assert.False(bag.HasErrors, string.Join('\n', bag.Items));
        var gear = program.Gears.Single(g => g.Symbol.Name == "f");
        Assert.True(gear.Symbol.IsTrusted);
        Assert.True(gear.Symbol.IsPure);
        Assert.False(gear.Symbol.IsUnsafe);
        Assert.Single(CppEmitter.Emit(program, "main.rgl").NativeUnits!);
    }

    [Fact]
    public void Une_frontiere_trusted_ne_masque_pas_un_fire()
    {
        var (_, bag) = Bind("@fire gear detached(): void {} @trusted gear f(): int { detached(); return 2; } !train |> f |> $ 0;", safe: true);
        Assert.Contains(bag.Items, d => d.Code == DiagnosticCodes.FireInSafeTrain);
    }

    [Theory]
    [InlineData("\"no-trust\":true", "@trusted gear f(): int => 2; !train |> f |> $ 0;", "E150")]
    [InlineData("\"no-trust\":true", "@trust gear f(): int => 2; gear g(): int { var cb: f; return cb(); } !train |> g |> $ 0;", "E150")]
    [InlineData("\"no-cpp\":true", "unsafe f(): int => cpp { int rgl() { return 2; } }(): int; !train |> f |> $ 0;", "E150")]
    [InlineData("\"no-cpp\":true", "@trusted gear f(): int => cpp { int rgl() { return 2; } }(): int; !train |> f |> $ 0;", "E150")]
    public void Les_restrictions_du_manifeste_s_appliquent_dans_l_editeur(string config, string text, string expectedCode)
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\"," + config + "}");
        var path = dir.File("main.rgl", text);
        Assert.Contains(DocumentAnalysis.Analyze(path, text).Diagnostics, d => d.Code == expectedCode);
    }

    [Fact]
    public void La_liste_autorise_un_fichier_precis_et_pas_ses_imports()
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\",\"safe\":true,\"no-trust\":true,\"no-cpp\":true,\"cpp-trust\":[\"Allowed.forge\"]}");
        dir.File("Allowed.forge", "forge: gears\ndescription: \"test\"\n@trusted gear pure f(): int => cpp { int rgl() { return 2; } }(): int;");
        const string text = "import Allowed; !train |> Allowed.f |> $ 0;";
        var path = dir.File("main.rgl", text);
        Assert.DoesNotContain(DocumentAnalysis.Analyze(path, text).Diagnostics, d => d.Level == DiagnosticLevel.Error);
        dir.File("Other.forge", "forge: gears\ndescription: \"test\"\n@trusted gear f(): int => 2;");
        dir.File("Allowed.forge", "forge: gears\ndescription: \"test\"\nimport Other;\n@trusted gear f(): int => Other.f();");
        Assert.Contains(DocumentAnalysis.Analyze(path, text).Diagnostics, d => d.Code == DiagnosticCodes.NativePolicy);
    }

    [Theory]
    [InlineData("\"no-cpp\":\"yes\"")]
    [InlineData("\"no-trust\":1")]
    [InlineData("\"cpp-trust\":\"Allowed.forge\"")]
    [InlineData("\"cpp-trust\":[\"../Allowed.forge\"]")]
    [InlineData("\"cpp-trust\":[\"/tmp/Allowed.forge\"]")]
    [InlineData("\"cpp-trust\":[\"native.cpp\"]")]
    public void Un_manifeste_mal_type_ou_hors_racine_est_refuse(string config)
    {
        using var dir = new TempDirectory();
        var path = dir.File("rgl.json", "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\"," + config + "}");
        var bag = new DiagnosticBag();
        Assert.Null(ProjectManifest.Load(path, bag));
        Assert.True(bag.HasErrors);
    }

    private static (int Code, string Output, string Error) Exec(string text, string config = "")
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", "{\"name\":\"cpp-test\",\"version\":\"1\",\"rgl\":\"0\"" + config + "}");
        dir.File("main.rgl", text);
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliRunner.Run(["compile", "main.rgl"], new CliContext(output, error, dir.Path, cache, Toolchain: ZigToolchain.Locate(cache)));
        if (code != 0) return (code, "", error.ToString());
        var run = ZigBuild.Run(Path.Combine(dir.Path, "out", "debug", OperatingSystem.IsWindows() ? "cpp-test.exe" : "cpp-test"));
        return (run.Code, run.Output.Replace("\r\n", "\n"), error.ToString());
    }

    [ZigFact]
    public void Sqrt_cpp_pure_et_safe_garde_le_nullable()
    {
        var result = Exec("""
            @trusted
            gear pure racine(double x): double? {
                if (x < 0.0) { return null; }
                return cpp {
                    #include <cmath>
                    double rgl(double x) { return std::sqrt(x); }
                }(double x: x): double;
            }
            gear run(): int { racine(81.0) ?? 0.0 -> console; racine(-1.0) ?? -1.0 -> console; return 0; }
            !train |> run |> $ 0;
            """, ",\"safe\":true");
        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("9.0-1.0", result.Output);
    }

    [ZigFact]
    public void Deux_blocs_isolent_les_helpers_et_preservent_les_chaines_cpp()
    {
        var result = Exec(""""
            unsafe a(): int => cpp {
                #include <cstring>
                const char* helper() { return R"tag( } /* #include <absent> */ )tag"; }
                int rgl() { return std::strlen(helper()) > 0 ? 2 : 0; }
            }(): int;
            unsafe b(): int => cpp {
                #if 1
                #include <cmath>
                #endif
                int helper() { return 3; }
                int rgl() { return helper(); }
            }(): int;
            gear run(): int { a() + b() -> console; return 0; }
            !train |> run |> $ 0;
            """");
        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("5", result.Output);
    }

    [ZigFact]
    public void Les_arguments_s_evaluent_une_fois_dans_l_ordre_et_le_retour_nullable_compile()
    {
        var result = Exec("""
            unsafe f(): int {
                int n: 0;
                int somme: cpp {
                    #include <rgl::standard>
                    rgl_return rgl(rgl_arg_a a, rgl_arg_b b) { return a * 10 + b; }
                }(int a: increment n, int b: increment n): int;
                int? x: cpp {
                    #include <rgl::standard>
                    rgl::standard::nullable<std::int32_t> rgl() { return 7; }
                }(): int?;
                return somme + (x ?? 0);
            }
            gear run(): int { f() -> console; return 0; }
            !train |> run |> $ 0;
            """);
        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("19", result.Output);
    }

    [ZigFact]
    public void Une_signature_cpp_incompatible_est_une_erreur_utilisateur()
    {
        var result = Exec("unsafe f(): int => cpp { double rgl() { return 2; } }(): int; !train |> f |> $ 0;");
        Assert.NotEqual(0, result.Code);
        Assert.Contains("E146", result.Error);
        Assert.DoesNotContain("bug du transpileur", result.Error);
    }
    [ZigFact]
    public void La_std_garde_sqrt_safe_et_pure_avec_les_deux_interdictions()
    {
        var result = Exec("gear pure f(): double? => std.sqrt(144.0); gear run(): int { f() ?? 0.0 -> console; return 0; } !train |> run |> $ 0;",
            ",\"safe\":true,\"no-cpp\":true,\"no-trust\":true");
        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("12.0", result.Output);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("descriptor")]
    public void No_cpp_refuse_les_links_avant_de_lancer_clang(string kind)
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\",\"no-cpp\":true}");
        dir.File("api.h", "int twice(int n);");
        dir.File("Api.forge", "forge: " + kind + "\ndescription: \"API\"\nheader: \"api.h\"\nlink: static\n");
        var text = "import Api; !train |> done;";
        var path = dir.File("main.rgl", text);
        var diagnostics = DocumentAnalysis.Analyze(path, text).Diagnostics;
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.NativePolicy);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.UnreadableHeader);
    }

    [Fact]
    public void Un_cpp_deja_lie_par_forge_ne_se_reinclut_pas()
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\"}");
        dir.File("Native.forge", "forge: gears\ndescription: \"test\"\nunsafe raw(): int;");
        dir.File("raw.rgl.cpp", "namespace rgl_native { int raw() { return 2; } }");
        const string text = "import Native; unsafe f(): int => cpp {\n#include \"raw.rgl.cpp\"\nint rgl() { return 2; }\n}(): int; !train |> f |> $ 0;";
        var path = dir.File("main.rgl", text);
        Assert.Contains(DocumentAnalysis.Analyze(path, text).Diagnostics, d => d.Code == DiagnosticCodes.InvalidCppBlock);
    }

    [Fact]
    public void La_purete_d_un_unsafe_reste_verifiee()
    {
        var (program, bag) = Bind("unsafe pure raw(int x): int => x + 1; @trusted gear pure f(): int => raw(3); !train |> f |> $ 0;", safe: true);
        Assert.NotNull(program);
        Assert.False(bag.HasErrors);
        var (_, errors) = Bind("unsafe pure raw(): int { 3 -> console; return 2; } !train |> $ 0;");
        Assert.Contains(errors.Items, d => d.Code == DiagnosticCodes.InputOutputInPure);
    }

    [Fact]
    public void L_editeur_distingue_le_contrat_native_et_deduit_le_retour_cpp()
    {
        using var dir = new TempDirectory();
        const string text = "@trusted gear pure f(): int { var n: cpp { int rgl() { return 2; } }(): int; return n; } !train |> $ 0;";
        var path = dir.File("main.rgl", text);
        var document = SemanticDocument.Create(path, text, new ProjectIndex().Open(path, _ => null));
        var local = document.LocalsAt(text.IndexOf("return n", StringComparison.Ordinal)).Single(l => l.Name == "n");
        Assert.Equal("int", document.TypeOfSymbol(local));
        Assert.Contains("attestées", HoverProvider.Describe(document, document.Self.Symbols["f"]));
        var offset = text.IndexOf("return 2", StringComparison.Ordinal);
        Assert.Empty(CompletionProvider.Complete(path, text, 0, (uint)offset, new ProjectIndex().Open(path, _ => null)));
    }
    [ZigFact]
    public void Void_noexcept_et_generiques_passent_la_frontiere()
    {
        var result = Exec("""
            @trusted gear pure identite<T>(T x): T => cpp {
                rgl_return rgl(rgl_arg_x x) noexcept { return x; }
            }(T x: x): T;
            unsafe vide(): void { cpp { void rgl() {} }(): void; }
            gear run(): int { vide(); identite(42) -> console; return 0; }
            !train |> run |> $ 0;
            """);
        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("42", result.Output);
    }

    [ZigFact]
    public void Une_fonction_RGL_peut_servir_de_callback_cpp()
    {
        var result = Exec("""
            @trusted gear appliquer(pure (int) => int callback, int value): int => cpp {
                int rgl(const rgl_arg_callback& callback, int value) { return callback(value); }
            }(pure (int) => int callback: callback, int value: value): int;
            gear run(): int { appliquer((int n) => n * 2, 21) -> console; return 0; }
            !train |> run |> $ 0;
            """);
        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("42", result.Output);
    }

    [Fact]
    public void Une_allowlist_ne_suit_pas_un_lien_hors_du_projet()
    {
        if (OperatingSystem.IsWindows()) return; // création de liens réservée selon les droits Windows
        using var dir = new TempDirectory();
        using var outside = new TempDirectory();
        var file = outside.File("External.forge", "forge: gears\ndescription: \"extérieur\"\n@trusted gear f(): int => 2;");
        File.CreateSymbolicLink(Path.Combine(dir.Path, "Allowed.forge"), file);
        var manifest = new ProjectManifest("p", "1", null, "0", NoTrust: true, NoCpp: true, CppTrust: ["Allowed.forge"]);
        var policy = new NativePolicy(manifest, dir.Path);
        Assert.False(policy.AllowsTrust(Path.Combine(dir.Path, "Allowed.forge")));
        Assert.False(policy.AllowsCpp(Path.Combine(dir.Path, "Allowed.forge")));
    }
}
