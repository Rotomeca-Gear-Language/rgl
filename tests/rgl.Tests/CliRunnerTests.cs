using Rgl.Build.Icons;
using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Tests de bout en bout de <see cref="CliRunner"/> : codes de sortie et messages affichés.</summary>
public class CliRunnerTests
{
    /// <summary>Exécute <c>rgl</c> avec les arguments donnés, sans chaîne de compilation, et capture ses deux sorties.</summary>
    private static (int ExitCode, string Out, string Error) Run(string workingDirectory, string cacheRoot, params string[] args) =>
        RunWith(null, workingDirectory, cacheRoot, args);

    /// <summary>Exécute <c>rgl</c> avec la chaîne de compilation <paramref name="toolchain"/> et capture ses deux sorties.</summary>
    private static (int ExitCode, string Out, string Error) RunWith(ICompilerToolchain? toolchain, string workingDirectory, string cacheRoot, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, workingDirectory, cacheRoot, Toolchain: toolchain));
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Cache partagé par les tests qui compilent pour de vrai : le runtime n'y est compilé qu'une fois
    /// (une vingtaine de secondes), puis réutilisé.
    /// </summary>
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    /// <summary>Chaîne de compilation qui échoue toujours, comme si le C++ généré ne compilait pas.</summary>
    private sealed class FailingToolchain : ICompilerToolchain
    {
        public string Name => "faux compilateur";
        public string Target => "x86_64-windows-gnu";
        public ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<RuntimeComponent> components, string? icon, IReadOnlyList<Rgl.Native.CLibrary>? natives = null) =>
            new(false, "main.cpp:1:1: error: sortie brute de Clang\n");

        public ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<Rgl.Native.CLibrary>? natives = null) =>
            new(false, "main.cpp:1:1: error: sortie brute de Clang\n");

        public ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false) =>
            new(false, "main.cpp:1:1: error: sortie brute de Clang\n");

        public ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) =>
            new(false, "main.cpp:1:1: error: sortie brute de Clang\n");

        public ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) =>
            new(false, "main.cpp:1:1: error: sortie brute de Clang\n");

        public ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories) =>
            new(false, "main.cpp:1:1: error: sortie brute de Clang");
    }

    [Fact]
    public void Une_erreur_d_usage_renvoie_2()
    {
        using var dir = new TempDirectory();
        var (exitCode, _, error) = Run(dir.Path, dir.Path, "compile");
        Assert.Equal(CliRunner.UsageError, exitCode);
        Assert.Contains("rgl --help", error);
    }

    [Fact]
    public void Sans_Zig_compile_explique_comment_l_installer()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();

        var init = Run(project.Path, cache.Path, "init");
        Assert.Equal(CliRunner.Success, init.ExitCode);
        Assert.Contains("src/first.forge", init.Out);

        var compile = Run(project.Path, cache.Path, "compile", "-d", ".");
        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Equal("""
            erreur: chaîne de compilation introuvable : Zig n'est pas installé
             = aide : lance : rgl setup
             = aide : « --emit-cpp » produit le C++ sans avoir besoin de Zig

            """.ReplaceLineEndings("\n"), compile.Error);
        Assert.Single(Directory.GetDirectories(Path.Combine(cache.Path, "projets")));
    }

    [Fact]
    public void Init_web_affiche_les_fichiers_du_squelette()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();

        var init = Run(project.Path, cache.Path, "init", "--web");

        Assert.Equal(CliRunner.Success, init.ExitCode);
        Assert.Contains("main.rgl", init.Out);
        Assert.Contains("src/Home.paper", init.Out);
        Assert.Contains("static/site.css", init.Out);
        Assert.True(File.Exists(Path.Combine(project.Path, "src", "Home.paper")));
    }

    [Fact]
    public void Un_echec_de_Clang_est_un_bug_du_transpileur_et_sa_sortie_reste_dans_le_journal()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        Run(project.Path, cache.Path, "init");

        var compile = RunWith(new FailingToolchain(), project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Contains("erreur interne du compilateur : le C++ généré ne compile pas avec faux compilateur", compile.Error);
        Assert.Contains("c'est un bug du transpileur, pas une erreur dans ton programme", compile.Error);
        Assert.DoesNotContain("sortie brute de Clang", compile.Error);

        var log = Assert.Single(Directory.GetFiles(cache.Path, "build.log", SearchOption.AllDirectories));
        Assert.Contains("sortie brute de Clang", File.ReadAllText(log));
    }

    [ZigFact]
    public void La_demo_V0_compile_s_execute_et_renvoie_0()
    {
        using var project = new TempDirectory();
        var toolchain = ZigToolchain.Locate(SharedCache);
        Assert.NotNull(toolchain);
        Run(project.Path, SharedCache, "init");

        var exec = RunWith(toolchain, project.Path, SharedCache, "exec", "-d", ".");

        Assert.Equal("", exec.Error);
        Assert.Equal(0, exec.ExitCode);
    }

    [ZigFact]
    public void Exec_propage_le_code_de_sortie_du_train()
    {
        using var project = new TempDirectory();
        var file = Path.Combine(project.Path, "main.rgl");
        File.WriteAllText(file, "guard refuser([string] a): void {\n    fail \"aucun argument utile\";\n}\ntrain([string] args) args\n    |> refuser !> [$ 42]\n    |> done;\n");

        var exec = RunWith(ZigToolchain.Locate(SharedCache), project.Path, SharedCache, "exec", file, "--", "un", "argument");

        Assert.Equal("", exec.Error);
        Assert.Equal(42, exec.ExitCode);
    }

    [ZigFact]
    public void Un_guard_et_la_console_marchent_en_debug_et_en_release()
    {
        using var project = new TempDirectory();
        var file = Path.Combine(project.Path, "main.rgl");
        File.WriteAllText(file, """
            guard diviser(int n): int => 100 / n;

            emit signaler(Exception e): uint {
                `{e}\n` -> console;
                return 3;
            }

            train 0 |> diviser !> [signaler] |> done;
            """);
        var toolchain = ZigToolchain.Locate(SharedCache);

        var debug = RunWith(toolchain, project.Path, SharedCache, "exec", file);
        var release = RunWith(toolchain, project.Path, SharedCache, "exec", file, "--release");

        Assert.Equal(("", ""), (debug.Error, release.Error));
        Assert.Equal((3, 3), (debug.ExitCode, release.ExitCode));
    }

    [ZigFact]
    public void Le_build_de_release_donne_le_meme_resultat_avec_un_binaire_plus_leger()
    {
        using var project = new TempDirectory();
        var file = Path.Combine(project.Path, "main.rgl");
        File.WriteAllText(file, "guard g(): void {\n    fail \"oups\";\n}\ntrain |> g !> [$ 42] |> done;\n");
        var toolchain = ZigToolchain.Locate(SharedCache);

        var debug = RunWith(toolchain, project.Path, SharedCache, "exec", file);
        var release = RunWith(toolchain, project.Path, SharedCache, "exec", file, "--release");

        Assert.Equal((42, 42), (debug.ExitCode, release.ExitCode));

        var name = OperatingSystem.IsWindows() ? "main.exe" : "main";
        var debugSize = new FileInfo(Path.Combine(project.Path, "out", "debug", name)).Length;
        var releaseSize = new FileInfo(Path.Combine(project.Path, "out", "release", name)).Length;
        Assert.True(releaseSize < debugSize / 2, $"release : {releaseSize} octets, debug : {debugSize} octets");
    }

    [ZigFact]
    public void Compile_avec_emit_asm_ecrit_l_assembleur_optimise_dans_le_cache()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), "gear calcul(): uint {\n    uint code: 40;\n    code <- code + 2;\n    return code;\n}\n!train |> $ calcul();\n");

        var compile = RunWith(ZigToolchain.Locate(SharedCache), project.Path, cache.Path, "compile", "main.rgl", "--emit-asm", "--release");

        Assert.Equal("", compile.Error);
        Assert.Equal(CliRunner.Success, compile.ExitCode);
        var assembly = Assert.Single(Directory.GetFiles(cache.Path, "main.s", SearchOption.AllDirectories));
        Assert.Contains(Path.Combine("asm", "release"), assembly);
        Assert.Contains("main:", File.ReadAllText(assembly));
        Assert.Contains(assembly, compile.Out);
        Assert.False(Directory.Exists(Path.Combine(project.Path, "out")));   // rien n'est lié
    }

    [ZigFact]
    public void L_executable_porte_l_icone_du_manifeste_ou_celle_de_rgl_par_defaut()
    {
        if (!OperatingSystem.IsWindows()) return;   // seul un exécutable Windows porte une icône

        using var project = new TempDirectory();
        var toolchain = ZigToolchain.Locate(SharedCache);
        Run(project.Path, SharedCache, "init");
        var executable = Path.Combine(project.Path, "out", "debug", Path.GetFileName(project.Path) + ".exe");

        // Sans clé icon : l'icône par défaut de RGL.
        Assert.Equal(CliRunner.Success, RunWith(toolchain, project.Path, SharedCache, "compile", "-d", ".").ExitCode);
        Assert.True(_Contains(File.ReadAllBytes(executable), WindowsIcon.Default()[^200..]), "l'icône par défaut manque dans l'exécutable");

        // Avec une icône PNG : elle est réduite et liée à la place.
        var logo = new PngImage(4, 4, [.. Enumerable.Repeat<byte>(0x40, 4 * 4 * 4)]).Encode();
        File.WriteAllBytes(Path.Combine(project.Path, "logo.png"), logo);
        var manifest = Path.Combine(project.Path, "rgl.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"version\"", "\"icon\": \"logo.png\",\n    \"version\""));
        Assert.Equal(CliRunner.Success, RunWith(toolchain, project.Path, SharedCache, "compile", "-d", ".").ExitCode);

        var bytes = File.ReadAllBytes(executable);
        Assert.False(_Contains(bytes, WindowsIcon.Default()[^200..]));
        Assert.True(_Contains(bytes, WindowsIcon.FromImage(PngImage.Decode(logo, out _)!)[^64..]));
    }

    private static bool _Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void Compile_avec_emit_cpp_ecrit_le_cpp_dans_le_cache()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        Run(project.Path, cache.Path, "init");

        var compile = Run(project.Path, cache.Path, "compile", "-d", ".", "--emit-cpp");

        Assert.Equal(CliRunner.Success, compile.ExitCode);
        Assert.Empty(compile.Error);
        var cpp = Assert.Single(Directory.GetFiles(cache.Path, "main.cpp", SearchOption.AllDirectories));
        Assert.Contains("std::uint32_t rgl_entry_train(", File.ReadAllText(cpp));
        Assert.Contains(Path.GetDirectoryName(cpp)!, compile.Out);
    }

    [Fact]
    public void Compile_affiche_les_erreurs_avec_un_extrait_du_source()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        Run(project.Path, cache.Path, "init");
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), "train |> done;\nvar nom: \"Élodie;\n");

        var compile = Run(project.Path, cache.Path, "compile", "-d", ".");

        Assert.Equal(CliRunner.Failure, compile.ExitCode);
        Assert.Equal("""
            erreur[E021]: chaîne non fermée : « " » fermant manquant
             --> main.rgl:2:10
              |
            2 | var nom: "Élodie;
              |          ^ ouvert ici
              |
              = aide : ajoute le « " » fermant avant la fin de la ligne
              = aide : pour du texte sur plusieurs lignes, utilise un template : `…`

            """.ReplaceLineEndings("\n"), compile.Error);
    }
}
