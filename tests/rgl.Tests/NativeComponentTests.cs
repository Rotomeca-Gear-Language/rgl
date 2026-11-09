using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Native;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>
/// Les composants écrits en C++ : un guard déclaré sans corps (<c>guard normalise(string t): string;</c>) dont <c>normalise.rgl.cpp</c> est l'implémentation.
/// Le C++ s'exécute dans une unité compilée avec les exceptions ; une exception y devient une <c>NativeError</c> sur le rail.
/// </summary>
public class NativeComponentTests
{
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    private const string Declarations = """
        forge: gears
        description: "Composants en C++"

        guard normalise(string texte): string;
        guard divise(int a, int b): int;
        guard echoue(int n): int;
        guard note(string texte, double poids): void;
        """;

    private const string Normalise = """
        #include <stdexcept>
        #include <string>

        namespace rgl_native {
        rgl::standard::string normalise(const rgl::standard::string& texte) {
            if (texte.size() == 0) throw std::invalid_argument("texte vide");
            std::string resultat(texte.data(), texte.size());
            for (auto& c : resultat) if (c >= 'a' && c <= 'z') c = static_cast<char>(c - 'a' + 'A');
            return rgl::standard::string(resultat.c_str(), resultat.size());
        }
        }
        """;

    private const string Divise = """
        #include <stdexcept>

        namespace rgl_native {
        std::int32_t divise(std::int32_t a, std::int32_t b) {
            if (b == 0) throw std::domain_error("division par zéro, en C++");
            return a / b;
        }
        }
        """;

    private const string Echoue = """
        namespace rgl_native {
        std::int32_t echoue(std::int32_t) { throw 42; }
        }
        """;

    private const string Note = """
        namespace rgl_native {
        void note(const rgl::standard::string&, double) {}
        }
        """;

