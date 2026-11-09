using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>
/// Les descriptors : <c>descriptor demo::Counter -&gt; Compteur { … }</c> donne à une classe C++ son identité RGL. La valeur RGL est un type opaque et déplaçable dont le
/// destructeur C++ s'appelle tout seul ; ses membres sont des guards dont le C++ est l'expression écrite dans le descriptor.
/// </summary>
public class NativeDescriptorTests
{
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    private const string Header = """
        #pragma once
        #include <stdexcept>
        #include <string>
        namespace demo {
        class Counter {
        public:
            explicit Counter(int start) : _total(start), _capacity(100) {}
            Counter(const Counter&) = delete;
            Counter(Counter&& o) noexcept : _total(o._total), _capacity(o._capacity) { o._moved = true; }
            ~Counter() { if (!_moved) ++destroyed(); }
            int add(int n) {
                if (_total + n > _capacity) throw std::overflow_error("capacite depassee");
                _total += n;
                return _total;
            }
            int total() const { return _total; }
            std::string label() const { return "compteur=" + std::to_string(_total); }
            static Counter Zero() { return Counter(0); }
            static int& destroyed() { static int d = 0; return d; }
        private:
            int _total;
            int _capacity;
            bool _moved = false;
        };
        }
        """;

    private const string Descriptor = """
        forge: descriptor
        description: "La classe Counter"
        header: "lib/counter.hpp"
        link: static

        descriptor demo::Counter -> Compteur {
            constructor(int start) => demo::Counter(start);
            add(int n): int => add;
            const field total: int => $self.total();
            label(): string => rgl::standard::string($self.label().c_str());
            static Zero(): Compteur => Zero;
            static Detruits(): int => demo::Counter::destroyed();
        }
        """;

