using System.Text.Json;
using System.Text.RegularExpressions;
using Rgl.Build;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Project.Packages;
using Rgl.Text;

namespace Rgl.Cli;

/// <summary>
/// Tout ce qui passe par l'index de découverte : <c>rgl search</c>, le nom court de <c>rgl add json</c>, <c>rgl run script</c> (un gist figé à une révision) et
/// <c>rgl index build</c>, que le dépôt d'index lance pour fabriquer <c>index.json</c> en vérifiant chaque entrée. L'index ne sert qu'à trouver un nom : un projet
/// compile sans lui, ses dépendances sont des adresses écrites dans son manifeste.
/// </summary>
internal static partial class IndexCommands
{
    /// <summary>Le fichier où est gardée la dernière copie de l'index lu.</summary>
    private static string _CachePath(CliContext context) => Path.Combine(context.CacheRoot, "index", "index.json");

    /// <summary>
    /// Lit l'index : celui du réseau, qui est alors gardé dans le cache ; sans réseau, la dernière copie gardée (une notice le dit). Introuvable : E162.
    /// </summary>
    public static PackageIndex? Load(CliContext context, DiagnosticBag diagnostics)
    {
        var source = context.Index ?? new HttpIndexSource();
        var cache = _CachePath(context);
        var fetched = source.Fetch();
        if (fetched.Json is { } text)
        {
            if (PackageIndex.Parse(text, "de l'index", diagnostics) is not { } index) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                File.WriteAllText(cache, text, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Le cache ne sert qu'à lire hors ligne : ne pas pouvoir l'écrire n'empêche pas de chercher.
            }
            return index;
        }

        if (File.Exists(cache))
        {
            diagnostics.Info($"index hors ligne ({fetched.Error}) : copie du {File.GetLastWriteTime(cache):yyyy-MM-dd}");
            return PackageIndex.Parse(File.ReadAllText(cache), cache, diagnostics);
        }
        diagnostics.Error($"l'index de découverte est introuvable : {fetched.Error}", cache, DiagnosticCodes.InvalidIndex);
        return null;
    }

