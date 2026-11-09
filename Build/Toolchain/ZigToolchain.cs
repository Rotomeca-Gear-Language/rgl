using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Rgl.Emit;
using Rgl.Native;

namespace Rgl.Build.Toolchain;

/// <summary>
/// Chaîne de compilation <c>zig c++</c> (Clang fourni par Zig), la seule du V0. Cible la machine
/// courante : Windows MinGW, ou Linux glibc 2.28 (x86_64 ou aarch64).
/// </summary>
/// <remarks>
/// <para>
/// On ne compile que ce dont le programme a besoin. Chaque fichier du runtime (<c>runtime/src/*.cpp</c>)
/// est un composant (<see cref="RuntimeComponent"/>) compilé à part, seulement si le programme
/// l'utilise ou si un composant utilisé en dépend. Chaque objet compilé est rangé dans le cache sous
/// une empreinte de sa source, des en-têtes du runtime, de la version de Zig, de la cible et des
/// options : il n'est recompilé que si l'un d'eux change. Le programme est lié directement avec ces
/// objets, et avec rien d'autre du runtime.
/// </para>
/// <para>
/// Les caches de Zig sont placés dans le cache de RGL, pour que tout ce que produit un build se
/// trouve au même endroit.
/// </para>
/// </remarks>
internal sealed class ZigToolchain : ICompilerToolchain, IWebAssemblyToolchain
{
    /// <summary>
    /// Options de compilation, communes au runtime et au programme. RGL est sans exceptions : le C++
    /// l'est aussi dans les deux profils, pour qu'un programme se comporte de la même façon.
    /// </summary>
    private static string[] CompileFlags(BuildProfile profile, bool exceptions = false) => _WithExceptions(_CompileFlags(profile), exceptions);

    /// <summary>
    /// Les options avec les exceptions : seul le composant <see cref="RuntimeComponent.Native"/> les demande, pour attraper celles que lance le C++ d'une
    /// liaison. Le RTTI reste désactivé : un <c>catch</c> par type n'en a pas besoin.
    /// </summary>
    private static string[] _WithExceptions(string[] flags, bool exceptions) =>
        exceptions ? [.. flags.Select(f => f == "-fno-exceptions" ? "-fexceptions" : f)] : flags;

    private static string[] _CompileFlags(BuildProfile profile) => [.. _FamilyFlags(profile.Kind), .. profile.CompileFlags];

    private static string[] _FamilyFlags(ProfileKind kind) => kind switch
    {
        // Debug : rien d'optimisé, informations de débogage complètes, pile d'appels lisible, et
        // UBSan avec message (fichier, ligne, extrait). AddressSanitizer n'existe pas pour MinGW.
        // Le mode « durci » de libc++ (index hors limites, déréférencement d'un conteneur vide, etc.) arrête le programme au lieu d'un comportement indéfini.
        ProfileKind.Debug => ["-std=c++23", "-fno-exceptions", "-fno-rtti", "-O0", "-g", "-fno-omit-frame-pointer", "-fsanitize=undefined", "-D_LIBCPP_HARDENING_MODE=_LIBCPP_HARDENING_MODE_EXTENSIVE"],

        // Size : le plus petit exécutable, au prix de la vitesse. -Oz va plus loin que -Os ; chaque fonction et chaque donnée dans sa section
        // pour que l'éditeur de liens retire ce qui n'est jamais utilisé.
        ProfileKind.Size => ["-std=c++23", "-fno-exceptions", "-fno-rtti", "-Oz", "-ffunction-sections", "-fdata-sections"],

        // Release : le mode rapide de Zig (-O3 donne le même binaire). Chaque fonction et chaque
        // donnée dans sa section, pour que l'éditeur de liens retire ce qui n'est jamais utilisé.
        // -Os serait plus léger, mais plus lent : la vitesse prime.
        _ => ["-std=c++23", "-fno-exceptions", "-fno-rtti", "-O2", "-ffunction-sections", "-fdata-sections"],
    };

