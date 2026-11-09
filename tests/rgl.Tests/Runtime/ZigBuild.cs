using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Rgl.Tests.Runtime;

/// <summary>
/// Compile et lance des programmes C++ avec le runtime et le Zig de <c>tools/zig</c>, pour les tests
/// qui vérifient du C++ pour de vrai (runtime, code généré).
/// </summary>
internal static class ZigBuild
{
    /// <summary>Racine du dépôt, retrouvée à partir de l'emplacement de ce fichier source.</summary>
    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    /// <summary>Chemin de l'exécutable Zig installé par <c>tools/fetch-tools.cs</c>.</summary>
    public static string Zig => Path.Combine(Root(), "tools", "zig", OperatingSystem.IsWindows() ? "zig.exe" : "zig");

    /// <summary>Dossier du runtime C++.</summary>
    public static string Runtime => Path.Combine(Root(), "runtime");

    /// <summary>
    /// Compile le runtime (<c>memory.cpp</c>, <c>string.cpp</c>) avec <paramref name="sources"/>
    /// (chemins complets) en un exécutable placé dans <paramref name="directory"/>.
    /// Les fichiers de <c>runtime/src</c> sont compilés une seule fois en objets, rangés dans un dossier partagé par tous les tests et
    /// reconnus par l'empreinte de leur source, de tous les en-têtes du runtime, de Zig et des options : un test ne recompile plus que son propre fichier.
    /// </summary>
    /// <param name="warningsAsErrors">Vrai pour traiter les avertissements comme des erreurs.</param>
    public static string Compile(string directory, string name, bool warningsAsErrors, params string[] sources) =>
        CompileWith(directory, name, warningsAsErrors, [], sources);

    /// <summary>Comme <see cref="Compile"/>, avec des options en plus (un <c>-D…</c>), pour le runtime comme pour les sources.</summary>
    public static string CompileWith(string directory, string name, bool warningsAsErrors, string[] extraOptions, params string[] sources)
    {
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        var runtimeSources = Path.Combine(Runtime, "src") + Path.DirectorySeparatorChar;
        var all = new[] { Path.Combine(Runtime, "src", "memory.cpp"), Path.Combine(Runtime, "src", "string.cpp") }.Concat(sources).Distinct().ToArray();

        List<string> arguments = ["c++", .. _Options(warningsAsErrors, extraOptions)];
        foreach (var source in all)
            arguments.Add(source.StartsWith(runtimeSources, StringComparison.OrdinalIgnoreCase) ? _RuntimeObject(source, warningsAsErrors, extraOptions) : source);
        arguments.Add("-o");
        arguments.Add(executable);
        if (OperatingSystem.IsWindows()) arguments.Add("-lshell32");

        var (code, text) = _RunZig(arguments);
        Assert.True(code == 0, "La compilation a échoué :" + Environment.NewLine + text);
        return executable;
    }

    private static string[] _Options(bool warningsAsErrors, string[] extraOptions)
    {
        List<string> options =
        [
            "-std=c++23", "-fno-exceptions", "-fno-rtti", "-Wall", "-Wextra", "-Wpedantic",
            "-I" + Path.Combine(Runtime, "include"),
            "-isystem", Path.Combine(Runtime, "third_party", "ctre"),
        ];
        options.AddRange(extraOptions);
        if (warningsAsErrors) options.Add("-Werror");
        return [.. options];
    }

    // Comme dans ZigToolchain : sous Windows, le cache de Zig échoue parfois (« checking cache failed ») sans que le C++ y soit pour rien ;
    // on relance alors une fois.
    private static (int Code, string Output) _RunZig(List<string> arguments)
    {
        var (code, text) = Run(Zig, [.. arguments]);
        if (code != 0 && text.Contains("checking cache failed", StringComparison.Ordinal))
            (code, text) = Run(Zig, [.. arguments]);
        return (code, text);
    }