    /// <summary>L'adresse du paquet de ce nom court dans l'index ; <see langword="null"/> et E163 s'il n'y est pas.</summary>
    public static string? Resolve(string name, CliContext context, DiagnosticBag diagnostics)
    {
        if (Load(context, diagnostics) is not { } index) return null;
        if (index.FindPackage(name) is { } package) return package.Source;

        var near = index.Packages.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).Take(5).ToList();
        diagnostics.Error($"aucun paquet « {name} » dans l'index", null, DiagnosticCodes.IndexName);
        diagnostics.Info(near.Count > 0 ? $"noms proches : {string.Join(", ", near)} ; « rgl search {name} » les décrit" : "« rgl search » liste l'index ; sinon écris l'adresse du dépôt : rgl add github.com/compte/dépôt");
        return null;
    }

    // ---- search ------------------------------------------------------------------------------------------------------------------------------

    /// <summary><c>rgl search [terme]</c> : les paquets et les scripts dont le nom, la description ou un mot-clé contient le terme (sans terme : tout l'index).</summary>
    public static int Search(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        if (Load(context, diagnostics) is not { } index) return CliRunner.Failure;
        var term = commandLine.PackageArguments.FirstOrDefault()?.Trim();

        bool Matches(string? text) => text is not null && (string.IsNullOrEmpty(term) || text.Contains(term, StringComparison.OrdinalIgnoreCase));
        var packages = index.Packages.Where(p => Matches(p.Name) || Matches(p.Description) || p.Keywords.Any(Matches) || Matches(p.Source)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var scripts = index.Scripts.Where(s => Matches(s.Name) || Matches(s.Description)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        if (packages.Count == 0 && scripts.Count == 0)
        {
            context.Out.WriteLine($"Rien ne correspond à « {term} » dans l'index.");
            return CliRunner.Success;
        }
        if (packages.Count > 0)
        {
            var name = Math.Max("Paquet".Length, packages.Max(p => p.Name.Length));
            var address = packages.Max(p => p.Source.Length);
            context.Out.WriteLine($"{"Paquet".PadRight(name)}  {"Adresse".PadRight(address)}  Version  Description");
            foreach (var package in packages)
                context.Out.WriteLine($"{package.Name.PadRight(name)}  {package.Source.PadRight(address)}  {(package.Version ?? "").PadRight(7)}  {package.Description}".TrimEnd());
            context.Out.WriteLine("Ajouter : rgl add <paquet>");
        }
        if (scripts.Count > 0)
        {
            if (packages.Count > 0) context.Out.WriteLine();
            var name = Math.Max("Script".Length, scripts.Max(s => s.Name.Length));
            context.Out.WriteLine($"{"Script".PadRight(name)}  Description");
            foreach (var script in scripts) context.Out.WriteLine($"{script.Name.PadRight(name)}  {script.Description}".TrimEnd());
            context.Out.WriteLine("Lancer : rgl run <script>");
        }
        return CliRunner.Success;
    }

    // ---- les scripts (gists) ---------------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex FileNamePattern();

    /// <summary>
    /// Écrit le gist <paramref name="files"/> comme un projet dans <paramref name="directory"/> et vérifie ce qu'un script peut être : exactement un fichier <c>.rgl</c> (le
    /// train d'entrée), des <c>.forge</c> à côté, éventuellement un <c>rgl.json</c> dont seules les <c>dependencies</c> comptent — des paquets de l'index, jamais
    /// <c>package-trust</c> : un script lancé de l'extérieur ne s'autorise rien. Le manifeste du projet écrit est le sien : <c>no-cpp</c> et <c>no-trust</c> (ni C++, ni
    /// <c>@trusted</c>). Tout le reste est E164.
    /// </summary>
    /// <returns>Faux si une erreur a été signalée (rien n'est alors laissé dans le dossier).</returns>
    public static bool PrepareScript(IReadOnlyDictionary<string, string> files, string name, PackageIndex index, string directory, DiagnosticBag diagnostics)
    {
        string Label() => $"script « {name} »";
        var ok = true;
        void Fail(string message)
        {
            diagnostics.Error($"{Label()} : {message}", null, DiagnosticCodes.InvalidScript);
            ok = false;
        }

        if (files.Count > 20) Fail("plus de 20 fichiers");
        foreach (var file in files.Keys)
        {
            if (!FileNamePattern().IsMatch(file)) Fail($"nom de fichier non permis : « {file} »");
            else if (!(file.EndsWith(".rgl", StringComparison.Ordinal) || file.EndsWith(".forge", StringComparison.Ordinal) || file == ProjectManifest.FileName))
                Fail($"« {file} » n'est ni un .rgl, ni un .forge, ni un rgl.json : un script ne contient que du code RGL");
        }
        var entries = files.Keys.Where(f => f.EndsWith(".rgl", StringComparison.Ordinal)).ToList();
        if (entries.Count != 1) Fail(entries.Count == 0 ? "aucun fichier .rgl (le train d'entrée)" : $"{entries.Count} fichiers .rgl : un script n'en a qu'un");

        IReadOnlyDictionary<string, string> dependencies = new Dictionary<string, string>();
        if (files.TryGetValue(ProjectManifest.FileName, out var manifestText))
        {
            try
            {
                using var document = JsonDocument.Parse(manifestText);
                if (document.RootElement.ValueKind != JsonValueKind.Object) Fail("rgl.json doit être un objet JSON");
                else
                {
                    if (document.RootElement.TryGetProperty("package-trust", out _)) Fail("un script ne s'autorise rien : « package-trust » est refusé");
                    var local = new DiagnosticBag();
                    var (valid, read) = PackageManifest.ReadDependencies(document.RootElement, ProjectManifest.FileName, local);
                    foreach (var item in local.Items) diagnostics.Add(item with { Code = DiagnosticCodes.InvalidScript, Message = $"{Label()} : {item.Message}" });
                    if (!valid) ok = false;
                    else dependencies = read;
                }
            }
            catch (JsonException e)
            {
                Fail($"rgl.json n'est pas du JSON valide : {e.Message}");
            }
        }
        foreach (var address in dependencies.Keys)
            if (index.FindSource(address) is null) Fail($"la dépendance « {address} » n'est pas dans l'index : un script ne tire que des paquets de l'index");

        if (!ok) return false;

        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        foreach (var (file, content) in files.Where(f => f.Key != ProjectManifest.FileName))
            File.WriteAllText(Path.Combine(directory, file), content, new System.Text.UTF8Encoding(false));

        var manifest = new Dictionary<string, object>
        {
            ["name"] = name,
            ["version"] = "1.0.0",
            ["rgl"] = ProjectManifest.SupportedLanguageVersion,
            ["no-cpp"] = true,
            ["no-trust"] = true,
        };
        if (dependencies.Count > 0) manifest["dependencies"] = dependencies;
        File.WriteAllText(Path.Combine(directory, ProjectManifest.FileName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n", new System.Text.UTF8Encoding(false));
        return true;
    }

    /// <summary>Le dossier où est écrit un script : <c>&lt;cache&gt;/gists/&lt;id&gt;/&lt;révision&gt;</c>.</summary>
    private static string _ScriptDirectory(CliContext context, IndexScript script) => Path.Combine(context.CacheRoot, "gists", script.Gist, script.Revision);

    /// <summary>
    /// <c>rgl run script [-- arguments]</c> : le script de l'index (E164 s'il n'y est pas), lu à sa révision — jamais autrement : l'auteur du gist peut le modifier après la
    /// vérification, pas la révision —, écrit dans le cache puis compilé et exécuté comme <c>rgl exec</c>, son code de sortie propagé.
    /// </summary>
    public static int RunScript(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        if (Load(context, diagnostics) is not { } index) return CliRunner.Failure;
        var name = commandLine.PackageArguments[0];
        if (index.FindScript(name) is not { } script)
        {
            diagnostics.Error($"« {name} » n'est pas un script de l'index", null, DiagnosticCodes.InvalidScript);
            diagnostics.Info("« rgl search » liste les scripts ; seul un script de l'index se lance, vérifié à une révision");
            return CliRunner.Failure;
        }

        var directory = _ScriptDirectory(context, script);
        var marker = Path.Combine(directory, ".rgl-gist");
        if (!File.Exists(marker))
        {
            var fetched = (context.Gists ?? new HttpGistSource()).Fetch(script.Gist, script.Revision);
            if (fetched.Files is null)
            {
                diagnostics.Error($"script « {name} » : {fetched.Error}", null, DiagnosticCodes.InvalidScript);
                return CliRunner.Failure;
            }
            if (!PrepareScript(fetched.Files, name, index, directory, diagnostics)) return CliRunner.Failure;
            File.WriteAllText(marker, script.Revision);
        }

        return CliRunner.Build(new CommandLine(CommandKind.Exec, Directory: directory, ProgramArgs: commandLine.ProgramArgs), context, diagnostics);
    }

    // ---- index build ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>rgl index build [dossier] [-o fichier]</c> : lit <c>packages/*.json</c> (<c>{"source": "github.com/alice/json", "keywords": […]}</c>, nommé comme le paquet) et
    /// <c>scripts/*.json</c> (<c>{"gist": "…", "revision": "…", "description": "…"}</c>), vérifie chaque entrée — le paquet est récupéré à sa dernière version, son manifeste doit porter
    /// le nom du fichier, il doit compiler ; le script est lu à sa révision, accepté comme un script et compilé — et écrit <c>index.json</c>. La moindre erreur n'écrit rien. C'est la
    /// commande que lance l'action GitHub du dépôt d'index avant d'accepter une demande de fusion.
    /// </summary>
    public static int Build(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var root = Path.GetFullPath(commandLine.Directory ?? ".", context.WorkingDirectory);
        var packagesDirectory = Path.Combine(root, "packages");
        var scriptsDirectory = Path.Combine(root, "scripts");
        if (!Directory.Exists(packagesDirectory) && !Directory.Exists(scriptsDirectory))
        {
            diagnostics.Error($"ni packages/ ni scripts/ dans {root} : un dépôt d'index range une entrée JSON par paquet et par script", root, DiagnosticCodes.InvalidIndex);
            return CliRunner.Failure;
        }

        var source = context.Packages ?? new HttpPackageSource();
        var packages = new List<IndexPackage>();
        var scripts = new List<IndexScript>();
        var failed = false;

        foreach (var path in _Entries(packagesDirectory))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (_BuildPackage(path, name, source, context, diagnostics) is { } package) packages.Add(package);
            else failed = true;
        }
        var provisional = new PackageIndex(packages, []);
        foreach (var path in _Entries(scriptsDirectory))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (_BuildScript(path, name, provisional, context, diagnostics) is { } script) scripts.Add(script);
            else failed = true;
        }
        if (failed) return CliRunner.Failure;

        var index = new PackageIndex(packages, scripts);
        var output = Path.GetFullPath(commandLine.Output ?? Path.Combine(root, "index.json"), context.WorkingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, index.ToText(), new System.Text.UTF8Encoding(false));
        context.Out.WriteLine($"Index écrit dans {Path.GetRelativePath(context.WorkingDirectory, output)} : {packages.Count} paquet(s), {scripts.Count} script(s).");
        return CliRunner.Success;
    }

    private static IEnumerable<string> _Entries(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal) : [];

    private static JsonElement? _ReadEntry(string path, DiagnosticBag diagnostics, out JsonDocument? document)
    {
        document = null;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement;
            diagnostics.Error("l'entrée doit être un objet JSON", path, DiagnosticCodes.InvalidIndex);
        }
        catch (JsonException e)
        {
            diagnostics.Error($"l'entrée n'est pas du JSON valide : {e.Message}", path, DiagnosticCodes.InvalidIndex);
        }
        return null;
    }

    private static string? _Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static IndexPackage? _BuildPackage(string path, string name, IPackageSource source, CliContext context, DiagnosticBag diagnostics)
    {
        if (_ReadEntry(path, diagnostics, out var document) is not { } entry) return null;
        using (document)
        {
            if (_Text(entry, "source") is not { } written || PackageId.Parse(written, out var problem) is not { } id)
            {
                diagnostics.Error($"« source » doit être l'adresse d'un dépôt (hôte/compte/dépôt)", path, DiagnosticCodes.InvalidIndex);
                return null;
            }

            var versions = source.Versions(id);
            if (versions.Versions is not { Count: > 0 })
            {
                diagnostics.Error($"« {id.Source} » : {versions.Error ?? "aucune version publiée (un tag vX.Y.Z)"}", path, DiagnosticCodes.PackageNotFound);
                return null;
            }
            var latest = versions.Versions[0];

            // Le paquet est récupéré et vérifié comme à l'ordinaire, dans un projet temporaire.
            var temporary = Path.Combine(context.CacheRoot, "index-build", Guid.NewGuid().ToString("N")[..12]);
            try
            {
                Directory.CreateDirectory(temporary);
                var graph = new PackageResolver(source, context.CacheRoot, writeLock: false).Resolve(temporary, new Dictionary<string, string> { [id.Source] = latest }, locked: false, diagnostics);
                if (graph?[id.Source] is not { } package) return null;
                if (package.Manifest.Name != name)
                {
                    diagnostics.Error($"le manifeste de « {id.Source} » s'appelle « {package.Manifest.Name} » : l'entrée doit s'appeler packages/{package.Manifest.Name}.json", path, DiagnosticCodes.InvalidIndex);
                    return null;
                }
                if (!PackageCommands.CheckPackage(package.Directory, package.Manifest, source, context, diagnostics, requireDocs: false)) return null;

                var keywords = entry.TryGetProperty("keywords", out var words) && words.ValueKind == JsonValueKind.Array
                    ? words.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.String).Select(w => w.GetString()!).ToList()
                    : [];
                return new IndexPackage(name, id.Source, package.Manifest.Description, package.Manifest.License, latest, keywords);
            }
            finally
            {
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static IndexScript? _BuildScript(string path, string name, PackageIndex index, CliContext context, DiagnosticBag diagnostics)
    {
        if (_ReadEntry(path, diagnostics, out var document) is not { } entry) return null;
        using (document)
        {
            var text = new DiagnosticBag();
            var candidate = new PackageIndex([], [new IndexScript(name, _Text(entry, "gist") ?? "", _Text(entry, "revision") ?? "", _Text(entry, "description"))]);
            // Le nom, le gist et la révision se vérifient comme ceux d'un index lu.
            var parsed = PackageIndex.Parse(candidate.ToText(), path, text);
            foreach (var item in text.Items) diagnostics.Add(item);
            if (parsed?.FindScript(name) is not { } script) return null;

            var fetched = (context.Gists ?? new HttpGistSource()).Fetch(script.Gist, script.Revision);
            if (fetched.Files is null)
            {
                diagnostics.Error($"script « {name} » : {fetched.Error}", path, DiagnosticCodes.InvalidScript);
                return null;
            }

            var directory = Path.Combine(context.CacheRoot, "index-build", "script-" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                if (!PrepareScript(fetched.Files, name, index, directory, diagnostics)) return null;

                // Le script doit compiler : jusqu'au C++, sans Zig (la vérification n'a pas besoin de l'exécutable).
                var build = new DiagnosticBag();
                var project = RglProject.Load(null, directory, context.WorkingDirectory, build);
                var result = project is null ? null : BuildPipeline.Run(project, context.CacheRoot, BuildOutput.Cpp, BuildProfile.Debug, context.Toolchain, build, context.Packages);
                foreach (var item in build.Items) diagnostics.Add(item);
                return result is null ? null : script;
            }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
