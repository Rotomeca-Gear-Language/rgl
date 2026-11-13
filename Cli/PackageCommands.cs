using System.Text.Json;
using System.Text.RegularExpressions;
using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Docs;
using Rgl.IR;
using Rgl.Project;
using Rgl.Project.Packages;
using Rgl.Text;

namespace Rgl.Cli;

/// <summary>
/// Les commandes de paquets : <c>add</c>, <c>remove</c>, <c>restore</c>, <c>tree</c>, <c>outdated</c>, <c>update</c> et <c>publish</c>. Elles travaillent sur le
/// <c>rgl.json</c> du dossier (<c>-d</c>, le dossier courant sans lui) — celui d'un programme comme celui d'un paquet — et jamais sur ses fichiers sources,
/// sauf <c>publish</c>, qui compile le paquet. Rien n'est modifié tant que la résolution n'a pas réussi : un conflit ou un paquet introuvable laisse
/// <c>rgl.json</c> et <c>rgl.lock</c> tels qu'ils étaient.
/// </summary>
internal static partial class PackageCommands
{
    /// <summary>Le dossier de travail d'une commande : son manifeste, ses dépendances et ce que le projet autorise.</summary>
    private sealed record Session(string Root, string ManifestPath, string? Name, string? Version, IReadOnlyDictionary<string, string> Dependencies, PackageTrust Trust, IPackageSource Source, string CacheRoot)
    {
        public PackageResolver Resolver => new(Source, CacheRoot, trusted: Trust.Binaries);

        public string LockPath => Path.Combine(Root, PackageLock.FileName);
    }

    /// <summary>Lance la commande de <paramref name="commandLine"/>.</summary>
    public static int Run(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        if (_Open(commandLine, context, diagnostics) is not { } session) return CliRunner.Failure;

        return commandLine.PackageCommand switch
        {
            PackageAction.Add => _Add(session, commandLine.PackageArguments[0], context, diagnostics),
            PackageAction.Remove => _Remove(session, commandLine.PackageArguments[0], context, diagnostics),
            PackageAction.Restore => _Restore(session, context, diagnostics),
            PackageAction.Tree => _Tree(session, context, diagnostics),
            PackageAction.Outdated => _Outdated(session, context, diagnostics),
            PackageAction.Update => _Update(session, commandLine.PackageArguments.FirstOrDefault(), context, diagnostics),
            PackageAction.Publish => _Publish(session, commandLine.DryRun, context, diagnostics),
            _ => throw new InvalidOperationException(),
        };
    }

