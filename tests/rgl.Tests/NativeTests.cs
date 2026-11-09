using System.Text;
using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Native;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>Les bibliothèques C : un fichier <c>forge: native</c>, la lecture de l'en-tête par Clang et l'appel de ses fonctions.</summary>
public class NativeTests
{
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    private const string Header = """
        #ifndef MATHX_H
        #define MATHX_H
        #include <stddef.h>
        #ifdef __cplusplus
        extern "C" {
        #endif
        unsigned int add_u(unsigned int a, unsigned int b);
        double half(double x);
        int negate(int);
        size_t widen(unsigned int n);
        void ping(void);
        const char* nom(void);
        unsigned int longueur(const char* texte);
        int formate(const char* fmt, ...);
        #define VERSION_MAJOR 3
        #define RATIO 2.5
        #define TITRE "mathx"
        #define GRAND 5000000000
        #define CARRE(x) ((x)*(x))
        #define DOUBLE_VERSION (VERSION_MAJOR * 2)
        typedef struct Vec2 { float x; float y; } Vec2;
        typedef struct { Vec2 min; int n; } Boite;
        enum Mode { MODE_A, MODE_B = 4, MODE_C };
        Vec2 mise_a_l_echelle(Vec2 v, float k);
        Boite boite(Vec2 v);
        enum Mode suivant(enum Mode m);
        #ifdef __cplusplus
        }
        #endif
        #endif
        """;

    private const string Implementation = """
        #include "mathx.h"
        unsigned int add_u(unsigned int a, unsigned int b) { return a + b; }
        double half(double x) { return x / 2.0; }
        int negate(int v) { return -v; }
        size_t widen(unsigned int n) { return n; }
        void ping(void) {}
        const char* nom(void) { return "mathx"; }
        unsigned int longueur(const char* texte) { unsigned int n = 0; while (texte[n]) ++n; return n; }
        Vec2 mise_a_l_echelle(Vec2 v, float k) { Vec2 r = { v.x * k, v.y * k }; return r; }
        Boite boite(Vec2 v) { Boite b = { v, 9 }; return b; }
        enum Mode suivant(enum Mode m) { return m == MODE_A ? MODE_B : MODE_A; }
        int formate(const char* fmt, ...) { (void)fmt; return 0; }
        """;

    private static string Native(string link = "static", string library = "") => $$"""
        forge: native
        description: "Mathx"

        header: "lib/mathx.h"
        link: {{link}}
        {{library}}
        """;

