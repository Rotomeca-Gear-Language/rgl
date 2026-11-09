using Rgl.Diagnostics;
using Rgl.Project;

namespace Rgl.Lsp;

/// <summary>Les fichiers que le document ouvert peut importer : ceux de son espace de noms (le projet, ou la box où il se trouve).</summary>
/// <param name="Root">Le dossier du projet : celui du <c>rgl.json</c> le plus proche, sinon celui du document.</param>
/// <param name="ScopeDirectory">Le dossier où les imports cherchent : le projet, ou le dossier de la box qui contient le document.</param>
/// <param name="Modules">Les fichiers <c>.forge</c> de cet espace, le document lui-même exclu.</param>
internal sealed record ProjectContext(string Root, string ScopeDirectory, IReadOnlyList<ModuleInfo> Modules, IReadOnlyList<ModuleInfo>? Std = null, IReadOnlyList<ModuleInfo>? StdExtensions = null, IReadOnlyList<ModuleInfo>? Entries = null)
{
    /// <summary>Les fichiers où chercher les emplois d'un nom : les <c>.forge</c> de l'espace et le fichier d'entrée <c>.rgl</c> du projet.</summary>
    public IEnumerable<ModuleInfo> Sources => Modules.Where(m => !PaperProjection.IsPaper(m.Path)).Concat(Entries ?? []);

    /// <summary>Les fichiers <c>forge: extend</c> du projet et de la std : leurs extensions s'appliquent à tout le programme.</summary>
    public IEnumerable<ModuleInfo> Extensions => Modules.Where(m => m.Kind == "extend").Concat(StdExtensions ?? []);

    /// <summary>
    /// Le contexte vu depuis un autre fichier du projet : lui-même exclu, le document que ce contexte excluait (<paramref name="self"/>) revenu. Sans cela, un fichier
    /// qui importe le document ne retrouverait pas ce qu'il importe, et ses emplois ne désigneraient plus rien.
    /// </summary>
    public ProjectContext SeenFrom(ModuleInfo other, ModuleInfo self)
    {
        var isForge = Path.GetExtension(self.Path).Equals(".forge", StringComparison.OrdinalIgnoreCase);
        bool NotOther(ModuleInfo m) => !string.Equals(m.Path, other.Path, StringComparison.OrdinalIgnoreCase);
        return this with
        {
            Modules = [.. Modules.Where(NotOther), .. isForge ? [self] : Array.Empty<ModuleInfo>()],
            Entries = [.. (Entries ?? []).Where(NotOther), .. isForge ? Array.Empty<ModuleInfo>() : [self]],
        };
    }

    /// <summary>Les noms de tous les fichiers de la std, à plat : ce que <c>std.nom</c> désigne.</summary>
    public IReadOnlyDictionary<string, RglSymbol> StdNames
    {
        get
        {
            var names = new Dictionary<string, RglSymbol>(StringComparer.Ordinal);
            foreach (var module in Std ?? [])
                foreach (var symbol in module.Exported.Values.Where(s => s.IsTopLevel && !s.Name.StartsWith('_'))) names.TryAdd(symbol.Name, symbol);
            return names;
        }
    }

    /// <summary>Le fichier ou la box de la std de ce nom, tel que <c>use &lt;Nom&gt;;</c> l'écrit.</summary>
    public ModuleInfo? FindStd(string name) => Std?.FirstOrDefault(m => m.Name == name);

    /// <summary>Le fichier de ce nom (sans extension), tel qu'un import l'écrit.</summary>
    public ModuleInfo? Find(string name) => Modules.FirstOrDefault(m => m.Name == name);
}

/// <summary>
/// Les fichiers d'un projet lus pour l'éditeur : la liste de leurs déclarations sert à proposer les noms à importer et à décrire
/// ce qu'on survole. Chaque fichier n'est relu que s'il a changé sur le disque, ou s'il est ouvert dans l'éditeur avec un autre
/// texte. Il suit les règles de recherche des imports : les dossiers cachés et exclus ne sont pas parcourus, une box est opaque.
/// </summary>
internal sealed class ProjectIndex
{
    private const string BoxSuffix = ".box";