    /// <summary>Options d'édition de liens du programme : en release, sans symboles et sans code inutilisé.</summary>
    private static string[] LinkFlags(BuildProfile profile) =>
        [.. profile.IsOptimized ? ["-s", "-Wl,--gc-sections"] : Array.Empty<string>(), .. profile.LinkFlags];

    private readonly string _zig;
    private readonly string _runtime;
    private readonly string _cacheRoot;
    private readonly string _version;
    private readonly string _target;
    private readonly string? _openSsl;

    private ZigToolchain(string zig, string runtime, string cacheRoot, string version, string? openSsl)
    {
        _openSsl = openSsl;
        _zig = zig;
        _runtime = runtime;
        _cacheRoot = cacheRoot;
        _version = version;
        _target = HostTarget();
    }

    /// <inheritdoc/>
    public string Name => $"zig c++ {_version}";

    /// <inheritdoc/>
    public string Target => _target;

    /// <summary>Le dossier où <c>rgl setup</c> installe Zig et OpenSSL : <c>tools</c> dans le cache, que <c>rgl cache clear</c> laisse en place.</summary>
    public static string ToolsDirectory(string cacheRoot) => Path.Combine(cacheRoot, "tools");

    /// <summary>
    /// Trouve Zig et le runtime en remontant depuis le dossier du compilateur (<c>tools/zig</c> et
    /// <c>runtime/</c> à la racine du dépôt), ou Zig via la variable d'environnement <c>RGL_ZIG</c>.
    /// </summary>
    /// <param name="cacheRoot">Racine du cache de RGL.</param>
    /// <returns>La chaîne de compilation, ou <see langword="null"/> si Zig ou le runtime sont introuvables.</returns>
    /// <param name="compilerDirectory">Dossier du compilateur d’origine si le serveur de langage est exécuté depuis une copie privée.</param>
    public static ZigToolchain? Locate(string cacheRoot, string? compilerDirectory = null)
    {
        var executable = OperatingSystem.IsWindows() ? "zig.exe" : "zig";
        // Dans l'ordre : la variable RGL_ZIG, tools/zig au-dessus du compilateur (le dépôt), puis ce que « rgl setup » a installé dans le cache.
        var installed = Path.Combine(ToolsDirectory(cacheRoot), "zig", executable);
        var zig = Environment.GetEnvironmentVariable("RGL_ZIG") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : _FindUpward(Path.Combine("tools", "zig", executable), compilerDirectory) ?? (File.Exists(installed) ? installed : null);
        // Le runtime : celui du dépôt s'il est au-dessus du compilateur, sinon celui qu'il embarque.
        var runtimeRoot = _FindUpward(Path.Combine("runtime", "include", "rgl", "standard", "memory.hpp"), compilerDirectory) is { } runtime
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(runtime)!, "..", "..", ".."))
            : EmbeddedRuntime.Extract(cacheRoot);

        if (zig is null || !File.Exists(zig) || runtimeRoot is null) return null;

        var (code, output) = _Run(zig, cacheRoot, ["version"]);
        if (code != 0) return null;

        // OpenSSL installé par « rgl setup » ou tools/fetch-tools.cs (Windows) : en-têtes, bibliothèques et DLL.
        var openSslHeader = Path.Combine("include", "openssl", "ssl.h");
        var installedHeader = Path.Combine(ToolsDirectory(cacheRoot), "openssl", openSslHeader);
        var openSsl = (_FindUpward(Path.Combine("tools", "openssl", openSslHeader), compilerDirectory) ?? (File.Exists(installedHeader) ? installedHeader : null)) is { } header
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(header)!, "..", ".."))
            : null;
        return new ZigToolchain(zig, runtimeRoot, cacheRoot, output.Trim(), openSsl);
    }

    /// <summary>
    /// Composants du runtime : leur fichier source et les composants dont ils dépendent. Ajouter un
    /// fichier au runtime, c'est ajouter une ligne ici et une valeur à <see cref="RuntimeComponent"/>.
    /// </summary>
    private static readonly Dictionary<RuntimeComponent, (string Source, RuntimeComponent[] Needs)> Components = new()
    {
        [RuntimeComponent.Memory] = ("memory.cpp", []),
        [RuntimeComponent.String] = ("string.cpp", [RuntimeComponent.Memory]),
        [RuntimeComponent.Arguments] = ("arguments.cpp", [RuntimeComponent.String]),
        [RuntimeComponent.Symbol] = ("symbol.cpp", [RuntimeComponent.Memory]),
        [RuntimeComponent.Console] = ("console.cpp", [RuntimeComponent.String]),
        [RuntimeComponent.File] = ("file.cpp", [RuntimeComponent.String]),
        [RuntimeComponent.Json] = ("json.cpp", [RuntimeComponent.String, RuntimeComponent.File]),
        [RuntimeComponent.Process] = ("process.cpp", [RuntimeComponent.String]),
        [RuntimeComponent.Net] = ("net.cpp", [RuntimeComponent.String, RuntimeComponent.Memory]),
        [RuntimeComponent.Http] = ("http.cpp", [RuntimeComponent.String, RuntimeComponent.Memory]),
        [RuntimeComponent.HttpTls] = ("http_tls.cpp", [RuntimeComponent.Http, RuntimeComponent.String, RuntimeComponent.Memory]),
        [RuntimeComponent.Native] = ("native.cpp", []),
        [RuntimeComponent.Child] = ("child.cpp", [RuntimeComponent.String]),
        [RuntimeComponent.Unicode] = ("unicode.cpp", [RuntimeComponent.String]),
        [RuntimeComponent.System] = ("system.cpp", [RuntimeComponent.String, RuntimeComponent.Console]),
        [RuntimeComponent.Xml] = ("xml.cpp", [RuntimeComponent.String, RuntimeComponent.File]),
    };

    /// <inheritdoc/>
    public ToolchainResult BuildExecutable(string mainCpp, string executable, BuildProfile profile, IReadOnlySet<RuntimeComponent> components, string? icon, IReadOnlyList<CLibrary>? natives = null)
    {
        var log = new StringBuilder();
        var needed = _WithDependencies(components);

        var objects = new List<string>();
        foreach (var component in needed.Order())
        {
            var obj = _EnsureRuntimeObject(log, component, profile, exceptions: component == RuntimeComponent.Native);
            if (obj is null) return new ToolchainResult(false, log.ToString());
            objects.Add(obj);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        List<string> arguments =
        [
            "c++", .. CompileFlags(profile), "-target", _target,
            "-I" + Path.Combine(_runtime, "include"),
            "-isystem", Path.Combine(_runtime, "third_party", "ctre"),
            .. _NativeIncludes(natives),
            mainCpp, .. objects,
            .. natives?.Select(n => n.Library).OfType<string>().Distinct() ?? [],
            .. LinkFlags(profile),
            "-o", executable,
        ];
        // shell32 fournit la lecture de la ligne de commande en UTF-16 : seulement pour les arguments.
        if (_target.Contains("windows") && needed.Contains(RuntimeComponent.Arguments)) arguments.Add("-lshell32");
        if (_target.Contains("windows") && (needed.Contains(RuntimeComponent.Http) || needed.Contains(RuntimeComponent.Net))) arguments.Add("-lws2_32");
        if (needed.Contains(RuntimeComponent.HttpTls))
        {
            if (Environment.GetEnvironmentVariable("RGL_OPENSSL_LIB") is { Length: > 0 } libraryDirectory)
                arguments.Add("-L" + libraryDirectory);
            else if (_target.Contains("linux"))
                arguments.Add("-L/usr/lib/" + _target.Split('-')[0] + "-linux-gnu");
            if (_openSsl is not null && Environment.GetEnvironmentVariable("RGL_OPENSSL_LIB") is not { Length: > 0 })
            {
                // Zig ne cherche pas les bibliothèques d'import MinGW (libssl.dll.a) par -l : chemins complets.
                arguments.Add(Path.Combine(_openSsl, "lib", "libssl.dll.a"));
                arguments.Add(Path.Combine(_openSsl, "lib", "libcrypto.dll.a"));
            }
            else
            {
                arguments.Add("-lssl");
                arguments.Add("-lcrypto");
            }
            // Le magasin de certificats du système (CertOpenSystemStoreW…) vient de crypt32.
            if (_target.Contains("windows")) arguments.Add("-lcrypt32");
            if (_target.Contains("linux")) arguments.Add("-Wl,--allow-shlib-undefined");
        }

        // L'icône : un script de ressources Windows à côté du .ico, compilé et lié par Zig.
        if (_target.Contains("windows") && icon is not null)
        {
            var script = Path.ChangeExtension(icon, ".rc");
            File.WriteAllText(script, $"1 ICON \"{Path.GetFileName(icon)}\"\n");
            arguments.Insert(arguments.IndexOf(mainCpp) + 1, script);
        }

        var linked = _Step(log, arguments);
        // Les DLL d'OpenSSL doivent se trouver à côté de l'exécutable pour qu'il démarre.
        if (linked && _openSsl is not null && _target.Contains("windows") && needed.Contains(RuntimeComponent.HttpTls))
            foreach (var dll in Directory.GetFiles(Path.Combine(_openSsl, "bin"), "*.dll"))
                File.Copy(dll, Path.Combine(Path.GetDirectoryName(executable)!, Path.GetFileName(dll)), overwrite: true);
        return new ToolchainResult(linked, log.ToString());
    }

    /// <inheritdoc/>
    public ToolchainResult BuildWebAssembly(string source, string output, BuildProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var log = new StringBuilder();
        List<string> arguments = ["c++", source, "-target", "wasm32-freestanding", "-nostdlib", "-fno-exceptions", "-fno-rtti"];
        if (profile.IsOptimized) arguments.Add(profile.Kind == ProfileKind.Size ? "-Oz" : "-O2");
        else arguments.AddRange(["-O0", "-g"]);
        arguments.AddRange(["-Wl,--no-entry", "-Wl,--export=rgl_paper_abi", "-o", output]);
        return new ToolchainResult(_Step(log, arguments), log.ToString());
    }

    /// <inheritdoc/>
    public ToolchainResult EmitAssembly(string mainCpp, string assembly, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null)
    {
        var log = new StringBuilder();
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);

        // Sans informations de débogage (-g0, ajouté après celles du profil), qui noieraient le code ;
        // en syntaxe Intel sur x86, plus lisible.
        List<string> arguments =
        [
            "c++", .. CompileFlags(profile), "-g0", "-target", _target,
            "-I" + Path.Combine(_runtime, "include"),
            "-isystem", Path.Combine(_runtime, "third_party", "ctre"),
            .. _NativeIncludes(natives),
            "-S", mainCpp, "-o", assembly,
        ];
        if (_target.StartsWith("x86")) arguments.Add("-masm=intel");

        return new ToolchainResult(_Step(log, arguments), log.ToString());
    }

    /// <inheritdoc/>
    public ToolchainResult ExplainVectorization(string mainCpp, BuildProfile profile, IReadOnlyList<CLibrary>? natives = null)
    {
        var log = new StringBuilder();
        var objectFile = Path.Combine(_cacheRoot, "vectorization", "main.o");
        Directory.CreateDirectory(Path.GetDirectoryName(objectFile)!);

        // Les remarques de Clang se rangent mal sous les directives #line (« could not determine the original source location ») :
        // on compile une copie où elles sont des commentaires, mêmes numéros de ligne, et le rapport les remet à leur place.
        var plain = Path.Combine(Path.GetDirectoryName(objectFile)!, "main.cpp");
        File.WriteAllLines(plain, File.ReadAllLines(mainCpp).Select(l => l.StartsWith("#line ", StringComparison.Ordinal) ? "//" : l));

        List<string> arguments =
        [
            "c++", .. CompileFlags(profile), "-gline-tables-only", "-target", _target,
            "-I" + Path.Combine(_runtime, "include"),
            "-isystem", Path.Combine(_runtime, "third_party", "ctre"),
            .. _NativeIncludes(natives),
            "-Rpass=loop-vectorize", "-Rpass-missed=loop-vectorize", "-Rpass-analysis=loop-vectorize",
            "-c", plain, "-o", objectFile,
        ];
        return new ToolchainResult(_Step(log, arguments), log.ToString());
    }

    /// <inheritdoc/>
    public ToolchainResult DumpHeaderAst(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read, bool cpp = false) =>
        // « zig cc » et « zig c++ » lisent avec les en-têtes de Zig (la machine n'a pas à avoir un compilateur installé) ; « -x c » ou « -x c++ » impose le langage à un .h.
        // Ils préparent un objet dans leur cache même quand on ne demande que la syntaxe, et échouent parfois à le renommer : ce seul échec n'est pas celui de l'en-tête.
        _DumpHeader([cpp ? "c++" : "cc", "-x", cpp ? "c++" : "c", .. cpp ? new[] { "-std=c++23" } : [], "-target", _target, "-Xclang", "-ast-dump=json", "-fsyntax-only", .. includeDirectories.Select(d => "-I" + d), header], read, syntaxOnly: true);

    /// <inheritdoc/>
    public ToolchainResult DumpHeaderMacros(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) =>
        _DumpHeader(["cc", "-x", "c", "-target", _target, "-E", "-dD", .. includeDirectories.Select(d => "-I" + d), header], read);

    /// <inheritdoc/>
    public ToolchainResult ListHeaderDependencies(string header, IReadOnlyList<string> includeDirectories, Action<Stream> read) =>
        _DumpHeader(["cc", "-x", "c", "-target", _target, "-MM", .. includeDirectories.Select(d => "-I" + d), header], read);

    /// <inheritdoc/>
    public ToolchainResult CompileNativeUnit(string source, string objectFile, BuildProfile profile, IReadOnlyList<string> includeDirectories)
    {
        var log = new StringBuilder();
        Directory.CreateDirectory(Path.GetDirectoryName(objectFile)!);

        // Du C++ écrit à la main : avec les exceptions et le RTTI, que le programme et le runtime n'ont pas.
        var flags = _CompileFlags(profile).Where(f => f != "-fno-rtti").Select(f => f == "-fno-exceptions" ? "-fexceptions" : f);
        List<string> arguments =
        [
            "c++", .. flags, "-target", _target,
            "-I" + Path.Combine(_runtime, "include"),
            "-isystem", Path.Combine(_runtime, "third_party", "ctre"),
            .. includeDirectories.Select(d => "-I" + d),
            "-c", source, "-o", objectFile,
        ];
        // Le cache doit dépendre des inclusions transitives, et non de la date du .cpp engendré.
        string? dependencies = null;
        var dependencyResult = _DumpHeader([.. arguments.Take(arguments.Count - 4), "-MM", source], stream =>
            { using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true); dependencies = reader.ReadToEnd(); });
        if (dependencyResult.Success && dependencies is not null)
        {
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                void Text(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text.Length + ":" + text));
                Text($"native-unit|{_version}|{_target}|{profile.Key}|{typeof(ZigToolchain).Assembly.ManifestModule.ModuleVersionId}");
                foreach (var flag in arguments.Take(arguments.Count - 4)) Text(flag);
                foreach (var file in NativeCache.Parse(dependencies).Prepend(source).Distinct())
                {
                    Text(Path.GetFullPath(file));
                    var content = File.ReadAllBytes(file);
                    Text(content.Length.ToString());
                    hash.AppendData(content);
                }
                var key = Convert.ToHexStringLower(hash.GetHashAndReset());
                var cache = Path.Combine(_cacheRoot, "native-units", key);
                var cached = Path.Combine(cache, "unit.o");
                if (File.Exists(cached))
                {
                    File.Copy(cached, objectFile, overwrite: true);
                    return new ToolchainResult(true, "Unité C++ réutilisée depuis le cache : " + key + "\n");
                }
                if (!_Step(log, arguments)) return new ToolchainResult(false, log.ToString());
                Directory.CreateDirectory(cache);
                var temporary = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".tmp");
                File.Copy(objectFile, temporary);
                File.Move(temporary, cached, overwrite: true);
                return new ToolchainResult(true, log.ToString());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Un cache indisponible ne doit jamais empêcher de compiler.
            }
        }
        return new ToolchainResult(_Step(log, arguments), log.ToString());
    }

    /// <summary>Lance Clang avec <paramref name="arguments"/> et envoie sa sortie à <paramref name="read"/> au fil de l'eau.</summary>
    private ToolchainResult _DumpHeader(List<string> arguments, Action<Stream> read, bool syntaxOnly = false)
    {
        var log = new StringBuilder();
        log.Append("$ zig ").AppendJoin(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)).Append('\n');

        var start = new ProcessStartInfo(_zig)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var zigCache = Path.Combine(_cacheRoot, "zig");
        start.Environment["ZIG_GLOBAL_CACHE_DIR"] = zigCache;
        start.Environment["ZIG_LOCAL_CACHE_DIR"] = zigCache;

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        read(process.StandardOutput.BaseStream);
        process.StandardOutput.BaseStream.CopyTo(Stream.Null);
        process.WaitForExit();
        log.Append(stderr.Result);
        if (process.ExitCode != 0) log.Append($"(code de sortie {process.ExitCode})\n");
        // Avec « -fsyntax-only », Zig essaie quand même de ranger un objet : une erreur « failed to rename » n'est pas celle de l'en-tête, toute autre l'est.
        var realError = stderr.Result.Split((char)10).Any(l => l.Contains("error:", StringComparison.Ordinal) && !l.Contains("failed to rename", StringComparison.Ordinal));
        return new ToolchainResult(process.ExitCode == 0 || (syntaxOnly && !realError), log.ToString());
    }

    /// <summary>Les options <c>-I</c> des dossiers d'en-têtes des bibliothèques C du programme.</summary>
    private static IEnumerable<string> _NativeIncludes(IReadOnlyList<CLibrary>? natives) =>
        natives?.SelectMany(n => n.IncludeDirectories).Distinct().Select(d => "-I" + d) ?? [];

    #region Runtime

    /// <summary>Les composants demandés et, récursivement, ceux dont ils dépendent.</summary>
    private static HashSet<RuntimeComponent> _WithDependencies(IEnumerable<RuntimeComponent> components)
    {
        var result = new HashSet<RuntimeComponent>();
        var pending = new Stack<RuntimeComponent>(components);
        while (pending.TryPop(out var component))
        {
            if (!result.Add(component)) continue;
            foreach (var dependency in Components[component].Needs) pending.Push(dependency);
        }
        return result;
    }

    /// <summary>L'objet compilé d'un fichier du runtime pour la cible et le profil, compilé s'il n'est pas déjà dans le cache.</summary>
    private string? _EnsureRuntimeObject(StringBuilder log, RuntimeComponent component, BuildProfile profile, bool exceptions)
    {
        var source = Components[component].Source;
        var path = Path.Combine(_runtime, "src", source);
        var extra = component == RuntimeComponent.HttpTls ? _OpenSslIncludes() : [];
        var directory = Path.Combine(_cacheRoot, "runtime", _target, profile.Name, _Fingerprint(path, profile, exceptions, extra));
        var obj = Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + ".o");
        if (File.Exists(obj)) return obj;

        // Compile dans un fichier temporaire, puis le renomme : deux builds simultanés ne voient
        // jamais un objet à moitié écrit.
        Directory.CreateDirectory(directory);
        var building = obj + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (!_Step(log, ["c++", .. CompileFlags(profile, exceptions), "-target", _target, "-I" + Path.Combine(_runtime, "include"), .. extra, "-c", path, "-o", building]))
                return null;

            try
            {
                File.Move(building, obj);
            }
            catch (IOException) when (File.Exists(obj))
            {
                // Un autre build l'a terminé en même temps : on garde le sien.
            }
            return obj;
        }
        finally
        {
            if (File.Exists(building)) File.Delete(building);
        }
    }

    /// <summary>
    /// Empreinte d'un fichier du runtime : sa source, tous les en-têtes du runtime (une source peut
    /// les inclure tous), la version de Zig, la cible et les options du profil.
    /// </summary>
    private string _Fingerprint(string source, BuildProfile profile, bool exceptions, IReadOnlyList<string>? extra = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{_version}|{_target}|{string.Join(' ', CompileFlags(profile, exceptions))}|{string.Join(' ', extra ?? [])}"));

        var headers = Directory.GetFiles(Path.Combine(_runtime, "include"), "*", SearchOption.AllDirectories);
        foreach (var file in headers.Order(StringComparer.Ordinal).Prepend(source))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(_runtime, file).Replace('\\', '/')));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>Les en-têtes OpenSSL sont une dépendance opt-in du seul composant HTTPS.</summary>
    private IReadOnlyList<string> _OpenSslIncludes()
    {
        if (Environment.GetEnvironmentVariable("RGL_OPENSSL_INCLUDE") is { Length: > 0 } configured)
            return ["-idirafter", configured];
        if (_openSsl is not null) return ["-idirafter", Path.Combine(_openSsl, "include")];
        if (!_target.Contains("linux")) return [];
        var architecture = _target.Split('-')[0];
        return ["-idirafter", "/usr/include", "-idirafter", $"/usr/include/{architecture}-linux-gnu"];
    }

    #endregion

    #region Processus

    /// <summary>Lance une commande Zig, l'écrit dans le journal avec sa sortie, et renvoie son succès.</summary>
    /// <remarks>
    /// Sous Windows, Zig échoue parfois en écrivant dans son propre cache (« checking cache
    /// failed »), en particulier la première fois qu'il prépare la bibliothèque MinGW d'un mode.
    /// Ce n'est ni une erreur du programme ni un bug du transpileur : la commande est relancée une
    /// fois, et réussit en pratique.
    /// </remarks>
    private bool _Step(StringBuilder log, IReadOnlyList<string> arguments)
    {
        for (var attempt = 1; ; ++attempt)
        {
            log.Append("$ zig ").AppendJoin(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)).Append('\n');
            var (code, output) = _Run(_zig, _cacheRoot, arguments);
            log.Append(output);
            if (code == 0) return true;

            log.Append($"(code de sortie {code})\n");
            if (attempt == 2 || !output.Contains("checking cache failed", StringComparison.Ordinal)) return false;
            log.Append("(échec du cache de Zig : nouvelle tentative)\n");
        }
    }

    private static (int Code, string Output) _Run(string zig, string cacheRoot, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(zig)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var zigCache = Path.Combine(cacheRoot, "zig");
        start.Environment["ZIG_GLOBAL_CACHE_DIR"] = zigCache;
        start.Environment["ZIG_LOCAL_CACHE_DIR"] = zigCache;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Cible de la machine courante, dans la syntaxe de Zig.</summary>
    private static string HostTarget()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "x86",
            _ => "x86_64",
        };
        return OperatingSystem.IsWindows() ? $"{architecture}-windows-gnu" : $"{architecture}-linux-gnu.2.28";
    }

    /// <summary>Cherche <paramref name="relativePath"/> dans le dossier du compilateur, puis dans chacun de ses parents.</summary>
    private static string? _FindUpward(string relativePath, string? compilerDirectory)
    {
        for (var directory = new DirectoryInfo(compilerDirectory ?? AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    #endregion
}