    private static TempDirectory Project(string main, string? descriptor = null)
    {
        var project = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "lib"));
        File.WriteAllText(Path.Combine(project.Path, "lib", "counter.hpp"), Header);
        File.WriteAllText(Path.Combine(project.Path, "Cpp.forge"), descriptor ?? Descriptor);
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), main);
        return project;
    }

    private static (int ExitCode, string Out, string Error) Run(ICompilerToolchain? toolchain, string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, directory, SharedCache, Toolchain: toolchain));
        return (exitCode, output.ToString(), error.ToString());
    }

    private static (int Code, string Output, string Error) Exec(string main)
    {
        using var project = Project(main);
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        if (compile.ExitCode != 0) return (compile.ExitCode, "", compile.Error);

        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        return (run.Code, run.Output.Replace("\r\n", "\n"), compile.Error);
    }

    [ZigFact]
    public void Descriptors_generiques_surcharges_et_champs_modifiables()
    {
        using var project = Project("""
            import Cpp;
            gear afficher(int n): void { `{n}` -> console; }
            guard calcule(): int {
                Cpp.Boite<int> b: Cpp.Boite<int>.create(5);
                b.set_value(12);
                int a: b.add(3);
                int c: b.add(2, 4);
                return a + c + b.value();
            }
            guard apres(): int {
                int n: calcule();
                return n + Cpp.Boite<int>.Detruits();
            }
            train |> apres !> [$ 9] |> afficher |> done;
            """, """
            forge: descriptor
            description: "Boite generique"
            header: "lib/counter.hpp"
            link: static
            descriptor demo::Box<T> -> Boite<T> {
                constructor(T start) => demo::Box<T>(start);
                field value: T => value;
                add(T n): T => add;
                add(T n, T m): T => add;
                static Detruits(): int => demo::destroyed();
            }
            """);
        File.WriteAllText(Path.Combine(project.Path, "lib", "counter.hpp"), """
            #pragma once
            namespace demo {
            inline int& destroyed() { static int count = 0; return count; }
            template<typename T> class Box {
            public:
                T value;
                explicit Box(T start): value(start) {}
                ~Box() { ++destroyed(); }
                T add(T n) { return value + n; }
                T add(T n, T m) { return value + n + m; }
            };
            }
            """);
        var compile = Run(ZigToolchain.Locate(SharedCache), project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == 0, compile.Error);
        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));
        Assert.Equal((0, "46"), (run.Code, run.Output));
    }

    private const string Show = """
        gear afficher(int n): void { `{n}` -> console; }
        gear texte(string s): void { s -> console; }
        gear rapporter(std.NativeError e): void { `[{e.message}] [{e.callee}]` -> console; }
        """;

    [ZigFact]
    public void Un_objet_C_plus_plus_se_cree_se_lit_et_s_appelle_depuis_RGL()
    {
        var result = Exec("import Cpp;\n" + Show + """

            guard calcule(): int {
                Cpp.Compteur c: Cpp.Compteur.create(10);
                int a: c.add(5);
                int b: c.total();
                return a + b;
            }
            train |> calcule !> [$ 9] |> afficher |> done;
            """);

        Assert.Equal((0, "30"), (result.Code, result.Output));
    }

    [ZigFact]
    public void Une_chaine_et_une_methode_statique_qui_renvoie_un_objet_passent_la_frontiere()
    {
        var result = Exec("import Cpp;\n" + Show + """

            guard decrit(): string {
                Cpp.Compteur c: Cpp.Compteur.Zero();
                int n: c.add(11);
                return c.label();
            }
            train |> decrit !> [$ 9] |> texte |> done;
            """);

        Assert.Equal((0, "compteur=11"), (result.Code, result.Output));
    }

    [ZigFact]
    public void Une_exception_de_la_classe_arrive_au_rail_en_NativeError()
    {
        var result = Exec("import Cpp;\n" + Show + """

            guard deborde(): int {
                Cpp.Compteur c: Cpp.Compteur.create(10);
                return c.add(500);
            }
            train |> deborde !> [rapporter |> done] |> afficher |> done;
            """);

        Assert.Equal((0, "[capacite depassee] [Compteur.add]"), (result.Code, result.Output));
    }

    [ZigFact]
    public void Le_destructeur_C_plus_plus_s_appelle_une_seule_fois_quand_la_valeur_sort_de_portee()
    {
        var result = Exec("import Cpp;\n" + Show + """

            guard travail(): int {
                Cpp.Compteur c: Cpp.Compteur.create(1);
                return c.total();
            }
            guard apres(): int {
                int t: travail();
                return Cpp.Compteur.Detruits();
            }
            train |> apres !> [$ 9] |> afficher |> done;
            """);

        Assert.Equal((0, "1"), (result.Code, result.Output));
    }

    [Fact]
    public void Un_objet_C_plus_plus_ne_se_copie_pas_il_se_deplace()
    {
        using var project = Project("import Cpp;\n" + Show + """

            guard copie(): int {
                Cpp.Compteur c: Cpp.Compteur.create(1);
                Cpp.Compteur d: c;
                return d.total();
            }
            train |> copie !> [$ 9] |> afficher |> done;
            """);

        var compile = Run(null, project.Path, "compile", "main.rgl");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains(DiagnosticCodes.LifecycleCopy, compile.Error);
    }

    #region Fichiers invalides

    private static (bool Loaded, IReadOnlyList<Diagnostic> Items) Load(string directory)
    {
        var diagnostics = new DiagnosticBag();
        var path = Path.Combine(directory, "main.rgl");
        var modules = SourceGraph.Load(new SourceText(path, File.ReadAllText(path)), directory, diagnostics);
        return (modules is not null, diagnostics.Items);
    }

    [Fact]
    public void Un_descriptor_s_ecrit_dans_un_fichier_forge_descriptor()
    {
        using var project = Project("import Cpp;\n!train |> $ 0;\n", "forge: gears\ndescription: \"x\"\n\ndescriptor demo::Counter -> Compteur { constructor() => demo::Counter(); }\n");

        var (loaded, items) = Load(project.Path);

        Assert.False(loaded);
        Assert.Contains(items, d => d.Code == DiagnosticCodes.MisplacedDeclaration && d.Message.Contains("fichier forge: descriptor"));
    }

    [Fact]
    public void Un_fichier_descriptor_demande_un_header_et_n_a_que_des_descriptors()
    {
        using var project = Project("import Cpp;\n!train |> $ 0;\n", "forge: descriptor\ndescription: \"x\"\nlink: static\n\ngear f(): int => 1;\n");

        var (loaded, items) = Load(project.Path);

        Assert.False(loaded);
        Assert.Contains(items, d => d.Message.Contains("header:"));
        Assert.Contains(items, d => d.Message.Contains("n'a pas sa place"));
    }

    [Fact]
    public void Un_membre_en_double_ou_avec_une_option_non_prise_en_charge_est_E145()
    {
        using var project = Project("import Cpp;\n!train |> $ 0;\n", """
            forge: descriptor
            description: "x"
            header: "lib/counter.hpp"
            link: static

            descriptor demo::Counter -> Compteur {
                total(): int => total;
                total(): int => total;
                add(mut int n): int => add;
            }
            """);

        var (loaded, items) = Load(project.Path);

        Assert.False(loaded);
        Assert.Contains(items, d => d.Code == DiagnosticCodes.NativeComponent && d.Message.Contains("déjà un membre"));
        Assert.Contains(items, d => d.Code == DiagnosticCodes.NativeComponent && d.Message.Contains("n'a ni mut"));
    }

    [Fact]
    public void Un_header_de_descriptor_introuvable_est_signale()
    {
        using var project = Project("import Cpp;\n!train |> $ 0;\n");
        File.Delete(Path.Combine(project.Path, "lib", "counter.hpp"));

        var (loaded, items) = Load(project.Path);

        Assert.False(loaded);
        Assert.Contains(items, d => d.Code == DiagnosticCodes.ImportNotFound && d.Message.Contains("counter.hpp"));
    }

    #endregion
}
