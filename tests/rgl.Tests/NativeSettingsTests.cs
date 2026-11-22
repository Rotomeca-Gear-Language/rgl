using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Diagnostics;
using Rgl.Native;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>
/// La compilation et la liaison de code C ou C++ qui n'est pas écrit pour RGL : les clés <c>include</c>, <c>define</c>, <c>sources</c>, <c>libraries</c>, <c>libdirs</c> et <c>system</c>,
/// dans <c>rgl.json</c> (<c>native</c>) ou l'en-tête d'un fichier forge, et les bibliothèques dynamiques copiées à côté de l'exécutable.
/// </summary>
public class NativeSettingsTests
{
    private static readonly string SharedCache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Raw(params (string Key, string[] Values)[] entries) =>
        entries.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Values);

    [Fact]
    public void Les_elements_d_une_autre_plateforme_sont_ecartes_et_les_prefixes_retires()
    {
        using var dir = new TempDirectory();
        var platform = NativeSettings.Platform;
        var other = platform == "windows" ? "linux" : "windows";

        var settings = NativeSettings.Resolve(Raw(("system", [$"{platform}:m", $"{other}:ws2_32", "pthread"])), dir.Path, null, false, (message, _) => Assert.Fail(message));

        Assert.NotNull(settings);
        Assert.Equal(["m", "pthread"], settings!.SystemLibraries);
    }

    [Fact]
    public void Les_motifs_de_sources_sont_developpes_et_ne_gardent_que_le_C_et_le_C_plus_plus()
    {
        using var dir = new TempDirectory();
        dir.File("vendor/a.c");
        dir.File("vendor/sub/b.cpp");
        dir.File("vendor/sub/deep/c.cc");
        dir.File("vendor/notes.txt");
        dir.File("vendor/.cache/d.c");

        var settings = NativeSettings.Resolve(Raw(("sources", ["vendor/**/*.c*"])), dir.Path, null, false, (message, _) => Assert.Fail(message));

        Assert.Equal(["a.c", "b.cpp", "c.cc"], settings!.Sources.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Un_chemin_sans_motif_qui_n_existe_pas_est_un_probleme()
    {
        using var dir = new TempDirectory();
        var problems = new List<string>();

        var settings = NativeSettings.Resolve(Raw(("sources", ["vendor/*.c"]), ("libraries", ["lib/x.a"]), ("libdirs", ["nulle-part"]), ("define", ["1mauvais"]), ("system", ["-lm"])),
            dir.Path, null, false, (message, key) => problems.Add(key + ": " + message));

        Assert.Null(settings);
        Assert.Equal(["define", "libdirs", "libraries", "sources", "system"], problems.Select(p => p.Split(':')[0]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Un_paquet_ne_sort_pas_de_son_dossier()
    {
        using var dir = new TempDirectory();
        dir.File("pkg/a.c");
        dir.File("outside.c");
        var problems = new List<string>();

        var settings = NativeSettings.Resolve(Raw(("include", ["../secret"]), ("sources", ["../outside.c"])), Path.Combine(dir.Path, "pkg"), Path.Combine(dir.Path, "pkg"), false, (message, key) => problems.Add(key));

        Assert.Null(settings);
        Assert.Contains("include", problems);
    }

    [Fact]
    public void Une_bibliotheque_dynamique_est_reconnue_par_son_extension_ou_par_link_dynamic()
    {
        using var dir = new TempDirectory();
        dir.File("lib/a.dll");
        dir.File("lib/libb.so.1.2");
        dir.File("lib/c.a");

        var settings = NativeSettings.Resolve(Raw(("libraries", ["lib/a.dll", "lib/libb.so.1.2", "lib/c.a"])), dir.Path, null, false, (message, _) => Assert.Fail(message));
        Assert.Equal([true, true, false], settings!.Libraries.Select(l => l.IsDynamic));

        var forced = NativeSettings.Resolve(Raw(("libraries", ["lib/c.a"])), dir.Path, null, true, (message, _) => Assert.Fail(message));
        Assert.True(forced!.Libraries[0].IsDynamic);
    }

    [Fact]
    public void Un_reglage_invalide_est_E165()
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", """{"name":"p","version":"1","rgl":"0","native":{"bogus":"x"}}""");
        var diagnostics = new DiagnosticBag();
        Assert.Null(ProjectManifest.Load(Path.Combine(dir.Path, "rgl.json"), diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.InvalidNativeSettings && d.Message.Contains("bogus"));

        dir.File("rgl.json", """{"name":"p","version":"1","rgl":"0","native":{"define":["1x"]}}""");
        diagnostics = new DiagnosticBag();
        Assert.Null(ProjectManifest.Load(Path.Combine(dir.Path, "rgl.json"), diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.InvalidNativeSettings && d.Message.Contains("define"));

        dir.File("Cpp.forge", "forge: descriptor\ndescription: \"x\"\nheader: \"c.hpp\"\nlink: static\nsystem: \"-lm\"\n\ndescriptor demo::C -> C { constructor() => demo::C(); }\n");
        dir.File("c.hpp", "namespace demo { struct C {}; }\n");
        dir.File("main.rgl", "import Cpp;\n!train |> $ 0;\n");
        diagnostics = new DiagnosticBag();
        SourceGraph.Load(new SourceText(Path.Combine(dir.Path, "main.rgl"), File.ReadAllText(Path.Combine(dir.Path, "main.rgl"))), dir.Path, diagnostics);
        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.InvalidNativeSettings);
    }

    [Fact]
    public void Le_manifeste_lit_la_section_native()
    {
        using var dir = new TempDirectory();
        dir.File("vendor/include/x.h");
        dir.File("vendor/src/x.c");
        dir.File("rgl.json", """{"name":"p","version":"1","rgl":"0","native":{"include":"vendor/include","define":["A","B=2"],"sources":["vendor/src/*.c"],"system":["m"]}}""");

        var diagnostics = new DiagnosticBag();
        var manifest = ProjectManifest.Load(Path.Combine(dir.Path, "rgl.json"), diagnostics);

        Assert.NotNull(manifest);
        Assert.Equal(["A", "B=2"], manifest!.Native.Defines);
        Assert.Equal(["x.c"], manifest.Native.Sources.Select(Path.GetFileName));
        Assert.Equal(["m"], manifest.Native.SystemLibraries);
        Assert.Contains("-DB=2", manifest.Native.PreprocessorOptions);
    }

    [Fact]
    public void Une_liaison_dynamique_d_un_descriptor_avertit_W071()
    {
        using var dir = new TempDirectory();
        dir.File("c.hpp", "namespace demo { struct C {}; }\n");
        dir.File("Cpp.forge", "forge: descriptor\ndescription: \"x\"\nheader: \"c.hpp\"\nlink: dynamic\n\ndescriptor demo::C -> C { constructor() => demo::C(); }\n");
        dir.File("main.rgl", "import Cpp;\n!train |> $ 0;\n");

        var diagnostics = new DiagnosticBag();
        SourceGraph.Load(new SourceText(Path.Combine(dir.Path, "main.rgl"), File.ReadAllText(Path.Combine(dir.Path, "main.rgl"))), dir.Path, diagnostics);

        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCodes.DynamicCppLink);
        Assert.DoesNotContain(diagnostics.Items, d => d.Level == DiagnosticLevel.Error);
    }

    private static (int Code, string Output, string Error) Compile(TempDirectory dir, string name = "p")
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliRunner.Run(["compile", "main.rgl"], new CliContext(output, error, dir.Path, SharedCache, Toolchain: ZigToolchain.Locate(SharedCache)));
        if (code != 0) return (code, "", error.ToString());
        var run = ZigBuild.Run(Path.Combine(dir.Path, "out", "debug", OperatingSystem.IsWindows() ? name + ".exe" : name));
        return (run.Code, run.Output.Replace("\r\n", "\n"), error.ToString());
    }

    [ZigFact]
    public void Le_manifeste_donne_inclusions_definitions_et_sources_au_C_integre()
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", """{"name":"p","version":"1","rgl":"0","native":{"include":"vendor","define":["SCALE=7"],"sources":"vendor/*.c"}}""");
        dir.File("vendor/scale.h", "#pragma once\n#ifdef __cplusplus\nextern \"C\" {\n#endif\nint scale(int n);\n#ifdef __cplusplus\n}\n#endif\n");
        dir.File("vendor/scale.c", "#include \"scale.h\"\nint scale(int n) { return n * SCALE; }\n");
        dir.File("main.rgl", """
            unsafe f(): int => cpp {
                #include "scale.h"
                int rgl() { return scale(6) + SCALE; }
            }(): int;
            gear run(): int { f() -> console; return 0; }
            !train |> run |> $ 0;
            """);

        var result = Compile(dir);

        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("49", result.Output);
    }

    [ZigFact]
    public void Un_fichier_native_donne_ses_definitions_a_la_lecture_de_l_en_tete_et_compile_ses_sources()
    {
        using var dir = new TempDirectory();
        dir.File("rgl.json", """{"name":"p","version":"1","rgl":"0"}""");
        dir.File("lib/feat.h", "#pragma once\n#ifdef FEATURE\nint feat(void);\n#endif\n");
        dir.File("lib/feat.c", "#include \"feat.h\"\nint feat(void) { return FEATURE + 3; }\n");
        dir.File("Feat.forge", "forge: native\ndescription: \"x\"\nheader: \"lib/feat.h\"\nlink: static\ndefine: \"FEATURE=2\"\nsources: [\"lib/feat.c\"]\n");
        dir.File("main.rgl", "import Feat;\n!train |> $ (Feat.feat() as uint);\n");

        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliRunner.Run(["exec", "main.rgl"], new CliContext(output, error, dir.Path, SharedCache, Toolchain: ZigToolchain.Locate(SharedCache)));

        Assert.True(code == 5, error.ToString());
    }

    [ZigFact]
    public void Une_bibliotheque_dynamique_se_lie_et_se_copie_a_cote_de_l_executable()
    {
        var toolchain = ZigToolchain.Locate(SharedCache);
        using var dir = new TempDirectory();
        var libraryName = OperatingSystem.IsWindows() ? "answer.dll" : "libanswer.so";
        // Seule la fonction est exportée : une DLL MinGW sans __declspec(dllexport) exporte tout, jusqu'à atexit, que l'exécutable définit aussi.
        dir.File("vendor/answer.c", "#ifdef _WIN32\n#define EXPORT __declspec(dllexport)\n#else\n#define EXPORT __attribute__((visibility(\"default\")))\n#endif\nEXPORT int answer(void) { return 42; }\n");
        dir.File("vendor/answer.h", "#pragma once\n#ifdef __cplusplus\nextern \"C\" {\n#endif\nint answer(void);\n#ifdef __cplusplus\n}\n#endif\n");
        var built = ZigBuild.Run(ZigBuild.Zig, "cc", "-shared", "-target", toolchain!.Target, Path.Combine(dir.Path, "vendor", "answer.c"), "-o", Path.Combine(dir.Path, "vendor", libraryName));
        Assert.True(built.Code == 0, built.Output);

        dir.File("rgl.json", $$$"""{"name":"p","version":"1","rgl":"0","native":{"include":"vendor","libraries":"vendor/{{{libraryName}}}"}}""");
        dir.File("main.rgl", """
            unsafe f(): int => cpp {
                #include "answer.h"
                int rgl() { return answer(); }
            }(): int;
            gear run(): int { f() -> console; return 0; }
            !train |> run |> $ 0;
            """);

        var result = Compile(dir);

        Assert.True(result.Code == 0, result.Error);
        Assert.Equal("42", result.Output);
        Assert.True(File.Exists(Path.Combine(dir.Path, "out", "debug", libraryName)));
    }
}
