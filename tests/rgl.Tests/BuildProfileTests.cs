using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Emit;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Les profils de build : <c>--debug</c>, <c>--release</c>, <c>--release=size</c>, <c>--profile nom</c> et la clé <c>profiles</c> de <c>rgl.json</c> (slice B.3).</summary>
public class BuildProfileTests
{
    /// <summary>Chaîne qui note le profil de chaque build et écrit un faux exécutable.</summary>
    private sealed class RecordingToolchain : ICompilerToolchain
    {
        public List<BuildProfile> Profiles { get; } = [];
        public string Name => "faux compilateur";
        public string Target => "x86_64-windows-gnu";

        public ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<RuntimeComponent> components, string? icon, IReadOnlyList<Rgl.Native.CLibrary>? natives = null)
        {
            Profiles.Add(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "");
            return new(true, "");
        }

        public ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<Rgl.Native.CLibrary>? natives = null) => new(false, "");
        public ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false) => new(false, "");
        public ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => new(false, "");
        public ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) => new(false, "");
        public ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories) => new(false, "");
    }

    private static (int Exit, string Out, string Error, RecordingToolchain Toolchain, string Project) Compile(string manifest, params string[] options)
    {
        var project = new TempDirectory();
        var cache = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "rgl.json"), manifest);
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), "train |> done;\n");
        var toolchain = new RecordingToolchain();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliRunner.Run(["compile", "-d", ".", .. options], new CliContext(output, error, project.Path, cache.Path, Toolchain: toolchain));
        return (exit, output.ToString(), error.ToString(), toolchain, project.Path);
    }

    private const string Plain = "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\"}";

    [Theory]
    [InlineData("", "debug")]
    [InlineData("--debug", "debug")]
    [InlineData("--release", "release")]
    [InlineData("--release=size", "size")]
    public void Chaque_option_choisit_son_profil_et_son_dossier_de_sortie(string option, string profile)
    {
        var (exit, output, _, toolchain, _) = Compile(Plain, option.Length == 0 ? [] : [option]);

        Assert.Equal(CliRunner.Success, exit);
        Assert.Equal(profile, Assert.Single(toolchain.Profiles).Name);
        Assert.Contains($"out{Path.DirectorySeparatorChar}{profile}", output);
    }

    [Fact]
    public void Un_profil_du_manifeste_se_demande_par_son_nom_avec_ses_options()
    {
        var (exit, _, _, toolchain, _) = Compile(
            "{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\",\"profiles\":{\"rapide\":{\"extends\":\"size\",\"flags\":[\"-O3\"],\"link\":[\"-static\"],\"overflow\":\"unchecked\"}}}",
            "--profile", "rapide");

        Assert.Equal(CliRunner.Success, exit);
        var profile = Assert.Single(toolchain.Profiles);
        Assert.Equal(("rapide", ProfileKind.Size), (profile.Name, profile.Kind));
        Assert.Equal(["-O3"], profile.CompileFlags);
        Assert.Equal(["-static"], profile.LinkFlags);
        Assert.Equal(Rgl.IR.OverflowMode.Unchecked, profile.Overflow);
    }

    [Fact]
    public void Un_profil_inconnu_est_une_erreur_qui_liste_les_profils()
    {
        var (exit, _, error, toolchain, _) = Compile(Plain, "--profile", "nope");

        Assert.Equal(CliRunner.Failure, exit);
        Assert.Contains("profil « nope » inconnu", error);
        Assert.Contains("debug, release, size", error);
        Assert.Empty(toolchain.Profiles);
    }

    [Fact]
    public void Deux_profils_dans_une_commande_sont_refuses()
    {
        var (exit, _, error, _, _) = Compile(Plain, "--release", "--debug");

        Assert.NotEqual(CliRunner.Success, exit);
        Assert.Contains("un seul profil par commande", error);
    }

    [Theory]
    [InlineData("{\"debug\":{}}", "profil fourni")]
    [InlineData("{\"Rapide\":{}}", "minuscules")]
    [InlineData("{\"ok\":{\"extends\":\"autre\"}}", "extends")]
    [InlineData("{\"ok\":{\"flags\":\"-O3\"}}", "flags")]
    [InlineData("{\"ok\":{\"vitesse\":1}}", "clé inconnue")]
    public void Un_profil_mal_ecrit_est_refuse_avec_la_raison(string profiles, string fragment)
    {
        var (exit, _, error, toolchain, _) = Compile($"{{\"name\":\"p\",\"version\":\"1\",\"rgl\":\"0\",\"profiles\":{profiles}}}");

        Assert.NotEqual(CliRunner.Success, exit);
        Assert.Contains(fragment, error);
        Assert.Empty(toolchain.Profiles);
    }

    [Fact]
    public void Les_profils_ne_partagent_pas_le_cache_de_leurs_objets()
    {
        var release = BuildProfile.Release;
        var custom = new BuildProfile("release", ProfileKind.Release, ["-O3"], []);

        Assert.NotEqual(release.Key, custom.Key);
        Assert.NotEqual(BuildProfile.Release.Key, BuildProfile.Size.Key);
    }

    [ZigFact]
    public void Le_profil_size_donne_un_executable_plus_petit_que_release()
    {
        using var project = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "rgl.json"), Plain);
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"),
            "gear pure fib(int n): int => n < 2 ? n : fib(n - 1) + fib(n - 2);\nemit run(): uint {\n`{fib(20)}` -> console;\nreturn 0;\n}\n!train |> run;\n");
        var cache = Path.Combine(Path.GetTempPath(), "rgl-tests-cache");
        long Build(string option)
        {
            var error = new StringWriter();
            var exit = CliRunner.Run(["compile", "-d", ".", option], new CliContext(new StringWriter(), error, project.Path, cache, Toolchain: ZigToolchain.Locate(cache)));
            Assert.True(exit == CliRunner.Success, error.ToString());
            return new FileInfo(Directory.GetFiles(Path.Combine(project.Path, "out", option == "--release" ? "release" : "size"), "*.exe").Concat(Directory.GetFiles(Path.Combine(project.Path, "out", option == "--release" ? "release" : "size"), "p")).First()).Length;
        }

        Assert.True(Build("--release=size") < Build("--release"));
    }
}
