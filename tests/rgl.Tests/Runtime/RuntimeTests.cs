namespace Rgl.Tests.Runtime;

/// <summary>
/// Tests du runtime C++ (<c>runtime/</c>) : compile des programmes de test avec le Zig de
/// <c>tools/zig</c> et les lance. Ignorés si Zig n'est pas installé.
/// </summary>
public class RuntimeTests
{
    private static string Source(params string[] parts) => Path.Combine([ZigBuild.Runtime, .. parts]);

    [ZigFact]
    public void Les_tests_du_runtime_string_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "string_tests", warningsAsErrors: true, Source("tests", "string_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_text_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "text_tests", warningsAsErrors: true, Source("tests", "text_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_symbol_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "symbol_tests", warningsAsErrors: true,
            Source("src", "symbol.cpp"), Source("tests", "symbol_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_exception_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "exception_tests", warningsAsErrors: true, Source("tests", "exception_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void La_console_lit_les_lignes_et_ecrit_en_UTF8()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "console_tests", warningsAsErrors: true,
            Source("src", "console.cpp"), Source("tests", "console_tests.cpp"));

        var (code, text) = ZigBuild.RunWithInput(executable, "un\r\ndeux\n\nété");
        Assert.Equal(0, code);
        Assert.Equal("[un]\n[deux]\n[]\n[été]\n[]\n[]\n", text);
    }

    [ZigFact]
    public void Les_fichiers_se_lisent_et_s_ecrivent_en_UTF8_strict_sans_exception()
    {
        using var output = new TempDirectory();
        var work = Path.Combine(output.Path, "travail");
        Directory.CreateDirectory(work);
        var executable = ZigBuild.Compile(output.Path, "file_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("src", "file.cpp"), Source("tests", "file_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable, work);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_json_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "json_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("src", "file.cpp"), Source("src", "json.cpp"), Source("tests", "json_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_regex_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "regex_tests", warningsAsErrors: true, Source("tests", "regex_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_xml_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "xml_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("src", "file.cpp"), Source("src", "xml.cpp"), Source("tests", "xml_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_processus_enfants_se_lancent_se_lisent_et_s_arretent_au_timeout()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "process_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("src", "process.cpp"), Source("tests", "process_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable, output.Path);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_systeme_passent()
    {
        using var output = new TempDirectory();
        var work = Path.Combine(output.Path, "travail");
        Directory.CreateDirectory(work);
        var executable = ZigBuild.Compile(output.Path, "system_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("src", "console.cpp"), Source("src", "system.cpp"), Source("tests", "system_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable, work);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_enfants_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "child_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("src", "child.cpp"), Source("tests", "child_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable, output.Path);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_http2_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "http2_tests", warningsAsErrors: true, Source("tests", "http2_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_unicode_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "unicode_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("src", "unicode.cpp"), Source("tests", "unicode_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable, Source("tests", "NormalizationTest.txt"));
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :" + System.Environment.NewLine + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_format_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "format_tests", warningsAsErrors: true, Source("tests", "format_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_array_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "array_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("tests", "array_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_function_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "function_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("tests", "function_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_pool_et_arene_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "arena_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("tests", "arena_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_soa_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "soa_tests", warningsAsErrors: true,
            Source("src", "memory.cpp"), Source("src", "string.cpp"), Source("tests", "soa_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_async_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "async_tests", warningsAsErrors: true, Source("tests", "async_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Les_tests_du_runtime_cast_passent()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "cast_tests", warningsAsErrors: true, Source("tests", "cast_tests.cpp"));

        var (code, text) = ZigBuild.Run(executable);
        Assert.True(code == 0, "Des vérifications du runtime ont échoué :\n" + text);
    }

    [ZigFact]
    public void Le_point_d_entree_transmet_les_arguments_en_UTF8_et_le_code_de_sortie()
    {
        using var output = new TempDirectory();
        var executable = ZigBuild.Compile(output.Path, "entry_tests", warningsAsErrors: true,
            Source("src", "arguments.cpp"), Source("tests", "entry_tests.cpp"));
        string[] arguments = ["simple", "avec espace", "Élodie", "ünïcødé ✓", "", "\"guillemets\"", @"C:\chemin\"];

        var (code, text) = ZigBuild.Run(executable, arguments);

        // Le faux train renvoie 40 + le nombre d'arguments, et les écrit un par ligne.
        Assert.Equal(40 + arguments.Length, code);
        // stdout est en mode texte sous Windows : « \n » y devient « \r\n ».
        Assert.Equal(arguments, text.ReplaceLineEndings("\n").Split('\n')[..^1]);
    }
}