    /// <summary>Un projet avec la bibliothèque C compilée en archive statique à côté de son en-tête.</summary>
    private static TempDirectory Project(ICompilerToolchain? toolchain, string main, string? forge = null)
    {
        var project = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "lib"));
        File.WriteAllText(Path.Combine(project.Path, "lib", "mathx.h"), Header);
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), main);

        if (toolchain is not null)
        {
            File.WriteAllText(Path.Combine(project.Path, "lib", "mathx.c"), Implementation);
            var obj = Path.Combine(project.Path, "lib", "mathx.o");
            var archive = Path.Combine(project.Path, "lib", "libmathx.a");
            var compile = ZigBuild.Run(ZigBuild.Zig, "cc", "-c", "-target", toolchain.Target, Path.Combine(project.Path, "lib", "mathx.c"), "-o", obj);
            Assert.True(compile.Code == 0, compile.Output);
            var archived = ZigBuild.Run(ZigBuild.Zig, "ar", "rcs", archive, obj);
            Assert.True(archived.Code == 0, archived.Output);
        }

        var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
        File.WriteAllText(Path.Combine(project.Path, "Mathx.forge"), forge ?? Native(library: $"library: {{{platform}: \"lib/libmathx.a\"}}"));
        return project;
    }

    private static (int ExitCode, string Out, string Error) Run(ICompilerToolchain? toolchain, string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, directory, SharedCache, Toolchain: toolchain));
        return (exitCode, output.ToString(), error.ToString());
    }

    [ZigFact]
    public void Une_fonction_C_s_appelle_comme_un_gear_et_se_lie_a_la_bibliotheque_statique()
    {
        var toolchain = ZigToolchain.Locate(SharedCache);
        using var project = Project(toolchain, "import Mathx;\n!train |> $ Mathx.add_u(40, 2);\n");

        var exec = Run(toolchain, project.Path, "exec", "main.rgl");

        Assert.Equal(42, exec.ExitCode);
        Assert.DoesNotContain("erreur", exec.Error);
    }

    [ZigFact]
    public void Les_struct_les_enums_et_les_textes_C_passent_la_frontiere_dans_les_deux_sens()
    {
        var toolchain = ZigToolchain.Locate(SharedCache);
        using var project = Project(toolchain, """
            import Mathx;
            gear calcul(): uint {
                uint total: 0;
                Mathx.Vec2 v: Mathx.mise_a_l_echelle({x: 1.5, y: 2.0}, 2.0);
                total <- total + ((v.x + v.y) as uint);
                if (Mathx.suivant(Mathx.Mode.MODE_A) eq Mathx.Mode.MODE_B) { total <- total + 10; }
                total <- total + Mathx.longueur("héllo");
                total <- total + (Mathx.nom().length as uint);
                Mathx.Boite b: Mathx.boite(v);
                total <- total + (b.n as uint) + (b.min.x as uint);
                total <- total + Mathx.VERSION_MAJOR;
                total <- total + ((Mathx.RATIO * 2.0) as uint);
                total <- total + (Mathx.TITRE.length as uint);
                return total;
            }
            !train |> $ calcul();
            """);

        // 7 (struct) + 10 (enum) + 6 (« héllo » : six octets) + 5 (« mathx » copié) + 9 + 3 (struct imbriqué) + 3 + 5 + 5 (macros).
        var exec = Run(toolchain, project.Path, "exec", "main.rgl");

        Assert.True(exec.ExitCode == 53, exec.Error);
    }

    [ZigFact]
    public void Les_types_sans_alias_et_les_flottants_passent_la_frontiere()
    {
        var toolchain = ZigToolchain.Locate(SharedCache);
        using var project = Project(toolchain, """
            import Mathx;
            gear total(): int {
                double moitie: Mathx.half(84.0);
                ulong large: Mathx.widen(1);
                Mathx.ping();
                return Mathx.negate(5);
            }
            !train |> $ 7;
            """);

        // Le programme compile : types entiers, flottants et un alias (size_t) convertis, une fonction sans résultat appelée.
        var compile = Run(toolchain, project.Path, "compile", "main.rgl");

        Assert.True(compile.ExitCode == CliRunner.Success, compile.Error);
    }

    [ZigFact]
    public void Une_fonction_non_convertie_est_inconnue_et_l_utilisateur_est_prevenu()
    {
        var toolchain = ZigToolchain.Locate(SharedCache);
        using var project = Project(toolchain, "import Mathx;\n!train |> $ Mathx.formate();\n");

        var compile = Run(toolchain, project.Path, "compile", "main.rgl");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("formate", compile.Error);
        Assert.Contains("pas encore prises en charge", compile.Out + compile.Error);
    }

    [ZigFact]
    public void Un_en_tete_que_Clang_refuse_est_une_erreur_du_fichier_pas_un_bug_du_transpileur()
    {
        var toolchain = ZigToolchain.Locate(SharedCache);
        using var project = Project(toolchain, "import Mathx;\n!train |> $ 0;\n");
        File.WriteAllText(Path.Combine(project.Path, "lib", "mathx.h"), "int cassé(int a;\n");

        var compile = Run(toolchain, project.Path, "compile", "main.rgl");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains(DiagnosticCodes.UnreadableHeader, compile.Error);
        Assert.DoesNotContain("bug du transpileur", compile.Error);
    }

    [Fact]
    public void La_liaison_dynamique_n_est_pas_disponible()
    {
        using var project = Project(null, "import Mathx;\n!train |> $ 0;\n", Native(link: "dynamic"));

        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(project.Path, "main.rgl"), File.ReadAllText(Path.Combine(project.Path, "main.rgl"))), project.Path, diagnostics);

        Assert.Null(modules);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.DynamicLinkUnavailable);
    }

    [Fact]
    public void Sans_chaine_de_compilation_un_en_tete_ne_se_lit_pas()
    {
        using var project = Project(null, "import Mathx;\n!train |> $ 0;\n", Native());

        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(project.Path, "main.rgl"), File.ReadAllText(Path.Combine(project.Path, "main.rgl"))), project.Path, diagnostics);

        Assert.Null(modules);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.UnreadableHeader);
    }

    [Fact]
    public void Un_en_tete_introuvable_est_signale()
    {
        using var project = Project(null, "import Mathx;\n!train |> $ 0;\n");
        File.Delete(Path.Combine(project.Path, "lib", "mathx.h"));

        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(project.Path, "main.rgl"), File.ReadAllText(Path.Combine(project.Path, "main.rgl"))), project.Path, diagnostics);

        Assert.Null(modules);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.ImportNotFound && d.Message.Contains("mathx.h"));
    }

    [Fact]
    public void Un_fichier_native_demande_un_header_et_un_link()
    {
        using var project = Project(null, "import Mathx;\n!train |> $ 0;\n", "forge: native\ndescription: \"vide\"\n");

        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(Path.Combine(project.Path, "main.rgl"), File.ReadAllText(Path.Combine(project.Path, "main.rgl"))), project.Path, diagnostics);

        Assert.Null(modules);
        Assert.Contains(diagnostics.Items, d => d.Message.Contains("header:"));
        Assert.Contains(diagnostics.Items, d => d.Message.Contains("link: static"));
    }

    private static CHeader? Read(string json, string header) => ClangAstReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)), header);

    [Fact]
    public void Le_lecteur_ne_garde_que_les_declarations_de_l_en_tete_demande()
    {
        // Clang n'écrit « file » que lorsqu'il change : « sub » est dans lib.h comme « add », sans le redire ; la fonction du système
        // porte « includedFrom », qui désigne lib.h sans que ce soit le fichier où elle est écrite.
        const string json = """
            {"id":"0x1","kind":"TranslationUnitDecl","inner":[
              {"id":"0x2","kind":"TypedefDecl","loc":{},"name":"__int128_t","isImplicit":true},
              {"id":"0x2b","kind":"TypedefDecl","loc":{"file":"/sys/stddef.h","line":1},"name":"size_t","type":{"qualType":"unsigned long long"}},
              {"id":"0x3","kind":"FunctionDecl","loc":{"offset":1,"file":"/sys/stdio.h","line":1,"col":1,"tokLen":3,"includedFrom":{"file":"/proj/lib.h"}},
               "name":"du_systeme","type":{"qualType":"int (int)"},"inner":[{"kind":"ParmVarDecl","name":"c","type":{"qualType":"int"}}]},
              {"id":"0x4","kind":"FunctionDecl","loc":{"offset":9,"file":"/proj/lib.h","line":2,"col":1,"tokLen":3},
               "name":"add","type":{"qualType":"int (int, int)"},"inner":[
                 {"kind":"ParmVarDecl","name":"a","type":{"qualType":"int"}},{"kind":"ParmVarDecl","name":"b","type":{"qualType":"int"}}]},
              {"id":"0x5","kind":"FunctionDecl","loc":{"offset":50,"line":3,"col":8,"tokLen":3},
               "name":"taille","type":{"qualType":"size_t (int)","desugaredQualType":"unsigned long long (int)"},"inner":[{"kind":"ParmVarDecl","type":{"qualType":"int"}}]},
              {"id":"0x6","kind":"RecordDecl","loc":{"offset":80,"line":4,"col":8,"tokLen":5},"name":"Point"}
            ]}
            """;

        var header = Read(json, "/proj/lib.h");

        Assert.NotNull(header);
        Assert.Equal(["add", "taille"], header.Functions.Select(f => f.Name));
        Assert.Equal("int", header.Functions[0].ReturnType);
        Assert.Equal([("a", "int"), ("b", "int")], header.Functions[0].Parameters.Select(p => (p.Name, p.Type)));
        Assert.Equal("unsigned long long", header.Functions[1].ReturnType);
        Assert.Empty(header.Skipped);   // « Point » n'est pas défini ici : une déclaration seule ne dit rien
        Assert.Equal("unsigned long long", header.Aliases!["size_t"]);
    }

    [Fact]
    public void Les_alias_du_systeme_sont_retenus_pour_la_conversion()
    {
        var header = new CHeader([new CFunction("taille", "size_t", [new CParameter("n", "uint32_t")], false)], [],
            new Dictionary<string, string> { ["size_t"] = "unsigned long long", ["uint32_t"] = "__uint32_t", ["__uint32_t"] = "unsigned int" });

        var result = CBindings.Generate(header, "mathx.forge", "x86_64-linux-gnu.2.28");

        Assert.Contains("gear taille(uint n): ulong {", result.Source);
    }

    [Fact]
    public void Le_lecteur_refuse_ce_qui_n_est_pas_un_arbre_de_Clang()
    {
        Assert.Null(Read("pas du json", "/proj/lib.h"));
        Assert.Null(Read("", "/proj/lib.h"));
    }

    [Fact]
    public void Le_lecteur_lit_un_flux_plus_gros_que_son_tampon()
    {
        // Un JSON de quelques Mo, comme celui d'un en-tête qui en inclut d'autres : la lecture au fil de l'eau n'en perd rien.
        var json = new StringBuilder("{\"kind\":\"TranslationUnitDecl\",\"inner\":[");
        for (var i = 0; i < 40_000; ++i)
            json.Append($"{{\"kind\":\"FunctionDecl\",\"loc\":{{\"file\":\"/sys/x{i % 7}.h\",\"line\":1}},\"name\":\"f{i}\",\"type\":{{\"qualType\":\"int (int)\"}}}},");
        json.Append("{\"kind\":\"FunctionDecl\",\"loc\":{\"file\":\"/proj/lib.h\"},\"name\":\"voulue\",\"type\":{\"qualType\":\"int (void)\"}}]}");

        var header = Read(json.ToString(), "/proj/lib.h");

        Assert.NotNull(header);
        Assert.Equal(["voulue"], header.Functions.Select(f => f.Name));
    }

    private static CBindings.Result Bindings(CFunction function, string target) =>
        CBindings.Generate(new CHeader([function], []), "mathx.forge", target);

    [Fact]
    public void Chaque_fonction_convertie_devient_un_gear_qui_enveloppe_l_appel_C()
    {
        var result = Bindings(new CFunction("add", "int", [new CParameter("a", "int"), new CParameter("", "double")], false), "x86_64-linux-gnu.2.28");

        Assert.Equal(1, result.Converted);
        Assert.Contains("gear add(int a, double a1): int {", result.Source);
        Assert.Contains("return native add(a, a1);", result.Source);
    }

    [Theory]
    [InlineData("long", "x86_64-windows-gnu", "int")]
    [InlineData("long", "x86_64-linux-gnu.2.28", "long")]
    [InlineData("unsigned long", "x86_64-windows-gnu", "uint")]
    [InlineData("unsigned long", "x86_64-linux-gnu.2.28", "ulong")]
    [InlineData("unsigned long long", "x86_64-windows-gnu", "ulong")]
    [InlineData("const int", "x86_64-windows-gnu", "int")]
    [InlineData("_Bool", "x86_64-windows-gnu", "bool")]
    public void La_table_de_conversion_depend_de_la_cible(string cType, string target, string expected) =>
        Assert.Contains($"): {expected} {{", Bindings(new CFunction("f", cType, [], false), target).Source);

    [Theory]
    [InlineData("char *", "pas encore pris en charge")]
    [InlineData("struct Point", "pas encore pris en charge")]
    [InlineData("long double", "pas encore pris en charge")]
    public void Un_type_que_les_liaisons_ne_connaissent_pas_laisse_la_fonction_de_cote(string cType, string reason)
    {
        var result = Bindings(new CFunction("f", cType, [], false), "x86_64-linux-gnu.2.28");

        Assert.Equal(0, result.Converted);
        Assert.Contains(reason, Assert.Single(result.Unconverted).Reason);
    }

    [Fact]
    public void Les_struct_et_les_enums_de_l_en_tete_deviennent_des_molds_et_des_enums()
    {
        var header = new CHeader(
            [new CFunction("echelle", "Vec2", [new CParameter("v", "Vec2"), new CParameter("m", "enum Mode")], false),
             new CFunction("nom", "const char *", [], false)],
            [],
            new Dictionary<string, string> { ["Vec2"] = "struct Vec2" },
            [new CRecord("Vec2", false, [new CField("x", "float", false), new CField("y", "float", false)]),
             new CRecord("Union", true, [new CField("a", "int", false)]),
             new CRecord("Bits", false, [new CField("f", "int", true)])],
            [new CEnum("Mode", [("MODE_A", 0), ("MODE_B", 4)])]);

        var result = CBindings.Generate(header, "mathx.forge", "x86_64-linux-gnu.2.28");

        Assert.Contains("enum Mode: int {\n    MODE_A: 0,\n    MODE_B: 4\n}", result.Source);
        Assert.Contains("mold Vec2 {\n    float x;\n    float y;\n}", result.Source);
        Assert.Contains("gear echelle(Vec2 v, Mode m): Vec2 {", result.Source);
        Assert.Contains("gear nom(): string {", result.Source);
        Assert.Equal(["Union", "Bits"], result.Unconverted.Select(u => u.Name));
        Assert.Equal((2, 2), (result.Types, result.Converted));
    }

    [Fact]
    public void Le_lecteur_lit_les_struct_et_les_enums_meme_anonymes()
    {
        const string json = """
            {"id":"0x1","kind":"TranslationUnitDecl","inner":[
              {"id":"0x10","kind":"RecordDecl","loc":{"file":"/proj/lib.h","line":1},"tagUsed":"struct","completeDefinition":true,"inner":[
                {"kind":"FieldDecl","name":"a","type":{"qualType":"int"}},
                {"kind":"FieldDecl","name":"v","type":{"desugaredQualType":"struct Vec2","qualType":"Vec2"}},
                {"kind":"FieldDecl","name":"f","isBitfield":true,"type":{"qualType":"int"}}]},
              {"id":"0x11","kind":"TypedefDecl","loc":{"line":2},"name":"Anon","type":{"qualType":"struct Anon"},
               "inner":[{"kind":"RecordType","decl":{"id":"0x10","kind":"RecordDecl","name":""}}]},
              {"id":"0x20","kind":"EnumDecl","loc":{"line":3},"name":"Mode","inner":[
                {"kind":"EnumConstantDecl","name":"A"},
                {"kind":"EnumConstantDecl","name":"B","inner":[{"kind":"ConstantExpr","value":"4"}]},
                {"kind":"EnumConstantDecl","name":"C"}]},
              {"id":"0x30","kind":"RecordDecl","loc":{"line":4},"name":"Opaque","tagUsed":"struct"}
            ]}
            """;

        var header = Read(json, "/proj/lib.h")!;

        var record = Assert.Single(header.Records!);
        Assert.Equal("Anon", record.Name);
        Assert.Equal([("a", "int", false), ("v", "struct Vec2", false), ("f", "int", true)], record.Fields.Select(x => (x.Name, x.Type, x.IsBitfield)));
        var enumeration = Assert.Single(header.Enums!);
        Assert.Equal("Mode", enumeration.Name);
        Assert.Equal([("A", 0L), ("B", 4L), ("C", 5L)], enumeration.Constants);
    }

    [Fact]
    public void Le_lecteur_de_macros_ne_garde_que_les_constantes_du_fichier_demande()
    {
        // Les repères « # n "fichier" » disent de quel fichier vient la suite ; seules les macros de lib.h comptent.
        const string text = """
            # 1 "/sys/stddef.h" 1 3
            #define NULL ((void *)0)
            #define SYSTEME 1
            # 3 "/proj/lib.h" 2
            #define A 3
            #define B (0x7fffffffL)
            #define C 3.14f
            #define D "a\n"
            #define E 5000000000
            #define F -7
            #define G (1)+(2)
            #define H(x) ((x))
            #define I A
            #define J 077
            #define K 4294967295U
            #define L 1e3
            #define M .5
            #define N
            #define R 2
            #undef R
            #define S 1
            #define S 2
            """;

        var macros = CMacroReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "/proj/lib.h");

        Assert.Equal(
            [("A", "int", "3"), ("B", "int", "2147483647"), ("C", "double", "3.14"), ("D", "string", "\"a\\n\""), ("E", "long", "5000000000"), ("F", "int", "-7"),
             ("J", "int", "63"), ("K", "uint", "4294967295"), ("L", "double", "1.0e3"), ("M", "double", "0.5"), ("S", "int", "2")],
            macros.Select(m => (m.Name, m.Type, m.Value)));
    }

    [Fact]
    public void Une_macro_devient_une_constante_avant_les_gears_sauf_si_son_nom_est_deja_pris()
    {
        var header = new CHeader([new CFunction("f", "int", [], false)], [], null, null, null,
            [new CMacro("LIMITE", "int", "9"), new CMacro("f", "int", "1"), new CMacro("string", "int", "2")]);

        var result = CBindings.Generate(header, "mathx.forge", "x86_64-linux-gnu.2.28");

        Assert.Equal(1, result.Constants);
        Assert.True(result.Source.IndexOf("const int LIMITE: 9;", StringComparison.Ordinal) < result.Source.IndexOf("gear f()", StringComparison.Ordinal));
    }

    /// <summary>La chaîne de compilation de Zig, qui compte combien de fois Clang lit un arbre : un cache qui sert ne le lance pas.</summary>
    private sealed class CountingToolchain(ICompilerToolchain inner) : ICompilerToolchain
    {
        public int Dumps { get; private set; }
        public string Name => inner.Name;
        public string Target => inner.Target;

        public ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<Rgl.Emit.RuntimeComponent> components, string? icon, IReadOnlyList<CLibrary>? natives = null) =>
            inner.BuildExecutable(mainCpp, executable, profile, components, icon, natives);

        public ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null) =>
            inner.EmitAssembly(mainCpp, assembly, profile, natives);

        public ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false)
        {
            ++Dumps;
            return inner.DumpHeaderAst(header, includeDirectories, read, cpp);
        }

        public ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => inner.DumpHeaderMacros(header, includeDirectories, read);
        public ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => inner.ListHeaderDependencies(header, includeDirectories, read);
        public ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories) => inner.CompileNativeUnit(source, objectFile, profile, includeDirectories);
    }

    private static bool Load(ICompilerToolchain toolchain, string directory, string cache)
    {
        var main = File.ReadAllText(Path.Combine(directory, "main.rgl"));
        var modules = SourceGraph.Load(new SourceText(Path.Combine(directory, "main.rgl"), main), directory, new DiagnosticBag(), null, null, toolchain, cache);
        return modules is not null;
    }

    [ZigFact]
    public void Les_liaisons_sont_rangees_dans_le_cache_et_refaites_quand_un_en_tete_change()
    {
        var toolchain = new CountingToolchain(ZigToolchain.Locate(SharedCache)!);
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "lib"));
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), "import Petit;\n!train |> $ 0;\n");
        File.WriteAllText(Path.Combine(project.Path, "Petit.forge"), "forge: native\ndescription: \"petit\"\nheader: \"lib/a.h\"\nlink: static\n");
        File.WriteAllText(Path.Combine(project.Path, "lib", "a.h"), "#include \"b.h\"\nint fa(int x);\n");
        File.WriteAllText(Path.Combine(project.Path, "lib", "b.h"), "#define K 5\n");

        // Premier chargement : Clang lit l'arbre. Le deuxième, avec les mêmes fichiers, le trouve dans le cache.
        Assert.True(Load(toolchain, project.Path, cache.Path));
        Assert.Equal(1, toolchain.Dumps);
        Assert.True(Load(toolchain, project.Path, cache.Path));
        Assert.Equal(1, toolchain.Dumps);
        Assert.Single(Directory.GetFiles(cache.Path, "*.rgl"));

        // Un en-tête change : nouvelles liaisons.
        File.AppendAllText(Path.Combine(project.Path, "lib", "a.h"), "int fb(int x);\n");
        Assert.True(Load(toolchain, project.Path, cache.Path));
        Assert.Equal(2, toolchain.Dumps);

        // Un en-tête inclus change : les liaisons de a.h ne sont plus celles du cache non plus.
        File.WriteAllText(Path.Combine(project.Path, "lib", "b.h"), "#define K 6\n");
        Assert.True(Load(toolchain, project.Path, cache.Path));
        Assert.Equal(3, toolchain.Dumps);

        // Sans changement depuis, le cache sert de nouveau.
        Assert.True(Load(toolchain, project.Path, cache.Path));
        Assert.Equal(3, toolchain.Dumps);
    }

    [ZigFact]
    public void Un_cache_abime_est_ignore_et_les_liaisons_refaites()
    {
        var toolchain = new CountingToolchain(ZigToolchain.Locate(SharedCache)!);
        using var project = Project(toolchain, "import Mathx;\n!train |> $ Mathx.add_u(1, 2);\n");
        using var cache = new TempDirectory();

        Assert.True(Load(toolchain, project.Path, cache.Path));
        foreach (var file in Directory.GetFiles(cache.Path, "*.rgl")) File.WriteAllText(file, "n'importe quoi");
        Assert.True(Load(toolchain, project.Path, cache.Path));

        Assert.Equal(2, toolchain.Dumps);
    }

    [Theory]
    [InlineData("maths.o: lib/maths.h", new[] { "lib/maths.h" })]
    [InlineData("a.o: a.h \\\n  b.h c\\ d.h\n", new[] { "a.h", "b.h", "c d.h" })]
    [InlineData("a.o: C:/proj/lib/a.h C:/proj/lib/b.h", new[] { "C:/proj/lib/a.h", "C:/proj/lib/b.h" })]
    [InlineData("", new string[0])]
    public void Le_fichier_Makefile_de_Clang_donne_la_liste_des_dependances(string makefile, string[] expected) =>
        Assert.Equal(expected, NativeCache.Parse(makefile));

    [ZigFact]
    public void Une_cle_ne_se_calcule_pas_si_clang_ne_donne_pas_les_dependances()
    {
        using var directory = new TempDirectory();
        var absent = Path.Combine(directory.Path, "absent.h");
        Assert.Null(NativeCache.Key(ZigToolchain.Locate(SharedCache)!, [absent], [directory.Path], "A.forge"));
    }

    [Fact]
    public void Une_fonction_variadique_ou_au_nom_reserve_est_laissee_de_cote()
    {
        Assert.Contains("nombre variable", Assert.Single(Bindings(new CFunction("formate", "int", [], true), "x86_64-linux-gnu.2.28").Unconverted).Reason);
        Assert.Contains("mot réservé", Assert.Single(Bindings(new CFunction("string", "int", [], false), "x86_64-linux-gnu.2.28").Unconverted).Reason);
    }
}