    private static TempDirectory Project(string main, string? declarations = null, bool components = true)
    {
        var project = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), main);
        File.WriteAllText(Path.Combine(project.Path, "Texte.forge"), declarations ?? Declarations);
        if (components)
        {
            File.WriteAllText(Path.Combine(project.Path, "normalise.rgl.cpp"), Normalise);
            File.WriteAllText(Path.Combine(project.Path, "divise.rgl.cpp"), Divise);
            File.WriteAllText(Path.Combine(project.Path, "echoue.rgl.cpp"), Echoue);
            File.WriteAllText(Path.Combine(project.Path, "note.rgl.cpp"), Note);
        }
        return project;
    }

    private static (int ExitCode, string Out, string Error) Run(ICompilerToolchain? toolchain, string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, directory, SharedCache, Toolchain: toolchain));
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Compile et lance le programme ; ce qu'il écrit sur la sortie standard.</summary>
    private static (int Code, string Output, string Error) Exec(string main)
    {
        using var project = Project(main);
        var toolchain = ZigToolchain.Locate(SharedCache);
        var compile = Run(toolchain, project.Path, "compile", "main.rgl");
        if (compile.ExitCode != 0) return (compile.ExitCode, "", compile.Error);

        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        return (run.Code, run.Output.Replace("\r\n", "\n"), compile.Error);
    }

    [ZigFact]
    public void Un_tableau_passe_la_frontiere_C_plus_plus()
    {
        using var project = Project("""
            import Texte;
            gear afficher(int n): void { `{n}` -> console; }
            guard calcul(): int { return Texte.somme([3, 7]); }
            train |> calcul !> [$ 9] |> afficher |> done;
            """, "forge: gears\ndescription: \"Tableau\"\nguard somme([int] valeurs): int;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "somme.rgl.cpp"), "namespace rgl_native { std::int32_t somme(const rgl::standard::array<std::int32_t>& a) { return a[0] + a[1]; } }");
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == 0, compile.Error);
        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        Assert.Equal((0, "10"), (run.Code, run.Output));
    }

    [ZigFact]
    public void Un_train_par_defaut_est_implemente_en_C_plus_plus()
    {
        using var project = Project("""
            import Texte;
            gear afficher(int n): void { `{n}` -> console; }
            train 6 |> Texte !> [$ 9] |> afficher |> done;
            """, "forge: train\ndescription: \"Train C++\"\ntrain(int n): int;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "Texte.rgl.cpp"), "namespace rgl_native { std::int32_t Texte(std::int32_t n) { return n * 7; } }");
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == 0, compile.Error);
        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        Assert.Equal((0, "42"), (run.Code, run.Output));
    }

    [ZigFact]
    public void Molds_enums_et_tableaux_de_molds_passent_la_frontiere()
    {
        using var project = Project("""
            import Texte;
            gear afficher(int n): void { `{n}` -> console; }
            guard calcul(): int {
                Texte.Point p: {x: 3, y: 4};
                Texte.Point q: Texte.doublePoint(p);
                [Texte.Point] points: [p, q];
                int n: Texte.litPoints(pack(points));
                int m: Texte.litGroupes(pack([points]));
                return n + m + Texte.litCouleur(Texte.Couleur.Vert);
            }
            train |> calcul !> [$ 9] |> afficher |> done;
            """, """
            forge: gears
            description: "Types composes"
            mold Point { int x; int y; }
            enum Couleur { Rouge, Vert }
            guard doublePoint(Point p): Point;
            guard litPoints([Point] points): int;
            guard litGroupes([[Point]] groupes): int;
            guard litCouleur(Couleur c): int;
            """, components: false);
        File.WriteAllText(Path.Combine(project.Path, "doublePoint.rgl.cpp"), "namespace rgl_native { rgl_m_Texte_d_Point doublePoint(const rgl_m_Texte_d_Point& p) { return {p.rgl_f_x * 2, p.rgl_f_y * 2}; } }");
        File.WriteAllText(Path.Combine(project.Path, "litPoints.rgl.cpp"), "namespace rgl_native { std::int32_t litPoints(const rgl::standard::array<rgl_m_Texte_d_Point>& p) { return p[0].rgl_f_x + p[1].rgl_f_y; } }");
        File.WriteAllText(Path.Combine(project.Path, "litGroupes.rgl.cpp"), "namespace rgl_native { std::int32_t litGroupes(const rgl::standard::array<rgl::standard::array<rgl_m_Texte_d_Point>>& p) { return p[0][1].rgl_f_y; } }");
        File.WriteAllText(Path.Combine(project.Path, "litCouleur.rgl.cpp"), "namespace rgl_native { std::int32_t litCouleur(rgl_e_Texte_d_Couleur c) { return static_cast<std::int32_t>(c); } }");
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == 0, compile.Error);
        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        Assert.Equal((0, "20"), (run.Code, run.Output));
    }

    [ZigFact]
    public void Le_cache_natif_reutilise_l_objet_et_s_invalide_si_un_header_change()
    {
        using var directory = new TempDirectory();
        var header = Path.Combine(directory.Path, "value.hpp");
        var source = Path.Combine(directory.Path, "unit.cpp");
        var objectFile = Path.Combine(directory.Path, "unit.o");
        File.WriteAllText(header, "inline int value() { return 1; }");
        File.WriteAllText(source, "#include \"value.hpp\"\nint answer() { return value(); }");
        var toolchain = ZigToolchain.Locate(SharedCache)!;
        var first = toolchain.CompileNativeUnit(source, objectFile, BuildProfile.Debug, [directory.Path]);
        Assert.True(first.Success, first.Log);
        var before = File.ReadAllBytes(objectFile);
        var second = toolchain.CompileNativeUnit(source, objectFile, BuildProfile.Debug, [directory.Path]);
        Assert.True(second.Success, second.Log);
        Assert.Contains("réutilisée depuis le cache", second.Log);
        File.WriteAllText(header, "inline int value() { return 7; }");
        var third = toolchain.CompileNativeUnit(source, objectFile, BuildProfile.Debug, [directory.Path]);
        Assert.True(third.Success, third.Log);
        Assert.DoesNotContain("réutilisée depuis le cache", third.Log);
        Assert.False(before.SequenceEqual(File.ReadAllBytes(objectFile)));
    }

    [Theory]
    [InlineData("E148")]
    public void Un_tableau_de_molds_exige_pack_a_la_frontiere(string expectedCode)
    {
        using var project = Project("""
            import Texte;
            guard calcul(): int {
                Texte.Point p: {x: 3};
                [Texte.Point] points: [p];
                return Texte.f(points);
            }
            train |> calcul !> [$ 9] |> $ 0;
            """, "forge: gears\ndescription: \"pack\"\nmold Point { int x; }\nguard f([Point] points): int;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "f.rgl.cpp"), "// liaison non compilée : le binder doit refuser l'appel");
        var compile = Run(null, project.Path, "compile", "main.rgl");
        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains(expectedCode, compile.Error);
    }

    [ZigFact]
    public void Un_gear_unsafe_echange_des_pointeurs_avec_un_composant_CPP()
    {
        using var project = Project("""
            import Texte;
            unsafe calcul(): int {
                ptr<int> p: Texte.cree(42);
                int n: Texte.lit(p);
                delete p;
                return n;
            }
            emit sortie(int n): uint { if (n eq 42) { return 0; } return 1; }
            !train |> calcul |> sortie;
            """, "forge: gears\ndescription: \"Pointeurs\"\nunsafe cree(int n): ptr<int>;\nunsafe lit(ptr<int> p): int;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "cree.rgl.cpp"), "namespace rgl_native { std::int32_t* cree(std::int32_t n) { return new std::int32_t(n); } }");
        File.WriteAllText(Path.Combine(project.Path, "lit.rgl.cpp"), "namespace rgl_native { std::int32_t lit(std::int32_t* p) { return *p; } }");
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == 0, compile.Error);
        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        Assert.Equal(0, run.Code);
    }

    [ZigFact]
    public void Un_guard_natif_renvoie_un_ptr_au_train_qui_appelle_unsafe()
    {
        using var project = Project("""
            import Texte;
            unsafe lit(ptr<int> p): int { return *p; }
            emit sortie(int n): uint { if (n eq 42) { return 0; } return 1; }
            train 42 |> Texte.cree !> [$ 1] |> lit |> sortie;
            """, "forge: gears\ndescription: \"Pointeur sur rail\"\nguard cree(int n): ptr<int>;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "cree.rgl.cpp"), "namespace rgl_native { std::int32_t* cree(std::int32_t n) { return new std::int32_t(n); } }");
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == 0, compile.Error);
        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        Assert.Equal(0, run.Code);
    }

    [Theory]
    [InlineData("E147")]
    public void Les_pointeurs_natifs_rendent_un_train_non_sur(string expectedCode)
    {
        using var project = Project("import Texte;\ntrain 42 |> Texte.cree !> [$ 1] |> Texte.lit !> [$ 1] |> $ 0;\n",
            "forge: gears\ndescription: \"safe\"\nguard cree(int n): ptr<int>;\nguard lit(ptr<int> p): int;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "rgl.json"), "{\"name\":\"test\",\"version\":\"0.1.0\",\"rgl\":\"0\",\"safe\":true}");
        File.WriteAllText(Path.Combine(project.Path, "cree.rgl.cpp"), "// pas compilé : refus sémantique");
        File.WriteAllText(Path.Combine(project.Path, "lit.rgl.cpp"), "// pas compilé : refus sémantique");
        var compile = Run(null, project.Path, "compile", "main.rgl");
        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains(expectedCode, compile.Error);
    }

    [ZigFact]
    public void Un_pointeur_vers_un_tableau_RGL_garde_son_layout_dans_l_unite_native()
    {
        using var project = Project("""
            import Texte;
            unsafe calcul(): int {
                [Texte.Point] points: [{x: 42, y: 1}];
                ptr<[Texte.Point]> cellule: points;
                return Texte.lire(cellule);
            }
            emit sortie(int n): uint { if (n eq 42) { return 0; } return 1; }
            !train |> calcul |> sortie;
            """, "forge: gears\ndescription: \"Pointeur de tableau\"\nmold Point { int x; int y; }\nunsafe lire(ptr<[Point]> p): int;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "lire.rgl.cpp"), "namespace rgl_native { std::int32_t lire(rgl::standard::soa_array<rgl_l_m_Texte_d_Point>* p) { return (*p)[0].rgl_f_x; } }");
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == 0, compile.Error);
        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        Assert.Equal(0, run.Code);
    }

    private const string Report = """
        import Texte;
        gear afficher(string s): void { s -> console; }
        gear rapporter(std.NativeError e): void { `[{e.message}] [{e.callee}] [{e.where}]` -> console; }
        """;

    [ZigFact]
    public void Un_guard_implemente_en_C_plus_plus_rend_sa_valeur()
    {
        var result = Exec(Report + "\ntrain \"bonjour\" |> Texte.normalise !> [rapporter |> done] |> afficher |> done;\n");

        Assert.Equal((0, "BONJOUR"), (result.Code, result.Output));
    }

    [ZigFact]
    public void Une_exception_standard_du_C_plus_plus_arrive_au_rail_en_NativeError()
    {
        var result = Exec(Report + "\ntrain \"\" |> Texte.normalise !> [rapporter |> done] |> afficher |> done;\n");

        Assert.Equal(0, result.Code);
        Assert.Matches(@"^\[texte vide\] \[normalise\] \[main\.rgl:\d+:\d+\]$", result.Output);
    }

    [ZigFact]
    public void Une_exception_qui_n_est_pas_une_std_exception_a_un_message_de_repli()
    {
        var result = Exec("""
            import Texte;
            gear rapporter(std.NativeError e): void { `[{e.message}] [{e.callee}]` -> console; }
            guard calcule(): int { return Texte.echoue(1); }
            gear afficher(int n): void { `{n}` -> console; }
            train |> calcule !> [rapporter |> done] |> afficher |> done;
            """);

        Assert.Equal((0, "[exception C++ inconnue] [echoue]"), (result.Code, result.Output));
    }

    [ZigFact]
    public void Les_entiers_passent_la_frontiere_et_l_erreur_se_propage_d_un_guard_a_l_autre()
    {
        var ok = Exec("""
            import Texte;
            guard calcule(): int { return Texte.divise(100, 5); }
            gear afficher(int n): void { `{n}` -> console; }
            train |> calcule !> [$ 9] |> afficher |> done;
            """);
        var echec = Exec("""
            import Texte;
            guard calcule(): int { return Texte.divise(100, 0); }
            gear rapporter(std.NativeError e): void { `[{e.message}] [{e.callee}]` -> console; }
            train |> calcule !> [rapporter |> done] |> done;
            """);

        Assert.Equal((0, "20"), (ok.Code, ok.Output));
        Assert.Equal((0, "[division par zéro, en C++] [divise]"), (echec.Code, echec.Output));
    }

    [ZigFact]
    public void Un_guard_void_et_un_flottant_passent_aussi()
    {
        var result = Exec("""
            import Texte;
            guard fais(): void { Texte.note("a", 2.5); }
            train |> fais !> [$ 9] |> done;
            """);

        Assert.Equal(0, result.Code);
    }

    [ZigFact]
    public void Une_erreur_de_C_plus_plus_est_celle_du_fichier_pas_un_bug_du_transpileur()
    {
        using var project = Project("import Texte;\ntrain \"a\" |> Texte.normalise !> [$ 9] |> done;\n");
        File.WriteAllText(Path.Combine(project.Path, "normalise.rgl.cpp"), "namespace rgl_native {\nrgl::standard::string normalise(const rgl::standard::string&) { return 1 +; }\n}\n");

        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("E146", compile.Error);
        Assert.Contains("le C++ de normalise.rgl.cpp ne compile pas", compile.Error);
        Assert.DoesNotContain("bug du transpileur", compile.Error);
    }

    #region Déclarations invalides

    private static (bool Loaded, IReadOnlyList<Diagnostic> Items) Load(string directory)
    {
        var diagnostics = new DiagnosticBag();
        var path = Path.Combine(directory, "main.rgl");
        var modules = SourceGraph.Load(new SourceText(path, File.ReadAllText(path)), directory, diagnostics);
        return (modules is not null, diagnostics.Items);
    }

    [Fact]
    public void Un_guard_sans_corps_demande_son_fichier_rgl_cpp()
    {
        using var project = Project("import Texte;\n!train |> $ 0;\n", components: false);

        var (loaded, items) = Load(project.Path);

        Assert.False(loaded);
        Assert.Contains(items, d => d.Code == DiagnosticCodes.NativeComponent && d.Message.Contains("normalise.rgl.cpp est introuvable"));
    }

    [Theory]
    [InlineData("gear f(int n): int;", "c'est un guard")]
    [InlineData("guard f<T>(T n): T;", "pas générique")]
    [InlineData("guard f(mut [int] n): int;", "n'a ni mut")]
    [InlineData("guard f(int n: 3): int;", "valeur par défaut")]
    public void Un_composant_en_C_plus_plus_mal_declare_est_E145(string declaration, string reason)
    {
        using var project = Project("import Texte;\n!train |> $ 0;\n", "forge: gears\ndescription: \"x\"\n\n" + declaration + "\n", components: false);

        var (loaded, items) = Load(project.Path);

        Assert.False(loaded);
        Assert.Contains(items, d => d.Code == DiagnosticCodes.NativeComponent && d.Message.Contains(reason));
    }

    [Fact]
    public void Un_type_que_le_C_plus_plus_ne_recoit_pas_encore_est_E145()
    {
        using var project = Project("import Texte;\n!train |> $ 0;\n", "forge: gears\ndescription: \"x\"\n\nguard f([string:int] liste): int;\n", components: false);
        File.WriteAllText(Path.Combine(project.Path, "f.rgl.cpp"), "namespace rgl_native { std::int32_t f(const rgl::standard::array<std::int32_t>&) { return 0; } }\n");

        var compile = Run(null, project.Path, "compile", "main.rgl");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("E145", compile.Error);
        Assert.Contains("ne reçoit pas encore ce type", compile.Error);
    }

    [Fact]
    public void Un_guard_sans_corps_n_est_pas_permis_dans_le_fichier_rgl_d_entree()
    {
        using var project = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), "guard f(int n): int;\n!train |> $ 0;\n");

        var (loaded, items) = Load(project.Path);

        Assert.False(loaded);
        Assert.Contains(items, d => d.Code == DiagnosticCodes.MisplacedDeclaration && d.Message.Contains("fichier forge: gears"));
    }

    #endregion

    /// <summary>Une chaîne de compilation dont la compilation d'une unité C++ échoue : la sortie de Clang est celle d'une erreur du fichier de l'utilisateur.</summary>
    private sealed class UnitFailingToolchain : ICompilerToolchain
    {
        public string Name => "faux compilateur";
        public string Target => "x86_64-windows-gnu";
        public ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<RuntimeComponent> components, string? icon, IReadOnlyList<CLibrary>? natives = null) => new(true, "");
        public ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null) => new(true, "");
        public ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false) => new(false, "");
        public ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => new(false, "");
        public ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => new(false, "");
        public ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories) =>
            new(false, "$ zig c++ …" + Environment.NewLine + "normalise.rgl.cpp:3:5: error: expected expression" + Environment.NewLine);
    }

    [Fact]
    public void Le_C_plus_plus_que_Clang_refuse_est_E146_avec_ce_que_Clang_en_dit()
    {
        using var project = Project("import Texte;\ntrain \"a\" |> Texte.normalise !> [$ 9] |> done;\n");

        var compile = Run(new UnitFailingToolchain(), project.Path, "compile", "main.rgl");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("E146", compile.Error);
        Assert.Contains("le C++ de normalise.rgl.cpp ne compile pas", compile.Error);
        Assert.Contains("expected expression", compile.Error);
        Assert.DoesNotContain("bug du transpileur", compile.Error);
    }

    [Fact]
    public void Le_C_plus_plus_engendre_inclut_le_fichier_de_l_utilisateur_et_n_appelle_que_par_l_appel_protege()
    {
        using var project = Project("import Texte;\ntrain \"a\" |> Texte.normalise !> [$ 9] |> done;\n");

        using var cache = new TempDirectory();
        var exit = CliRunner.Run(["compile", "main.rgl", "--emit-cpp"], new CliContext(new StringWriter(), new StringWriter(), project.Path, cache.Path));
        Assert.Equal(CliRunner.Success, exit);
        var directory = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(cache.Path, "Texte_d_normalise.rgl.gen.cpp", SearchOption.AllDirectories)))!;
        var unit = File.ReadAllText(Path.Combine(directory, "Texte_d_normalise.rgl.gen.cpp"));
        var main = File.ReadAllText(Path.Combine(directory, "main.cpp"));

        Assert.Contains("normalise.rgl.cpp", unit);
        Assert.Contains("namespace rgl_native", unit);
        Assert.Contains("rgl_native_guard(&rgl_nat_Texte_d_normalise_thunk", main);
        Assert.DoesNotContain("try {", main);
        Assert.DoesNotContain("catch (", main);
    }
}
