using System.Text;
using Rgl.Build.Toolchain;
using Rgl.Cli;
using Rgl.Emit;
using Rgl.Native;

namespace Rgl.Tests;

/// <summary><c>rgl check</c> (le bilan des liaisons des bibliothèques C) et <c>rgl cache</c> (taille, vidage, avertissement à 2 % du disque).</summary>
public class CheckAndCacheTests
{
    /// <summary>Une chaîne de compilation qui répond avec des fichiers figés : ni Zig ni Clang, pour tester le bilan seul.</summary>
    private sealed class FixtureToolchain : ICompilerToolchain
    {
        public string Name => "faux Zig";
        public string Target => "x86_64-linux-gnu.2.28";

        public ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false)
        {
            // Deux fonctions : « add » se convertit, « formate » (variadique) non ; une union que le bilan signale aussi.
            var file = header.Replace("\\", "/");
            var json = $$$"""
                {"id":"0x1","kind":"TranslationUnitDecl","inner":[
                  {"id":"0x2","kind":"FunctionDecl","loc":{"file":"{{{file}}}","line":1},"name":"add","type":{"qualType":"int (int, int)"},
                   "inner":[{"kind":"ParmVarDecl","name":"a","type":{"qualType":"int"}},{"kind":"ParmVarDecl","name":"b","type":{"qualType":"int"}}]},
                  {"id":"0x3","kind":"FunctionDecl","loc":{"line":2},"name":"formate","type":{"qualType":"int (const char *, ...)"},
                   "inner":[{"kind":"ParmVarDecl","name":"f","type":{"qualType":"const char *"}}]},
                  {"id":"0x4","kind":"RecordDecl","loc":{"line":3},"name":"Mixte","tagUsed":"union","completeDefinition":true,
                   "inner":[{"kind":"FieldDecl","name":"i","type":{"qualType":"int"}}]}
                ]}
                """;
            read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            return new ToolchainResult(true, "");
        }