    private static Session? _Open(CommandLine commandLine, CliContext context, DiagnosticBag diagnostics)
    {
        var root = Path.GetFullPath(commandLine.Directory ?? ".", context.WorkingDirectory);
        var path = Path.Combine(root, ProjectManifest.FileName);
        if (!File.Exists(path))
        {
            diagnostics.Error($"{ProjectManifest.FileName} est introuvable dans {root}", root, DiagnosticCodes.InvalidPackage);
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Error($"{ProjectManifest.FileName} doit être un objet JSON", path, DiagnosticCodes.InvalidPackage);
                return null;
            }

            var (dependenciesOk, dependencies) = PackageManifest.ReadDependencies(document.RootElement, path, diagnostics);
            var (trustOk, trust) = PackageTrust.Read(document.RootElement, path, diagnostics);
            if (!dependenciesOk || !trustOk) return null;

            string? Text(string key) => document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new Session(root, path, Text("name"), Text("version"), dependencies, trust, context.Packages ?? new HttpPackageSource(), context.CacheRoot);
        }
        catch (JsonException e)
        {
            diagnostics.Error($"{ProjectManifest.FileName} n'est pas du JSON valide : {e.Message}", path, DiagnosticCodes.InvalidPackage);
            return null;
        }
    }

    // ---- add / remove / restore ----------------------------------------------------------------------------------------------------------

    private static int _Add(Session session, string argument, CliContext context, DiagnosticBag diagnostics)
    {
        var at = argument.LastIndexOf('@');
        var address = at < 0 ? argument : argument[..at];
        var version = at < 0 ? null : argument[(at + 1)..];
        if (PackageId.Parse(address, out var problem) is not { } id)
        {
            diagnostics.Error($"adresse de paquet invalide : {problem}", session.ManifestPath, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }
        if (version is not null && !PackageVersion.IsValid(version))
        {
            diagnostics.Error($"la version de « {id.Source} » s'écrit exactement, avec trois nombres (« 1.2.0 »), sans intervalle ni préfixe", session.ManifestPath, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }

        version ??= _Latest(session, id, diagnostics);
        if (version is null) return CliRunner.Failure;

        var dependencies = new SortedDictionary<string, string>(session.Dependencies.ToDictionary(d => d.Key, d => d.Value), StringComparer.Ordinal);
        if (dependencies.TryGetValue(id.Source, out var current) && current == version)
        {
            context.Out.WriteLine($"« {id.Source} » est déjà une dépendance en version {version}.");
            return CliRunner.Success;
        }
        var replaced = dependencies.GetValueOrDefault(id.Source);
        dependencies[id.Source] = version;

        var before = File.Exists(session.LockPath) ? PackageLock.Read(session.LockPath, new DiagnosticBag()) : null;
        if (session.Resolver.Resolve(session.Root, dependencies, locked: false, diagnostics) is not { } graph) return CliRunner.Failure;
        if (!ManifestEditor.SetDependencies(session.ManifestPath, dependencies, diagnostics)) return CliRunner.Failure;

        context.Out.WriteLine(replaced is null ? $"Ajouté : {id.Source} {version}" : $"Remplacé : {id.Source} {replaced} → {version}");
        foreach (var package in graph.Packages.Where(p => p.Source != id.Source && before?[p.Source] is null))
            context.Out.WriteLine($"  avec sa dépendance {package.Source} {package.Version}");
        _ReportNative(graph.Packages.Where(p => before?[p.Source] is null), session, context);
        return CliRunner.Success;
    }

    private static int _Remove(Session session, string argument, CliContext context, DiagnosticBag diagnostics)
    {
        if (PackageId.Parse(argument, out var problem) is not { } id)
        {
            diagnostics.Error($"adresse de paquet invalide : {problem}", session.ManifestPath, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }
        if (!session.Dependencies.ContainsKey(id.Source))
        {
            diagnostics.Error($"« {id.Source} » n'est pas une dépendance de ce projet", session.ManifestPath, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }

        var dependencies = session.Dependencies.Where(d => d.Key != id.Source).ToDictionary(d => d.Key, d => d.Value);
        if (session.Resolver.Resolve(session.Root, dependencies, locked: false, diagnostics) is null) return CliRunner.Failure;
        if (!ManifestEditor.SetDependencies(session.ManifestPath, dependencies, diagnostics)) return CliRunner.Failure;

        context.Out.WriteLine($"Retiré : {id.Source}");
        return CliRunner.Success;
    }

    private static int _Restore(Session session, CliContext context, DiagnosticBag diagnostics)
    {
        if (session.Resolver.Resolve(session.Root, session.Dependencies, locked: false, diagnostics) is not { } graph) return CliRunner.Failure;

        context.Out.WriteLine(graph.Packages.Count == 0 ? "Aucun paquet à récupérer." : $"{graph.Packages.Count} paquet(s) prêt(s) dans le cache.");
        return CliRunner.Success;
    }

    // ---- tree / outdated / update --------------------------------------------------------------------------------------------------------

    private static int _Tree(Session session, CliContext context, DiagnosticBag diagnostics)
    {
        if (session.Resolver.Resolve(session.Root, session.Dependencies, locked: false, diagnostics) is not { } graph) return CliRunner.Failure;

        context.Out.WriteLine($"{session.Name ?? Path.GetFileName(session.Root)} {session.Version}".TrimEnd());
        var shown = new HashSet<string>(StringComparer.Ordinal);
        _WriteTree(session.Dependencies.Keys.Order(StringComparer.Ordinal).Select(a => graph[a]).OfType<ResolvedPackage>().ToList(), graph, "", shown, context.Out);
        if (graph.Packages.Count == 0) context.Out.WriteLine("  (aucune dépendance)");
        return CliRunner.Success;
    }

    private static void _WriteTree(IReadOnlyList<ResolvedPackage> packages, PackageGraph graph, string indent, HashSet<string> shown, TextWriter output)
    {
        for (var i = 0; i < packages.Count; i++)
        {
            var package = packages[i];
            var last = i == packages.Count - 1;
            // Un paquet déjà montré ne l'est pas deux fois : « (*) » renvoie à sa première apparition.
            var repeated = !shown.Add(package.Source);
            output.WriteLine($"{indent}{(last ? "`-- " : "|-- ")}{package.Source} {package.Version}{(repeated && package.Dependencies.Count > 0 ? " (*)" : "")}");
            if (!repeated)
                _WriteTree([.. package.Dependencies.Select(a => graph[a]).OfType<ResolvedPackage>()], graph, indent + (last ? "    " : "|   "), shown, output);
        }
    }

    private static int _Outdated(Session session, CliContext context, DiagnosticBag diagnostics)
    {
        if (session.Resolver.Resolve(session.Root, session.Dependencies, locked: false, diagnostics) is not { } graph) return CliRunner.Failure;

        var rows = new List<(string Source, string Current, string Latest, bool Direct)>();
        var failed = false;
        foreach (var package in graph.Packages)
        {
            var versions = session.Source.Versions(package.Id);
            if (versions.Versions is null)
            {
                diagnostics.Warning($"versions de « {package.Source} » impossibles à lire : {versions.Error}", session.ManifestPath);
                failed = true;
                continue;
            }
            if (versions.Versions.Count > 0 && PackageVersion.Compare(versions.Versions[0], package.Version) > 0)
                rows.Add((package.Source, package.Version, versions.Versions[0], session.Dependencies.ContainsKey(package.Source)));
        }

        if (rows.Count == 0)
        {
            context.Out.WriteLine(failed ? "Aucune version plus récente trouvée parmi les paquets lus." : "Tous les paquets sont à jour.");
            return CliRunner.Success;
        }
        var width = rows.Max(r => r.Source.Length);
        context.Out.WriteLine($"{"Paquet".PadRight(width)}  Actuelle  Dernière");
        foreach (var row in rows)
            context.Out.WriteLine($"{row.Source.PadRight(width)}  {row.Current.PadRight(8)}  {row.Latest}{(row.Direct ? "" : "  (dépendance d'un paquet : elle monte avec lui)")}");
        return CliRunner.Success;
    }

    private static int _Update(Session session, string? only, CliContext context, DiagnosticBag diagnostics)
    {
        string? target = null;
        if (only is not null)
        {
            if (PackageId.Parse(only, out var problem) is not { } id)
            {
                diagnostics.Error($"adresse de paquet invalide : {problem}", session.ManifestPath, DiagnosticCodes.InvalidPackage);
                return CliRunner.Failure;
            }
            if (!session.Dependencies.ContainsKey(id.Source))
            {
                diagnostics.Error($"« {id.Source} » n'est pas une dépendance directe de ce projet", session.ManifestPath, DiagnosticCodes.InvalidPackage);
                return CliRunner.Failure;
            }
            target = id.Source;
        }

        var dependencies = session.Dependencies.ToDictionary(d => d.Key, d => d.Value);
        var changes = new List<string>();
        foreach (var (address, current) in session.Dependencies)
        {
            if (target is not null && address != target) continue;
            if (PackageId.Parse(address, out _) is not { } id) continue;
            var latest = _Latest(session, id, diagnostics);
            if (latest is null) return CliRunner.Failure;
            if (PackageVersion.Compare(latest, current) <= 0) continue;
            dependencies[address] = latest;
            changes.Add($"{address} {current} → {latest}");
        }

        if (changes.Count == 0)
        {
            context.Out.WriteLine("Tout est à jour.");
            return CliRunner.Success;
        }
        if (session.Resolver.Resolve(session.Root, dependencies, locked: false, diagnostics) is null) return CliRunner.Failure;
        if (!ManifestEditor.SetDependencies(session.ManifestPath, dependencies, diagnostics)) return CliRunner.Failure;

        foreach (var change in changes) context.Out.WriteLine($"Mis à jour : {change}");
        return CliRunner.Success;
    }

    /// <summary>La dernière version publiée d'un paquet ; <see langword="null"/> (et E155) s'il n'en a aucune ou si la liste ne se lit pas.</summary>
    private static string? _Latest(Session session, PackageId id, DiagnosticBag diagnostics)
    {
        var versions = session.Source.Versions(id);
        if (versions.Versions is null)
        {
            diagnostics.Error($"paquet « {id.Source} » : {versions.Error}", session.ManifestPath, DiagnosticCodes.PackageNotFound);
            return null;
        }
        if (versions.Versions.Count == 0)
        {
            diagnostics.Error($"« {id.Source} » n'a publié aucune version (un tag vX.Y.Z)", session.ManifestPath, DiagnosticCodes.PackageNotFound);
            return null;
        }
        return versions.Versions[0];
    }

    // ---- ce que le paquet contient ---------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^\s*forge\s*:\s*(native|descriptor)\b", RegexOptions.Multiline)]
    private static partial Regex NativeHeader();

    [GeneratedRegex(@"\bcpp\s*\{")]
    private static partial Regex CppBlock();

    /// <summary>Les fichiers d'un paquet qui contiennent du C++ ou des liaisons natives : ceux que le projet doit autoriser.</summary>
    private static IReadOnlyList<string> _NativeFiles(ResolvedPackage package)
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(package.Directory, file).Replace('\\', '/');
            if (relative.EndsWith(".rgl.cpp", StringComparison.OrdinalIgnoreCase)) found.Add(relative);
            else if (relative.EndsWith(".forge", StringComparison.OrdinalIgnoreCase) && File.ReadAllText(file) is var text && (NativeHeader().IsMatch(text) || CppBlock().IsMatch(text))) found.Add(relative);
        }
        return found;
    }

    private static void _ReportNative(IEnumerable<ResolvedPackage> packages, Session session, CliContext context)
    {
        foreach (var package in packages)
        {
            if (_NativeFiles(package) is not { Count: > 0 } files) continue;
            var trusted = session.Trust.Cpp.Contains(package.Source);
            context.Out.WriteLine($"  « {package.Source} » contient du C++ ou des liaisons natives : {string.Join(", ", files.Take(5))}{(files.Count > 5 ? ", …" : "")}");
            context.Out.WriteLine(trusted
                ? "  le projet l'autorise (package-trust.cpp)."
                : $"  il est refusé (E150) tant que le projet ne l'autorise pas : si tu lui fais confiance, écris « {package.Source} » dans package-trust.cpp de {ProjectManifest.FileName}.");
        }
    }

    // ---- publish ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Le paquet d'une adresse de dépôt Git (<c>https://github.com/alice/json.git</c>, <c>git@github.com:alice/json</c>) ; <see langword="null"/> si elle n'est pas d'une forge connue.</summary>
    private static PackageId? _FromRemote(string remote)
    {
        var text = remote.Trim();
        text = Regex.Replace(text, @"^(https?://|ssh://)?(git@)?", "");
        text = text.Replace(':', '/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        return PackageId.Parse(text.TrimEnd('/'), out _);
    }

    private static int _Publish(Session session, bool dryRun, CliContext context, DiagnosticBag diagnostics)
    {
        var manifest = PackageManifest.Load(session.ManifestPath, diagnostics);
        if (manifest is null) return CliRunner.Failure;
        if (!File.Exists(Path.Combine(session.Root, PackageManifest.IndexFile)))
        {
            diagnostics.Error($"{PackageManifest.IndexFile} est introuvable : il dit ce que le paquet rend visible", session.Root, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }

        var git = context.Git ?? new ProcessGitRunner();
        var tag = PackageVersion.Tag(manifest.Version);
        if (git.Run(session.Root, "rev-parse", "--is-inside-work-tree") is not { } inside)
        {
            diagnostics.Error("git est introuvable : « rgl publish » en a besoin pour créer le tag de la version", session.Root, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }
        if (inside.Exit != 0 || inside.Output != "true")
        {
            diagnostics.Error("ce dossier n'est pas un dépôt Git : un paquet est un dépôt dont les versions sont des tags", session.Root, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }

        var status = git.Run(session.Root, "status", "--porcelain");
        if (status is { Output.Length: > 0 })
        {
            diagnostics.Error($"des modifications ne sont pas commitées : le tag {tag} doit désigner exactement ce qui est publié", session.Root, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }
        if (git.Run(session.Root, "tag", "--list", tag) is { Output: var existing } && existing == tag)
        {
            diagnostics.Error($"le tag {tag} existe déjà : change « version » dans {ProjectManifest.FileName}", session.ManifestPath, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }

        PackageId? remote = git.Run(session.Root, "remote", "get-url", "origin") is { Exit: 0 } origin ? _FromRemote(origin.Output) : null;
        if (remote is not null)
        {
            var published = session.Source.Versions(remote);
            if (published.Versions?.Contains(manifest.Version) == true)
            {
                diagnostics.Error($"la version {manifest.Version} de « {remote.Source} » est déjà publiée", session.ManifestPath, DiagnosticCodes.InvalidPackage);
                return CliRunner.Failure;
            }
        }
        else context.Out.WriteLine("Remote « origin » absent ou d'une forge inconnue : la version n'a pas pu être comparée à celles déjà publiées.");

        if (!_Check(session, manifest, context, diagnostics)) return CliRunner.Failure;

        var address = remote?.Source ?? manifest.Name;
        if (dryRun)
        {
            context.Out.WriteLine($"Vérifications faites : {address} {manifest.Version} peut être publié (--dry-run : rien n'a été créé).");
            return CliRunner.Success;
        }

        var created = git.Run(session.Root, "tag", tag);
        if (created is not { Exit: 0 })
        {
            diagnostics.Error($"le tag {tag} n'a pas pu être créé{(string.IsNullOrEmpty(created?.Error) ? "" : " : " + created.Value.Error)}", session.Root, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }
        var pushed = git.Run(session.Root, "push", "origin", tag);
        if (pushed is not { Exit: 0 })
        {
            // Un tag resté local ferait croire à une version publiée.
            git.Run(session.Root, "tag", "-d", tag);
            diagnostics.Error($"le tag {tag} n'a pas pu être poussé vers « origin »{(string.IsNullOrEmpty(pushed?.Error) ? "" : " : " + pushed.Value.Error)}", session.Root, DiagnosticCodes.InvalidPackage);
            return CliRunner.Failure;
        }

        context.Out.WriteLine($"Publié : {address} {manifest.Version} (tag {tag}).");
        return CliRunner.Success;
    }

    /// <summary>
    /// Vérifie que le paquet compile et que sa documentation est sans avertissement. Le paquet est copié dans un projet temporaire comme la box <c>Verification.box</c> :
    /// son <c>index.forge</c> est alors celui d'une box, ses fichiers se trouvent par leur nom, et ses propres dépendances — récupérées comme à l'ordinaire — sont
    /// celles du projet temporaire.
    /// </summary>
    private static bool _Check(Session session, PackageManifest manifest, CliContext context, DiagnosticBag diagnostics)
    {
        var temporary = Path.Combine(context.CacheRoot, "publish", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var box = Path.Combine(temporary, "Verification.box");
            _Copy(session.Root, box);
            File.WriteAllText(Path.Combine(temporary, ProjectManifest.FileName), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = "verification",
                ["version"] = "1.0.0",
                ["rgl"] = ProjectManifest.SupportedLanguageVersion,
                ["dependencies"] = manifest.Dependencies,
            }));
            var entry = Path.Combine(temporary, "main.rgl");
            var entryText = "import box Verification;\ntrain |> done;\n";
            File.WriteAllText(entry, entryText);

            var graph = new PackageResolver(session.Source, context.CacheRoot).Resolve(temporary, manifest.Dependencies, locked: false, diagnostics);
            if (graph is null) return false;

            var load = new DiagnosticBag();
            var policy = new NativePolicy(null, temporary, graph);
            var modules = SourceGraph.Load(new SourceText(entry, entryText), temporary, load, null, toolchain: context.Toolchain, nativeCache: Path.Combine(context.CacheRoot, "native"), nativePolicy: policy, packages: new PackageContext(graph, manifest.Dependencies));
            if (modules is not null)
                new Binder(load, OverflowMode.Checked, ProjectManifest.DefaultRecursionLimit, false, false, policy).Bind(modules);
            foreach (var item in load.Items) diagnostics.Add(item);
            if (load.HasErrors || modules is null) return false;

            var documents = new DiagnosticBag();
            var all = new List<DocEntry>();
            var own = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in modules.Where(m => m.Name.StartsWith("Verification.box.", StringComparison.Ordinal)))
            {
                all.AddRange(DocCollector.Collect(module.Name, module.Unit, documents));
                own.Add(module.Name);
            }
            DocValidator.Validate(all, documents, own);
            var warnings = documents.Items.Where(d => d.Level == DiagnosticLevel.Warning).ToList();
            if (warnings.Count > 0)
            {
                foreach (var warning in warnings) diagnostics.Add(warning);
                diagnostics.Error($"la documentation du paquet a {warnings.Count} avertissement(s) : « rgl doc check » doit passer sans avertissement avant de publier", session.Root, DiagnosticCodes.InvalidPackage);
                return false;
            }
            return true;
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copie ce qu'un paquet publie : tout sauf son manifeste, ses dossiers cachés et de build, et les fichiers que l'extraction écarterait.</summary>
    private static void _Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(ProjectManifest.FileName, StringComparison.OrdinalIgnoreCase) || name.Equals(PackageLock.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (PackageArchive.Reason([name], keepNative: true) is not null) continue;
            File.Copy(file, Path.Combine(destination, name));
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || PackageArchive.Reason([name, "x"], keepNative: true) is not null) continue;
            _Copy(directory, Path.Combine(destination, name));
        }
    }
}