    private sealed record Cached(DateTime Stamp, string? OpenText, ModuleInfo Module);

    private readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combien de temps la liste des fichiers d'un espace reste valable : un parcours du disque à chaque demande de complétion coûtait plus que la complétion.</summary>
    private const long FileListLifetime = 700;

    private readonly Dictionary<string, (long At, string[] Files)> _fileLists = new(StringComparer.OrdinalIgnoreCase);

    // La std ne change pas pendant la vie du compilateur (elle est extraite sous l'identité de l'assembly) : elle est lue une fois pour tous les index.
    private static readonly object _stdLock = new();
    private static string? _stdDirectory;
    private static string? _stdCulture;
    private static List<ModuleInfo>? _stdModules;
    private static List<ModuleInfo>? _stdExtensionModules;

    // Les exclusions d'un manifeste, relues seulement s'il a changé.
    private static readonly Dictionary<string, (DateTime Stamp, string[] Exclude)> _excludes = new(StringComparer.OrdinalIgnoreCase);

    private string[] _FileList(string scope, string[] exclude)
    {
        var key = scope + "|" + string.Join(';', exclude);
        var now = Environment.TickCount64;
        if (_fileLists.TryGetValue(key, out var cached) && now - cached.At < FileListLifetime) return cached.Files;

        var files = _Files(scope, exclude).ToArray();
        _fileLists[key] = (now, files);
        return files;
    }

    /// <summary>La std lue pour l'éditeur, une seule fois : un fichier de la std ouvert dans l'éditeur avec un texte modifié n'est pas relu.</summary>
    private List<ModuleInfo>? _ReadStdOnce(Func<string, string?> openText, out List<ModuleInfo> extensions)
    {
        var directory = StdLibrary.Directory;
        // La documentation d'un nom est rendue dans la langue de l'éditeur au moment de la lecture : un autre réglage de langue relit la std.
        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        lock (_stdLock)
        {
            if (directory is not null && _stdModules is not null && _stdDirectory == directory && _stdCulture == culture)
            {
                extensions = _stdExtensionModules!;
                return _stdModules;
            }
        }

        var modules = _ReadStd(openText, out extensions);
        if (modules is not null && directory is not null)
        {
            lock (_stdLock)
            {
                _stdDirectory = directory;
                _stdCulture = culture;
                _stdModules = modules;
                _stdExtensionModules = extensions;
            }
        }
        return modules;
    }