        public ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read)
        {
            read(new MemoryStream());
            return new ToolchainResult(true, "");
        }

        public ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read)
        {
            read(new MemoryStream(Encoding.UTF8.GetBytes($"lib.o: {header.Replace("\\", "/")}\n")));
            return new ToolchainResult(true, "");
        }

        public ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories) =>
            new(false, "pas de compilation ici");

        public ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<RuntimeComponent> components, string? icon, IReadOnlyList<CLibrary>? natives = null) =>
            new(false, "pas de compilation ici");

        public ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null) =>
            new(false, "pas de compilation ici");
    }

    private static (int ExitCode, string Out, string Error) Run(ICompilerToolchain? toolchain, string directory, string cache, long? diskSize, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliRunner.Run(args, new CliContext(output, error, directory, cache, Toolchain: toolchain, DiskSize: diskSize));
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Un projet avec un fichier <c>forge: native</c> et un programme qui contient <paramref name="body"/>.</summary>
    private static TempDirectory Project(string main)
    {
        var project = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(project.Path, "lib"));
        File.WriteAllText(Path.Combine(project.Path, "lib", "lib.h"), "int add(int a, int b);\n");
        File.WriteAllText(Path.Combine(project.Path, "Lib.forge"), "forge: native\ndescription: \"lib\"\nheader: \"lib/lib.h\"\nlink: static\n");
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), main);
        return project;
    }

    #region rgl check

    [Fact]
    public void Un_symbole_utilise_et_non_converti_est_E019_mais_ne_fait_pas_echouer_par_defaut()
    {
        using var project = Project("import Lib;\n!train |> $ Lib.formate(\"x\");\n");
        using var cache = new TempDirectory();

        var check = Run(new FixtureToolchain(), project.Path, cache.Path, null, "check", "main.rgl");

        Assert.True(check.ExitCode == CliRunner.Success, check.Error);
        Assert.Contains("E019", check.Error);
        Assert.Contains("« formate » n'est pas converti : fonction à nombre variable d'arguments", check.Error);
        Assert.Contains("main.rgl:2", check.Error);
        Assert.Contains("1 fonction(s)", check.Out);
        Assert.Contains("1 symbole(s) utilisé(s) sans liaison (E019)", check.Out);
    }

    [Fact]
    public void Avec_strict_un_symbole_utilise_et_non_converti_fait_echouer()
    {
        using var project = Project("import Lib;\n!train |> $ Lib.formate(\"x\");\n");
        using var cache = new TempDirectory();

        var check = Run(new FixtureToolchain(), project.Path, cache.Path, null, "check", "main.rgl", "--strict");

        Assert.Equal(CliRunner.Failure, check.ExitCode);
    }

    [Fact]
    public void Un_symbole_non_converti_que_rien_n_utilise_n_est_dit_qu_avec_all()
    {
        using var project = Project("import Lib;\n!train |> $ Lib.add(1, 2);\n");
        using var cache = new TempDirectory();

        var plain = Run(new FixtureToolchain(), project.Path, cache.Path, null, "check", "main.rgl", "--strict");
        var all = Run(new FixtureToolchain(), project.Path, cache.Path, null, "check", "main.rgl", "--all");

        Assert.Equal(CliRunner.Success, plain.ExitCode);
        Assert.DoesNotContain("E019", plain.Error);
        Assert.Contains("Aucun symbole utilisé n'est resté sans liaison", plain.Out);
        Assert.Contains("2 symbole(s) non converti(s) et inutilisé(s)", plain.Out);
        Assert.Contains("« formate » (Lib.forge) n'est pas converti, mais rien ne l'utilise", all.Error);
        Assert.Contains("« Mixte » (Lib.forge) n'est pas converti, mais rien ne l'utilise : union", all.Error);
    }

    [Fact]
    public void Un_nom_rendu_visible_par_only_est_utilise_sans_etre_qualifie()
    {
        using var project = Project("import Lib only {formate};\n!train |> $ formate(\"x\");\n");
        using var cache = new TempDirectory();

        var check = Run(new FixtureToolchain(), project.Path, cache.Path, null, "check", "main.rgl");

        Assert.Contains("E019", check.Error);
        Assert.Contains("main.rgl:2", check.Error);
    }

    [Fact]
    public void Un_projet_sans_bibliotheque_C_n_a_rien_a_verifier()
    {
        using var project = new TempDirectory();
        using var cache = new TempDirectory();
        File.WriteAllText(Path.Combine(project.Path, "main.rgl"), "!train |> $ 0;\n");

        var check = Run(null, project.Path, cache.Path, null, "check", "main.rgl");

        Assert.Equal(CliRunner.Success, check.ExitCode);
        Assert.Contains("Aucune bibliothèque C", check.Out);
    }

    [Fact]
    public void Le_bilan_vient_du_cache_au_deuxieme_passage()
    {
        using var project = Project("import Lib;\n!train |> $ Lib.add(1, 2);\n");
        using var cache = new TempDirectory();

        var first = Run(new FixtureToolchain(), project.Path, cache.Path, null, "check", "main.rgl");
        var second = Run(new FixtureToolchain(), project.Path, cache.Path, null, "check", "main.rgl");

        Assert.Equal(first.Out, second.Out);
        Assert.Contains("2 déclaration(s)", second.Out);
    }

    [Theory]
    [InlineData("check", "--inconnue")]
    [InlineData("check", "a.rgl", "b.rgl")]
    [InlineData("cache")]
    [InlineData("cache", "autre")]
    [InlineData("cache", "--size", "clear")]
    public void Une_ligne_de_commande_invalide_est_une_erreur_d_usage(params string[] args)
    {
        using var dir = new TempDirectory();

        Assert.Equal(CliRunner.UsageError, Run(null, dir.Path, dir.Path, null, args).ExitCode);
    }

    #endregion

    #region rgl cache

    [Theory]
    [InlineData(0L, "0 o")]
    [InlineData(512L, "512 o")]
    [InlineData(1024L, "1 Ko")]
    [InlineData(1536L, "1,5 Ko")]
    [InlineData(5L * 1024 * 1024, "5 Mo")]
    [InlineData(1_800_000L, "1,7 Mo")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 Go")]
    [InlineData(1024L * 1024 * 1024 * 1024 * 2, "2048 Go")]
    public void La_taille_s_ecrit_avec_l_unite_la_plus_lisible(long bytes, string expected) =>
        Assert.Equal(expected, CacheCommand.Format(bytes));

    private static void Fill(string cache, int kilobytes)
    {
        Directory.CreateDirectory(Path.Combine(cache, "projets", "x"));
        File.WriteAllBytes(Path.Combine(cache, "projets", "x", "a.bin"), new byte[kilobytes * 1024 / 2]);
        File.WriteAllBytes(Path.Combine(cache, "b.bin"), new byte[kilobytes * 1024 / 2]);
    }

    [Fact]
    public void Cache_size_donne_la_taille_de_tous_les_fichiers_du_cache()
    {
        using var cache = new TempDirectory();
        Fill(cache.Path, 2048);

        var size = Run(null, cache.Path, cache.Path, long.MaxValue / 4, "cache", "--size");

        Assert.Equal(CliRunner.Success, size.ExitCode);
        Assert.Contains("Cache : 2 Mo", size.Out);
        Assert.Contains(cache.Path, size.Out);
    }

    [Fact]
    public void Cache_size_d_un_cache_absent_est_zero()
    {
        using var parent = new TempDirectory();

        var size = Run(null, parent.Path, Path.Combine(parent.Path, "absent"), long.MaxValue / 4, "cache", "--size");

        Assert.Equal(CliRunner.Success, size.ExitCode);
        Assert.Contains("Cache : 0 o", size.Out);
    }

    [Fact]
    public void Cache_clear_vide_le_cache_et_garde_son_dossier()
    {
        using var cache = new TempDirectory();
        Fill(cache.Path, 1024);

        var clear = Run(null, cache.Path, cache.Path, null, "cache", "clear");

        Assert.Equal(CliRunner.Success, clear.ExitCode);
        Assert.Contains("Cache vidé : 1 Mo libérés", clear.Out);
        Assert.True(Directory.Exists(cache.Path));
        Assert.Empty(Directory.GetFileSystemEntries(cache.Path));
        Assert.Equal(0, CacheCommand.Size(cache.Path));
    }

    [Fact]
    public void Cache_clear_d_un_cache_absent_ne_fait_rien()
    {
        using var parent = new TempDirectory();

        var clear = Run(null, parent.Path, Path.Combine(parent.Path, "absent"), null, "cache", "clear");

        Assert.Equal(CliRunner.Success, clear.ExitCode);
        Assert.Contains("déjà vide", clear.Out);
    }

    [Fact]
    public void Un_cache_a_2_pour_cent_du_disque_est_signale()
    {
        using var cache = new TempDirectory();
        Fill(cache.Path, 1024);   // 1 Mo

        // Un disque de 40 Mo : le cache en prend 2,5 %. Un disque de 100 Mo : 1 %.
        var warned = Run(null, cache.Path, cache.Path, 40L * 1024 * 1024, "cache", "--size");
        var quiet = Run(null, cache.Path, cache.Path, 100L * 1024 * 1024, "cache", "--size");

        Assert.Equal(CliRunner.Success, warned.ExitCode);
        Assert.Contains("W067", warned.Error);
        Assert.Contains("le cache de RGL prend 1 Mo, soit 2,5 % du disque", warned.Error);
        Assert.DoesNotContain("W067", quiet.Error);
    }

    [Fact]
    public void Un_build_signale_aussi_un_cache_trop_gros()
    {
        using var project = Project("!train |> $ 0;\n");
        using var cache = new TempDirectory();
        Fill(cache.Path, 1024);

        var compile = Run(null, project.Path, cache.Path, 20L * 1024 * 1024, "compile", "main.rgl");

        Assert.Contains("W067", compile.Error);
    }

    #endregion
}
