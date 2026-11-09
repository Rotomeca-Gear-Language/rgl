using System.Text;
using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Emit;
using Rgl.Native;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary><c>rgl descriptor en-tête --class Classe</c> : le squelette d'un fichier <c>forge: descriptor</c>, écrit d'après les classes que Clang lit dans un en-tête C++.</summary>
public class DescriptorSkeletonTests
{
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    /// <summary>Ce que Clang écrirait pour un en-tête : un espace de noms, une classe riche en cas limites, une classe abstraite et un patron.</summary>
    private const string Ast = """
        {"id":"0x1","kind":"TranslationUnitDecl","inner":[
          {"id":"0x10","kind":"NamespaceDecl","name":"demo","loc":{"file":"HEADER","line":1},"inner":[
            {"id":"0x11","kind":"CXXRecordDecl","name":"Counter","tagUsed":"class","completeDefinition":true,"bases":[{"type":{"qualType":"demo::Base"}}],"inner":[
              {"kind":"CXXRecordDecl","name":"Counter","isImplicit":true},
              {"kind":"AccessSpecDecl","access":"public"},
              {"kind":"CXXConstructorDecl","name":"Counter","type":{"qualType":"void (int, const std::string &)"},"inner":[
                {"kind":"ParmVarDecl","name":"start","type":{"qualType":"int"}},
                {"kind":"ParmVarDecl","name":"nom","type":{"qualType":"const std::string &","desugaredQualType":"const std::basic_string<char> &"}}]},
              {"kind":"CXXConstructorDecl","name":"Counter","explicitlyDeleted":true,"type":{"qualType":"void (const Counter &)"},"inner":[{"kind":"ParmVarDecl","name":"","type":{"qualType":"const Counter &"}}]},
              {"kind":"CXXConstructorDecl","name":"Counter","type":{"qualType":"void (Counter &&) noexcept"},"inner":[{"kind":"ParmVarDecl","name":"o","type":{"qualType":"Counter &&"}}]},
              {"kind":"CXXDestructorDecl","name":"~Counter","type":{"qualType":"void () noexcept"}},
              {"kind":"CXXMethodDecl","name":"add","type":{"qualType":"int (int)"},"inner":[{"kind":"ParmVarDecl","name":"n","type":{"qualType":"int"}}]},
              {"kind":"CXXMethodDecl","name":"total","type":{"qualType":"int () const"}},
              {"kind":"CXXMethodDecl","name":"label","type":{"qualType":"std::string () const"}},
              {"kind":"CXXMethodDecl","name":"rename","type":{"qualType":"void (const std::string &)"},"inner":[{"kind":"ParmVarDecl","name":"nom","type":{"qualType":"const std::string &"}}]},
              {"kind":"CXXMethodDecl","name":"compare","type":{"qualType":"bool (const Counter &) const"},"inner":[{"kind":"ParmVarDecl","name":"autre","type":{"qualType":"const Counter &"}}]},
              {"kind":"CXXMethodDecl","name":"Zero","storageClass":"static","type":{"qualType":"Counter ()"}},
              {"kind":"CXXMethodDecl","name":"destroyed","storageClass":"static","type":{"qualType":"int &()"}},
              {"kind":"CXXMethodDecl","name":"name","type":{"qualType":"const char *() const"}},
              {"kind":"CXXMethodDecl","name":"fill","type":{"qualType":"void (int &)"},"inner":[{"kind":"ParmVarDecl","name":"n","type":{"qualType":"int &"}}]},
              {"kind":"CXXMethodDecl","name":"add","type":{"qualType":"int (int, int)"},"inner":[{"kind":"ParmVarDecl","name":"a","type":{"qualType":"int"}},{"kind":"ParmVarDecl","name":"b","type":{"qualType":"int"}}]},
              {"kind":"CXXMethodDecl","name":"string","type":{"qualType":"int ()"}},
              {"kind":"CXXMethodDecl","name":"operator==","type":{"qualType":"bool (const Counter &) const"},"inner":[{"kind":"ParmVarDecl","name":"o","type":{"qualType":"const Counter &"}}]},
              {"kind":"CXXMethodDecl","name":"copy","type":{"qualType":"void (Counter)"},"inner":[{"kind":"ParmVarDecl","name":"o","type":{"qualType":"Counter"}}]},
              {"kind":"FieldDecl","name":"limite","type":{"qualType":"const int"}},
              {"kind":"FieldDecl","name":"titre","type":{"qualType":"std::string"}},
              {"kind":"AccessSpecDecl","access":"private"},
              {"kind":"CXXMethodDecl","name":"secret","type":{"qualType":"int ()"}},
              {"kind":"FieldDecl","name":"_total","type":{"qualType":"int"}}]},
            {"id":"0x12","kind":"CXXRecordDecl","name":"Forme","tagUsed":"struct","completeDefinition":true,"definitionData":{"isAbstract":true},"inner":[
              {"kind":"CXXConstructorDecl","name":"Forme","type":{"qualType":"void ()"}},
              {"kind":"CXXMethodDecl","name":"aire","virtual":true,"type":{"qualType":"double () const"}}]},
            {"id":"0x13","kind":"ClassTemplateDecl","name":"Boite"}]}
        ]}
        """;