    /// <summary>Le contexte de <paramref name="documentPath"/>.</summary>
    /// <param name="documentPath">Le document ouvert.</param>
    /// <param name="openText">Le texte d'un fichier tel que l'éditeur le montre, s'il y est ouvert ; <see langword="null"/> sinon.</param>
    public ProjectContext Open(string documentPath, Func<string, string?> openText)
    {
        var directory = Path.GetDirectoryName(documentPath);
        // Un document sans dossier (non enregistré, ou d'un projet absent) n'a pas de fichiers à importer, mais la std se propose partout.
        if (directory is null || !Path.IsPathRooted(documentPath) || !Directory.Exists(directory))
            return new ProjectContext(directory ?? "", directory ?? "", [], _ReadStdOnce(openText, out var lonelyExtensions), lonelyExtensions);

        var root = _FindRoot(directory);
        var exclude = _Exclude(root);
        var scope = _FindScope(directory, root);

        var modules = new List<ModuleInfo>();
        foreach (var path in _FileList(scope, exclude))
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(documentPath), StringComparison.OrdinalIgnoreCase)) continue;
            if (_Read(path, openText) is { } module) modules.Add(module);
        }
        // Le fichier d'entrée (.rgl, à la racine du projet) emploie des noms des .forge : il compte pour les références et la hiérarchie d'appels.
        var entries = new List<ModuleInfo>();
        if (string.Equals(scope, root, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var path in Directory.GetFiles(root, "*.rgl", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                if (!Path.GetExtension(path).Equals(".rgl", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(path), Path.GetFullPath(documentPath), StringComparison.OrdinalIgnoreCase)) continue;
                if (_Read(path, openText) is { } entry) entries.Add(entry);
            }
        }
        var std = _ReadStdOnce(openText, out var stdExtensions);
        return new ProjectContext(root, scope, modules, std, stdExtensions, entries);
    }

    /// <summary>
    /// Les fichiers de la std qu'un <c>use &lt;…&gt;</c> peut écrire : ses fichiers de premier niveau, et chacune de ses boxes, lue par le fichier
    /// qui porte son nom (l'index ne fait que réexporter).
    /// </summary>
    private List<ModuleInfo>? _ReadStd(Func<string, string?> openText, out List<ModuleInfo> extensions)
    {
        extensions = [];
        if (StdLibrary.Directory is not { } std) return null;

        var modules = new List<ModuleInfo>();
        foreach (var path in _Files(std, []))
        {
            if (_Read(path, openText) is not { } module) continue;
            if (module.Kind == "extend") extensions.Add(module);
            else modules.Add(module);
        }
        foreach (var box in Directory.GetDirectories(std, "*" + BoxSuffix, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            if (_ReadBox(box) is { } module) modules.Add(module);
        }
        return modules;
    }

    /// <summary>
    /// Une box de la std : lue par le fichier de son nom (<c>Math.box/Math.forge</c>), sinon par son index (<c>Geometry.box/index.forge</c>), mais elle montre ce que son
    /// index exporte : les noms de tous les fichiers et de toutes les boxes qu'il étale (<c>export {...Vectors, ...geo};</c>).
    /// </summary>
    private static ModuleInfo? _ReadBox(string box)
    {
        var name = Path.GetFileName(box)[..^BoxSuffix.Length];
        var main = Path.Combine(box, name + ".forge");
        if (!File.Exists(main)) main = Path.Combine(box, "index.forge");
        if (!File.Exists(main)) return null;

        var module = ModuleInfo.Build(main, File.ReadAllText(main), name);
        var members = Directory.GetFiles(box, "*.forge", SearchOption.TopDirectoryOnly).Select(f => ModuleInfo.Build(f, File.ReadAllText(f))).ToList();
        foreach (var nested in Directory.GetDirectories(box, "*" + BoxSuffix, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            if (_ReadBox(nested) is { } inner) members.Add(inner);
        }
        var index = members.FirstOrDefault(m => m.Name == "index");
        var spread = index?.Unit.Statements.OfType<AST.Files.ExportDeclaration>().SelectMany(e => e.Names).Where(n => n.Kind is AST.Files.ExportKind.Spread).Select(n => n.Name.Name) ?? [];
        module.MergeExports(members, spread);
        return module;
    }

    /// <summary>Le dossier du <code>rgl.json</code> le plus proche en remontant, sinon le dossier de départ.</summary>
    private static string _FindRoot(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, ProjectManifest.FileName))) return current;
        }
        return directory;
    }

    private static string[] _Exclude(string root)
    {
        var manifest = Path.Combine(root, ProjectManifest.FileName);
        var stamp = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest) : DateTime.MinValue;
        if (_excludes.TryGetValue(manifest, out var cached) && cached.Stamp == stamp) return cached.Exclude;

        var excluded = stamp != DateTime.MinValue ? ProjectManifest.Load(manifest, new DiagnosticBag())?.Exclude : null;
        var result = (string[])[.. (excluded ?? ProjectManifest.DefaultExclude).Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e, root)))];
        _excludes[manifest] = (stamp, result);
        return result;
    }

    /// <summary>La box la plus proche qui contient le dossier, dans le projet ; sinon le projet.</summary>
    private static string _FindScope(string directory, string root)
    {
        for (var current = directory; current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
        {
            if (Path.GetFileName(current).EndsWith(BoxSuffix, StringComparison.OrdinalIgnoreCase)) return current;
        }
        return root;
    }

    /// <summary>Les <c>.forge</c> d'un espace de noms : sans les dossiers cachés ou exclus, ni ceux des autres boxes.</summary>
    private static IEnumerable<string> _Files(string directory, string[] exclude)
    {
        IEnumerable<string> files;
        IEnumerable<string> children;
        try
        {
            // Les pages .paper sont des fichiers importables comme les .forge : elles donnent leurs noms (render, mount, State…) à l'éditeur.
            files = Directory.GetFiles(directory, "*.forge", SearchOption.TopDirectoryOnly).Where(f => Path.GetExtension(f).Equals(".forge", StringComparison.OrdinalIgnoreCase))
                .Concat(Directory.GetFiles(directory, "*.paper", SearchOption.TopDirectoryOnly).Where(f => Path.GetExtension(f).Equals(".paper", StringComparison.OrdinalIgnoreCase)));
            children = Directory.GetDirectories(directory);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files.Order(StringComparer.Ordinal)) yield return file;
        foreach (var child in children.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name.EndsWith(BoxSuffix, StringComparison.OrdinalIgnoreCase) || exclude.Contains(Path.TrimEndingDirectorySeparator(child), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in _Files(child, exclude)) yield return file;
        }
    }

    /// <summary>
    /// Une page <c>.paper</c> vue comme le module que le compilateur en fait (<c>render</c>, <c>mount</c>, <c>State</c> et les gears de ses blocs <c>@rgl</c>),
    /// pour que <c>import Home;</c> donne ses noms à l'éditeur. Une page que le compilateur refuse (une interpolation non fermée pendant la frappe, un
    /// handler inconnu) n'est lue que par le RGL de ses blocs, pour ne pas perdre son nom. Les positions de ce module ne sont pas celles de la page :
    /// l'éditeur n'y mène qu'au fichier.
    /// </summary>
    private static ModuleInfo _BuildPaper(string path, string text)
    {
        var diagnostics = new DiagnosticBag();
        var root = _FindRoot(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var compiled = Rgl.Paper.PaperCompiler.Compile(new Rgl.Text.SourceText(path, text), root, diagnostics);
        var module = ModuleInfo.Build(path, compiled is null ? PaperProjection.Build(text).VirtualText : compiled.Source.Content);

        // Les fragments d'interpolation sont un détail de la page : ils ne se proposent pas.
        foreach (var name in module.Symbols.Keys.Where(n => n.StartsWith("__paper_", StringComparison.Ordinal)).ToList())
        {
            module.Symbols.Remove(name);
            module.Exported.Remove(name);
        }
        return module;
    }

    private ModuleInfo? _Read(string path, Func<string, string?> openText)
    {
        var open = openText(path);
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var cached) && cached.Stamp == stamp && cached.OpenText == open && cached.Module.Kind != "native") return cached.Module;

            var module = PaperProjection.IsPaper(path) ? _BuildPaper(path, open ?? File.ReadAllText(path)) : ModuleInfo.Build(path, open ?? File.ReadAllText(path));
            if (module.Kind == "native")
            {
                // Les inclusions C peuvent changer sans que le fichier forge soit modifié.
                var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                var entry = new Rgl.Text.SourceText(Path.Combine(directory, "__editeur_native__.rgl"), $"import {module.Name};\n!train |> done;");
                var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(path)] = open ?? File.ReadAllText(path) };
                var modules = SourceGraph.Load(entry, directory, new DiagnosticBag(), openTexts: texts,
                    toolchain: NativeEditorModel.Toolchain, nativeCache: NativeEditorModel.CacheDirectory, nativePolicy: new NativePolicy(File.Exists(Path.Combine(_FindRoot(directory), ProjectManifest.FileName)) ? ProjectManifest.Load(Path.Combine(_FindRoot(directory), ProjectManifest.FileName), new DiagnosticBag()) : null, _FindRoot(directory)));
                if (modules?.FirstOrDefault(m => m.Name == module.Name && m.Native is not null) is { } native)
                    module.AddNativeBindings(native.Unit);
            }
            _cache[path] = new Cached(stamp, open, module);
            return module;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