    private static readonly Lazy<string> _headersFingerprint = new(() =>
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Runtime, "include"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sha.TransformBlock(Encoding.UTF8.GetBytes(file), 0, Encoding.UTF8.GetByteCount(file), null, 0);
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    });

    /// <summary>L'objet d'un fichier de <c>runtime/src</c>, compilé une fois pour tous les tests qui ont les mêmes options.</summary>
    private static string _RuntimeObject(string source, bool warningsAsErrors, string[] extraOptions)
    {
        var options = _Options(warningsAsErrors, extraOptions);
        var zig = new FileInfo(Zig);
        var key = string.Join('|', source, File.ReadAllText(source), _headersFingerprint.Value, string.Join(' ', options), zig.Length, zig.LastWriteTimeUtc.Ticks);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        var folder = Path.Combine(Path.GetTempPath(), "rgl-tests-runtime");
        Directory.CreateDirectory(folder);
        var objectFile = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + "-" + hash + ".o");
        if (File.Exists(objectFile)) return objectFile;

        // Écrit sous un nom temporaire puis renommé : deux tests qui compilent le même fichier en même temps ne se gênent pas.
        var temporary = objectFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        List<string> arguments = ["c++", .. options, "-c", source, "-o", temporary];
        var (code, text) = _RunZig(arguments);
        Assert.True(code == 0, "La compilation de " + Path.GetFileName(source) + " a échoué :" + Environment.NewLine + text);
        try { File.Move(temporary, objectFile); }
        catch (IOException) { File.Delete(temporary); }
        return objectFile;
    }

    /// <summary>
    /// Écrit dans <paramref name="directory"/> les unités C++ qu'un programme émis a demandées (les blocs <c>cpp { … }</c> de la std, par exemple) et rend
    /// leurs chemins : à passer à <see cref="Compile"/> avec le <c>main.cpp</c>, sans quoi l'édition de liens ne trouve pas leurs fonctions.
    /// </summary>
    internal static string[] WriteUnits(Rgl.Emit.CppProgram program, string directory)
    {
        var paths = new List<string>();
        foreach (var unit in program.NativeUnits ?? [])
        {
            var path = Path.Combine(directory, unit.Name + ".rgl.gen.cpp");
            File.WriteAllText(path, unit.Code);
            paths.Add(path);
        }
        return [.. paths];
    }

    /// <summary>
    /// Compile un programme émis avec <paramref name="sources"/> du runtime et, s'il demande des unités C++ (blocs <c>cpp { … }</c>), ces unités et le
    /// composant <c>native.cpp</c> qui les appelle, le tout avec les exceptions (comme le fait <c>ZigToolchain</c> pour ces fichiers).
    /// </summary>
    internal static string CompileProgram(string directory, string name, Rgl.Emit.CppProgram program, string mainCpp, params string[] sources)
    {
        var units = WriteUnits(program, directory);
        if (units.Length == 0) return Compile(directory, name, warningsAsErrors: true, [.. sources, mainCpp]);
        return CompileWith(directory, name, warningsAsErrors: true, ["-fexceptions"], [.. sources, mainCpp, .. units, Path.Combine(Runtime, "src", "native.cpp")]);
    }

    /// <summary>Lance un programme et renvoie son code de sortie et ses deux sorties réunies, lues en UTF-8.</summary>
    public static (int Code, string Output) Run(string program, params string[] arguments) => RunWithInput(program, null, arguments);

    /// <summary>
    /// Lance un programme avec <paramref name="input"/> sur son entrée standard (en UTF-8, puis fermée),
    /// et renvoie son code de sortie et ses deux sorties réunies.
    /// </summary>
    public static (int Code, string Output) RunWithInput(string program, string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (input is not null) start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}

/// <summary>Test qui a besoin de Zig : ignoré, avec la raison, si <c>tools/zig</c> n'est pas installé.</summary>
public sealed class ZigFactAttribute : FactAttribute
{
    public ZigFactAttribute()
    {
        if (!File.Exists(ZigBuild.Zig)) Skip = "Zig n'est pas installé : lancer « dotnet run fetch-tools.cs » dans tools/.";
    }
}

/// <summary>Théorie qui a besoin de Zig : ignorée, avec la raison, si <c>tools/zig</c> n'est pas installé.</summary>
public sealed class ZigTheoryAttribute : TheoryAttribute
{
    public ZigTheoryAttribute()
    {
        if (!File.Exists(ZigBuild.Zig)) Skip = "Zig n'est pas installé : lancer « dotnet run fetch-tools.cs » dans tools/.";
    }
}