    private sealed class FixtureToolchain(string json) : ICompilerToolchain
    {
        public string Name => "faux Zig";
        public string Target => "x86_64-linux-gnu.2.28";
        public bool? LastCpp { get; private set; }

        public ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false)
        {
            LastCpp = cpp;
            read(new MemoryStream(Encoding.UTF8.GetBytes(json.Replace("HEADER", header.Replace("\\", "/")))));
            return new ToolchainResult(true, "");
        }

        public ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => new(true, "");
        public ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => new(true, "");
        public ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories) => new(true, "");
        public ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<RuntimeComponent> components, string? icon, IReadOnlyList<CLibrary>? natives = null) => new(false, "");
        public ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null) => new(false, "");
    }

    private static (int ExitCode, string Out, string Error) Run(ICompilerToolchain? toolchain, string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, directory, directory, Toolchain: toolchain));
        return (exitCode, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private static TempDirectory Project()
    {
        var project = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "lib"));
        File.WriteAllText(Path.Combine(project.Path, "lib", "counter.hpp"), "// l'AST vient du faux compilateur\n");
        return project;
    }

    [Fact]
    public void Le_squelette_decrit_ce_que_RGL_sait_decrire_et_commente_le_reste_avec_la_raison()
    {
        using var project = Project();
        var toolchain = new FixtureToolchain(Ast);

        var result = Run(toolchain, project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Counter");

        Assert.Equal(CliRunner.Success, result.ExitCode);
        Assert.True(toolchain.LastCpp);
        Assert.StartsWith("// Squelette généré par « rgl descriptor » à partir de lib/counter.hpp", result.Out);
        Assert.Contains("forge: descriptor\n", result.Out);
        Assert.Contains("header: \"lib/counter.hpp\"\nlink: static\n", result.Out);
        Assert.Contains("descriptor demo::Counter -> Counter {\n", result.Out);
        Assert.Contains("// hérite de demo::Base", result.Out);

        // Ce qui se décrit : un constructeur (la copie et le déplacement sont écartés), des méthodes, des champs.
        Assert.Contains("constructor(int start, string nom) => demo::Counter($arg0, std::string($arg1.data(), $arg1.size()));", result.Out);
        Assert.Contains("    add(int n): int => add;", result.Out);
        Assert.Contains("    total(): int => total;", result.Out);
        Assert.Contains("    label(): string => rgl::standard::string(($self.label()).c_str());", result.Out);
        Assert.Contains("    rename(string nom): void => $self.rename(std::string($arg0.data(), $arg0.size()));", result.Out);
        Assert.Contains("    compare(Counter autre): bool => compare;", result.Out);
        Assert.Contains("    static Zero(): Counter => Zero;", result.Out);
        Assert.Contains("    const field limite: int => limite;", result.Out);
        Assert.Contains("    titre(): string => rgl::standard::string(($self.titre).c_str());", result.Out);

        Assert.Contains("set_titre(string value): void => ($self.titre = std::string($arg0.data(), $arg0.size()));", result.Out);

        // Ce qui ne se décrit pas encore reste visible, avec sa raison.
        Assert.Contains("// destroyed() : renvoie une référence", result.Out);
        Assert.Contains("// name() : pointeur", result.Out);
        Assert.Contains("// fill(int &) : référence non constante", result.Out);
        Assert.Contains("add(int a, int b): int => add;", result.Out);
        Assert.Contains("// string() : « string » est un mot réservé de RGL", result.Out);
        Assert.Contains("// copy(Counter) : objet passé par valeur", result.Out);
        Assert.Contains("// operator== : opérateur", result.Out);

        // Le privé, le destructeur et ce que Clang ajoute de lui-même n'y sont pas.
        Assert.DoesNotContain("secret", result.Out);
        Assert.DoesNotContain("_total", result.Out);
        Assert.DoesNotContain("~Counter", result.Out);
    }

    [Fact]
    public void Une_classe_abstraite_n_a_pas_de_constructeur()
    {
        using var project = Project();

        var result = Run(new FixtureToolchain(Ast), project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Forme", "--name", "Shape");

        Assert.Equal(CliRunner.Success, result.ExitCode);
        Assert.Contains("descriptor demo::Forme -> Shape {", result.Out);
        Assert.Contains("// classe abstraite : pas de constructeur", result.Out);
        Assert.DoesNotContain("constructor(", result.Out);
        Assert.Contains("aire(): double => aire;", result.Out);
    }

    [Fact]
    public void Le_squelette_s_ecrit_dans_un_fichier_avec_un_chemin_d_en_tete_relatif_et_ne_l_ecrase_jamais()
    {
        using var project = Project();
        var toolchain = new FixtureToolchain(Ast);

        var first = Run(toolchain, project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Counter", "-o", "src/Cpp.forge");
        var second = Run(toolchain, project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Counter", "-o", "src/Cpp.forge");

        Assert.Equal(CliRunner.Success, first.ExitCode);
        Assert.Contains("Squelette écrit dans", first.Out);
        var written = File.ReadAllText(Path.Combine(project.Path, "src", "Cpp.forge"));
        Assert.Contains("header: \"../lib/counter.hpp\"", written);
        Assert.Equal(CliRunner.Failure, second.ExitCode);
        Assert.Contains("existe déjà", second.Error);
    }

    [Fact]
    public void Une_classe_inconnue_ou_un_patron_est_signale_avec_les_classes_de_l_en_tete()
    {
        using var project = Project();
        var toolchain = new FixtureToolchain(Ast);

        var unknown = Run(toolchain, project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Autre");
        var template = Run(toolchain, project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Boite");

        Assert.Equal(CliRunner.Failure, unknown.ExitCode);
        Assert.Contains("la classe « demo::Autre » n'est pas définie dans counter.hpp", unknown.Error);
        Assert.Contains("classes définies : demo::Counter, demo::Forme", unknown.Error);
        Assert.Contains("patron de classe", template.Error);
    }

    [Fact]
    public void Un_en_tete_introuvable_ou_sans_chaine_de_compilation_est_une_erreur()
    {
        using var project = Project();

        var missing = Run(new FixtureToolchain(Ast), project.Path, "descriptor", "lib/absent.hpp", "--class", "demo::Counter");
        var withoutToolchain = Run(null, project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Counter");

        Assert.Equal(CliRunner.Failure, missing.ExitCode);
        Assert.Contains("introuvable", missing.Error);
        Assert.Equal(CliRunner.Failure, withoutToolchain.ExitCode);
        Assert.Contains(Rgl.Diagnostics.DiagnosticCodes.UnreadableHeader, withoutToolchain.Error);
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("descriptor", "lib/counter.hpp")]
    [InlineData("descriptor", "--class", "X")]
    [InlineData("descriptor", "a.hpp", "b.hpp", "--class", "X")]
    [InlineData("descriptor", "a.hpp", "--class")]
    [InlineData("descriptor", "a.hpp", "--inconnue", "--class", "X")]
    public void Une_ligne_de_commande_incomplete_est_une_erreur_d_usage(params string[] args)
    {
        using var project = Project();

        Assert.Equal(CliRunner.UsageError, Run(null, project.Path, args).ExitCode);
    }

    [Theory]
    [InlineData("int (int, int)", "int", "")]
    [InlineData("int () const", "int", "const")]
    [InlineData("void (Counter &&) noexcept", "void", "noexcept")]
    [InlineData("int &()", "int &", "")]
    [InlineData("std::string () const noexcept", "std::string", "const noexcept")]
    public void Le_type_de_retour_se_separe_de_la_liste_des_parametres(string signature, string returnType, string qualifiers)
    {
        Assert.True(CppReader._SplitSignature(signature, out var actualReturn, out var actualQualifiers));
        Assert.Equal((returnType, qualifiers), (actualReturn, actualQualifiers));
    }

    [ZigFact]
    public void Le_squelette_lu_par_Clang_se_compile_et_s_utilise_tel_quel()
    {
        using var project = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "lib"));
        File.WriteAllText(Path.Combine(project.Path, "lib", "counter.hpp"), """
            #pragma once
            #include <string>
            namespace demo {
            class Counter {
            public:
                std::string titre;
                explicit Counter(int start) : _total(start) {}
                Counter(const Counter&) = delete;
                Counter(Counter&& o) noexcept : _total(o._total) {}
                int add(int n) { _total += n; return _total; }
                int total() const { return _total; }
                std::string label() const { return "compteur=" + std::to_string(_total); }
                static Counter Zero() { return Counter(0); }
            private:
                int _total;
            };
            }
            """);
        var toolchain = ZigToolchain.Locate(SharedCache)!;

        // Le squelette de la classe, écrit par la commande.
        var generated = Run(toolchain, project.Path, "descriptor", "lib/counter.hpp", "--class", "demo::Counter", "-o", "Cpp.forge");
        Assert.True(generated.ExitCode == CliRunner.Success, generated.Error);

        // Un programme qui s'en sert, sans rien y changer.
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), """
            import Cpp;
            gear texte(string s): void { s -> console; }
            guard calcule(): string {
                Cpp.Counter c: Cpp.Counter.create(10);
                int n: c.add(5);
                c.set_titre(c.label());
                return c.titre();
            }
            train |> calcule !> [$ 9] |> texte |> done;
            """);
        var compile = Run(toolchain, project.Path, "compile", "main.rgl");
        Assert.True(compile.ExitCode == CliRunner.Success, compile.Error);

        var run = ZigBuild.Run(Path.Combine(project.Path, "out", "debug", OperatingSystem.IsWindows() ? "main.exe" : "main"));

        Assert.Equal((0, "compteur=15"), (run.Code, run.Output.Replace("\r\n", "\n")));
    }
}
