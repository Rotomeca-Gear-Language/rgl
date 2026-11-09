using Rgl.AST.Train;
using Rgl.AST;
using Rgl.AST.Datasets;
using Rgl.AST.Descriptors;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Interfaces;
using Rgl.AST.Variables;
using Rgl.Binding;
using Rgl.Build.Toolchain;
using Rgl.Diagnostics;
using Rgl.IR.Symbols;
using Rgl.Native;
using Rgl.Parsing;
using Rgl.Paper;
using Rgl.Syntax;
using Rgl.Text;
using System.Text;

namespace Rgl.Project;

/// <summary>
/// Les fichiers d'un programme : le <c>.rgl</c> d'entrée et, de proche en proche, les <c>.forge</c> qu'il importe. Un import désigne
/// un fichier par son nom, jamais par un chemin : <c>import Commandes;</c> trouve <c>Commandes.forge</c> où qu'il soit dans le
/// projet. Deux fichiers de même nom sont interdits (E107), sauf dans les boxes (<c>Nom.box</c>), opaques : le projet ne les
/// regarde pas, et un fichier d'une box ne cherche que dans sa box (<c>import [..] f;</c> cherche hors d'elle). <c>import box B;</c>
/// lit l'<c>index.forge</c> de <c>B.box</c>, <c>import tools T;</c> les fichiers de <c>T.tools</c>. Chaque fichier est lu, analysé
/// et vérifié (<see cref="FileChecks"/>) une fois ; le graphe des imports est sans cycle (E108) et se range dans l'ordre où lier
/// les fichiers : chacun après ceux qu'il importe, le fichier d'entrée en dernier.
/// </summary>
internal static class SourceGraph
{
    /// <summary>Extension du dossier d'une box.</summary>
    private const string BoxSuffix = ".box";

    /// <summary>Extension du dossier d'un tools.</summary>
    private const string ToolsSuffix = ".tools";

    /// <summary>Nom du fichier qui expose une box.</summary>
    private const string IndexName = "index";

    /// <summary>
    /// Charge le fichier d'entrée et tout ce qu'il importe.
    /// </summary>
    /// <param name="entry">Le texte du fichier d'entrée, déjà lu.</param>
    /// <param name="rootDirectory">Le dossier du projet, où se cherchent les fichiers importés.</param>
    /// <param name="diagnostics">Reçoit les erreurs de lecture, de syntaxe et d'import de tous les fichiers.</param>
    /// <param name="exclude">Dossiers du projet, relatifs à <paramref name="rootDirectory"/>, où la recherche des fichiers ne descend pas ; <c>out</c> par défaut.</param>
    /// <returns>Les fichiers dans l'ordre de liaison ; <see langword="null"/> si une erreur a été signalée.</returns>
    /// <param name="openTexts">Le texte des fichiers ouverts dans un éditeur, par chemin complet : il remplace celui du disque.</param>
    /// <param name="toolchain">La chaîne de compilation, qui lit les en-têtes C des fichiers <c>forge: native</c> ; <see langword="null"/> si elle est introuvable.</param>
    /// <param name="nativeCache">Le dossier du cache des liaisons des en-têtes C ; <see langword="null"/> pour ne pas en utiliser.</param>
    public static IReadOnlyList<SourceModule>? Load(SourceText entry, string rootDirectory, DiagnosticBag diagnostics, IReadOnlyList<string>? exclude = null, IReadOnlyDictionary<string, string>? openTexts = null, ICompilerToolchain? toolchain = null, string? nativeCache = null, NativePolicy? nativePolicy = null)
    {
        var loader = new Loader(rootDirectory, diagnostics, exclude ?? ProjectManifest.DefaultExclude, openTexts, toolchain, nativeCache, nativePolicy);
        // Deux fichiers de même nom dans une même portée, importés ou non, empêchent de compiler : un nom désigne un seul fichier.
        if (!loader.CheckNames()) return null;
        if (loader.Parse(entry, isEntry: true, isIndex: false) is not { } unit) return null;

        // Un fichier forge: extend s'applique à tout le programme : il est chargé sans être importé, avant les autres.
        loader.LoadExtensionFiles();

        // La std est importée dans tous les fichiers : ses fichiers sont tous liés, avant ceux du projet.
        loader.LoadStd();

        var imports = loader.Visit(ref unit, loader.ProjectScope, []);
        loader.Modules.Add(new SourceModule(Path.GetFileNameWithoutExtension(entry.Path), unit, true, imports));
        return diagnostics.HasErrors ? null : _MergeDatasetExtensions(loader.Modules, diagnostics);
    }

    /// <summary>
    /// Fond chaque <c>extend dataset Nom { … }</c> dans le dataset qu'il étend : les entrées s'ajoutent à la fin de celles du fichier qui le déclare — l'un des
    /// fichiers que l'extension importe, ou le sien —, dans l'ordre où les fichiers se lient (celui qui déclare le dataset d'abord), de sorte que les
    /// identifiants du jeu de base ne bougent pas. Les extensions sont retirées des fichiers : le binder ne voit qu'un dataset plus long. Un dataset que
    /// nul fichier visible ne déclare, ou que plusieurs déclarent, est E138 ; rien n'est changé pour un programme sans extension.
    /// </summary>
    private static IReadOnlyList<SourceModule>? _MergeDatasetExtensions(List<SourceModule> modules, DiagnosticBag diagnostics)
    {
        if (!modules.Any(m => m.Unit.Statements.Any(s => s is DatasetExtension))) return modules;

        var byName = modules.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
        var additions = new Dictionary<(string Module, string Dataset), List<DatasetEntry>>();
        foreach (var module in modules)
        {
            foreach (var extension in module.Unit.Statements.OfType<DatasetExtension>())
            {
                var visible = new List<string> { module.Name };
                if (module.Imports is not null) visible.AddRange(module.Imports.Values.SelectMany(ids => ids));
                var targets = visible.Distinct().Where(id => byName.TryGetValue(id, out var candidate)
                    && candidate.Unit.Statements.OfType<DatasetDeclaration>().Any(d => d.Name.Name == extension.Name.Name)).ToList();
                if (targets.Count != 1)
                {
                    diagnostics.Error(extension.Name.Span, DiagnosticCodes.InvalidDataset,
                        targets.Count == 0 ? $"le dataset « {extension.Name.Name} » n'est déclaré par aucun fichier que celui-ci importe" : $"plusieurs fichiers importés déclarent un dataset « {extension.Name.Name} »",
                        targets.Count == 0 ? "dataset introuvable" : "ambigu",
                        [targets.Count == 0 ? "importe le fichier qui déclare le dataset : import Moves;" : "importe un seul de ces fichiers"]);
                    continue;
                }
                if (!additions.TryGetValue((targets[0], extension.Name.Name), out var list)) additions[(targets[0], extension.Name.Name)] = list = [];
                list.AddRange(extension.Entries);
            }
        }
        if (diagnostics.HasErrors) return null;

        var merged = new List<SourceModule>();
        foreach (var module in modules)
        {
            var statements = new List<IStatement>();
            foreach (var statement in module.Unit.Statements)
            {
                if (statement is DatasetExtension) continue;
                statements.Add(statement is DatasetDeclaration dataset && additions.TryGetValue((module.Name, dataset.Name.Name), out var more)
                    ? dataset with { Entries = [.. dataset.Entries, .. more] }
                    : statement);
            }
            merged.Add(module with { Unit = module.Unit with { Statements = statements } });
        }
        return merged;
    }

    /// <summary>
    /// Une portée de noms de fichiers : le projet (boxes exclues) ou l'intérieur d'une box.
    /// </summary>
    private sealed class FileScope(string directory, string? box)
    {
        /// <summary>Le dossier racine de la portée.</summary>
        public string Directory { get; } = directory;

        /// <summary>Le nom du dossier de la box (<c>Database.box</c>) ; <see langword="null"/> pour le projet.</summary>
        public string? Box { get; } = box;

        /// <summary>Vrai pour la bibliothèque standard et ses boxes.</summary>
        public bool IsStd { get; init; }

        /// <summary>Les <c>.forge</c> de la portée, par nom sans extension.</summary>
        public Dictionary<string, List<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Les composants <c>.paper</c>, importables comme des modules ordinaires.</summary>
        public Dictionary<string, List<string>> PaperFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Les <c>.json</c> de la portée, par nom sans extension : ceux que <c>import json nom;</c> peut lire.</summary>
        public Dictionary<string, List<string>> DataFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Les <c>.xml</c> de la portée, par nom sans extension : ceux que <c>import xml nom;</c> peut lire.</summary>
        public Dictionary<string, List<string>> XmlFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tous les fichiers ordinaires de la portée, par nom complet avec extension, pour <c>import file</c>.</summary>
        public Dictionary<string, List<string>> RawFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Les dossiers <c>.tools</c> de la portée, par nom sans extension.</summary>
        public Dictionary<string, List<string>> Tools { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Le nom unique, dans le programme, du fichier de ce nom dans cette portée.</summary>
        public string IdOf(string name) => Box is null ? name : Box + "." + name;
    }

    private sealed class Loader
    {
        /// <summary>Les arbres des fichiers de la std déjà lus sans erreur, par chemin (la std est extraite sous l'identité du compilateur : un chemin désigne toujours le même contenu).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CompilationUnit> _stdUnits = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _root;
        private readonly DiagnosticBag _diagnostics;

        /// <summary>Les dossiers où la recherche ne descend pas, en chemins complets.</summary>
        private readonly HashSet<string> _excluded;

        /// <summary>Le projet, puis chaque box, avec leurs fichiers.</summary>
        private readonly List<FileScope> _scopes = [];

        /// <summary>Les boxes du projet par nom sans extension (<c>Database</c> pour <c>Database.box</c>).</summary>
        private readonly Dictionary<string, List<FileScope>> _boxes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Les fichiers visités par leur nom unique : en cours (vrai) ou terminés (faux).</summary>
        private readonly Dictionary<string, bool> _visiting = new(StringComparer.Ordinal);

        /// <summary>La portée de la bibliothèque standard ; <see langword="null"/> si elle n'est pas disponible.</summary>
        private readonly FileScope? _std;

        /// <summary>Les boxes de la bibliothèque standard.</summary>
        private readonly Dictionary<string, List<FileScope>> _stdBoxes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Le texte des fichiers ouverts dans un éditeur, par chemin complet.</summary>
        private readonly IReadOnlyDictionary<string, string>? _openTexts;

        /// <summary>La chaîne de compilation qui lit les en-têtes C ; <see langword="null"/> si elle est introuvable.</summary>
        private readonly ICompilerToolchain? _toolchain;

        /// <summary>Le dossier du cache des liaisons des en-têtes C ; <see langword="null"/> sans cache.</summary>
        private readonly string? _cacheDirectory;
        private readonly NativePolicy _nativePolicy;

        public Loader(string root, DiagnosticBag diagnostics, IReadOnlyList<string> exclude, IReadOnlyDictionary<string, string>? openTexts = null, ICompilerToolchain? toolchain = null, string? cacheDirectory = null, NativePolicy? nativePolicy = null)
        {
            _nativePolicy = nativePolicy ?? NativePolicy.Unrestricted;
            _cacheDirectory = cacheDirectory;
            _toolchain = toolchain;
            _openTexts = openTexts;
            _root = root;
            _diagnostics = diagnostics;
            _excluded = exclude.Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e, root))).ToHashSet(StringComparer.OrdinalIgnoreCase);

            _scopes.Add(new FileScope(root, null));
            if (Directory.Exists(root)) _Fill(_scopes[0], root, _boxes);

            if (StdLibrary.Directory is { } std)
            {
                _std = new FileScope(std, "std") { IsStd = true };
                _scopes.Add(_std);
                _Fill(_std, std, _stdBoxes);
            }
        }

        /// <summary>Les fichiers chargés, dans l'ordre de liaison.</summary>
        public List<SourceModule> Modules { get; } = [];

        /// <summary>La portée du projet.</summary>
        public FileScope ProjectScope => _scopes[0];

        /// <summary>
        /// Range les <c>.forge</c> de <paramref name="directory"/> et de ses sous-dossiers dans <paramref name="scope"/>. Une box a sa
        /// propre portée ; les dossiers cachés et exclus ne sont pas parcourus.
        /// </summary>
        private void _Fill(FileScope scope, string directory, Dictionary<string, List<FileScope>> boxesByName)
        {
            foreach (var path in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (!scope.RawFiles.TryGetValue(name, out var list)) scope.RawFiles[name] = list = [];
                list.Add(path);
            }

            foreach (var path in Directory.GetFiles(directory, "*" + RglProject.ForgeExtension, SearchOption.TopDirectoryOnly)
                         // Sous Windows, le motif « *.forge » attrape aussi « *.forgex » (correspondance sur 8.3).
                         .Where(f => Path.GetExtension(f).Equals(RglProject.ForgeExtension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!scope.Files.TryGetValue(name, out var list)) scope.Files[name] = list = [];
                list.Add(path);
            }

            foreach (var path in Directory.GetFiles(directory, "*" + PaperCompiler.Extension, SearchOption.TopDirectoryOnly)
                         .Where(f => Path.GetExtension(f).Equals(PaperCompiler.Extension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!scope.PaperFiles.TryGetValue(name, out var list)) scope.PaperFiles[name] = list = [];
                list.Add(path);
            }

            foreach (var path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                         .Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!scope.DataFiles.TryGetValue(name, out var list)) scope.DataFiles[name] = list = [];
                list.Add(path);
            }

            foreach (var path in Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly)
                         .Where(f => Path.GetExtension(f).Equals(".xml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!scope.XmlFiles.TryGetValue(name, out var list)) scope.XmlFiles[name] = list = [];
                list.Add(path);
            }

            foreach (var child in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || _excluded.Contains(Path.TrimEndingDirectorySeparator(child))) continue;

                if (name.EndsWith(BoxSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var box = new FileScope(child, (scope.IsStd ? "std." : "") + name) { IsStd = scope.IsStd };
                    _scopes.Add(box);
                    var boxName = name[..^BoxSuffix.Length];
                    if (!boxesByName.TryGetValue(boxName, out var boxes)) boxesByName[boxName] = boxes = [];
                    boxes.Add(box);
                    _Fill(box, child, boxesByName);
                    continue;
                }

                if (name.EndsWith(ToolsSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var toolsName = name[..^ToolsSuffix.Length];
                    if (!scope.Tools.TryGetValue(toolsName, out var folders)) scope.Tools[toolsName] = folders = [];
                    folders.Add(child);
                }
                _Fill(scope, child, boxesByName);
            }
        }

        /// <summary>Charge les fichiers <c>forge: extend</c> du projet, de ses boxes et de la std, qu'ils soient importés ou non.</summary>
        public void LoadExtensionFiles()
        {
            foreach (var scope in _scopes)
            {
                foreach (var path in scope.Files.Values.Where(l => l.Count == 1).Select(l => l[0]).Order(StringComparer.Ordinal))
                    if (_IsExtensionFile(path)) _Load(path, scope, null, []);
            }
        }

        /// <summary>Charge tous les fichiers de la bibliothèque standard (hors boxes), que le projet les importe ou non.</summary>
        public void LoadStd()
        {
            if (_std is null) return;
            foreach (var path in _std.Files.Values.Where(l => l.Count == 1).Select(l => l[0]).Order(StringComparer.Ordinal))
                _Load(path, _std, null, []);

            // Une box de la std se montre par son index.
            foreach (var box in _stdBoxes.Values.SelectMany(b => b))
                if (_Exact(box.Files, IndexName) is { } index) _Load(index, box, null, []);
        }

        /// <summary>Vrai si l'en-tête du fichier est <c>forge: extend</c> : lu sans analyser le fichier.</summary>
        private static bool _IsExtensionFile(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path).Take(40))
                {
                    var text = line.Trim();
                    if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal)) continue;
                    return System.Text.RegularExpressions.Regex.IsMatch(text, @"^forge\s*:\s*extend\b");
                }
            }
            catch (IOException) { }
            return false;
        }

        /// <summary>Signale chaque nom porté par plusieurs fichiers ou boxes d'une même portée (E107) ; faux s'il y en a.</summary>
        public bool CheckNames()
        {
            var ok = true;
            foreach (var scope in _scopes)
            {
                foreach (var name in scope.Files.Keys.Concat(scope.PaperFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var paths = scope.Files.GetValueOrDefault(name, []).Concat(scope.PaperFiles.GetValueOrDefault(name, [])).ToList();
                    if (paths.Count <= 1) continue;
                    ok = false;
                    _diagnostics.Error($"plusieurs fichiers portent le nom « {name} » : {string.Join(", ", paths.Select(p => Path.GetRelativePath(_root, p)))}", paths[0], DiagnosticCodes.ImportNotFound);
                }
            }
            foreach (var (name, scopes) in _boxes.Where(b => b.Value.Count > 1))
            {
                ok = false;
                _diagnostics.Error($"plusieurs boxes portent le nom « {name} » : {string.Join(", ", scopes.Select(s => Path.GetRelativePath(_root, s.Directory)))}", scopes[0].Directory, DiagnosticCodes.ImportNotFound);
            }
            return ok;
        }

        private int _Errors() => _diagnostics.Items.Count(d => d.Level == DiagnosticLevel.Error);

        /// <summary>Lit, analyse et vérifie un fichier ; <see langword="null"/> si une erreur lexicale ou de syntaxe a été signalée.</summary>
        /// <param name="isIndex">Vrai pour <c>index.forge</c> d'une box : c'est son genre, et il n'est que le sien.</param>
        public CompilationUnit? Parse(SourceText source, bool isEntry, bool isIndex)
        {
            var before = _Errors();

            // Après une erreur lexicale (chaîne non fermée…), le parseur ne signalerait que des erreurs en cascade.
            var tokens = new Lexer(source, _diagnostics).Tokenize();
            if (_Errors() > before) return null;

            // Le binder ne reçoit qu'un arbre complet : après une erreur de syntaxe, on s'arrête.
            var unit = new Parser(tokens, _diagnostics).Parse();
            if (_Errors() > before) return null;

            FileChecks.Check(unit, isEntry, _diagnostics);
            if (!isEntry && _Errors() == before && unit.Header?.Kind is { } kind)
            {
                if (kind.Name == "index" && !isIndex)
                    _diagnostics.Error(kind.Span, DiagnosticCodes.MisplacedDeclaration, "le genre index est celui de index.forge, dans une box", "pas un index de box",
                        ["l'index d'une box est le fichier index.forge de son dossier Nom.box"]);
                else if (isIndex && kind.Name != "index")
                    _diagnostics.Error(kind.Span, DiagnosticCodes.MisplacedDeclaration, "l'index d'une box est de genre index", "pas de genre index",
                        ["écris forge: index en tête de index.forge"]);
            }
            return _Errors() > before ? null : unit;
        }

        /// <summary>
        /// Charge les fichiers que <paramref name="unit"/> importe, avant lui. <paramref name="chain"/> est la suite
        /// des imports qui y mènent, pour reconnaître un cycle.
        /// </summary>
        /// <param name="scope">La portée du fichier qui importe : où ses imports cherchent leurs fichiers.</param>
        /// <returns>Pour chaque import, les noms uniques des fichiers qu'il désigne.</returns>
        public Dictionary<ImportDeclaration, string[]> Visit(ref CompilationUnit unit, FileScope scope, List<string> chain)
        {
            unit = _ExpandData(unit, scope);
            unit = _ExpandDatasets(unit, scope);
            var resolved = new Dictionary<ImportDeclaration, string[]>(ReferenceEqualityComparer.Instance);
            foreach (var import in unit.Imports)
            {
                var targets = _Resolve(import, scope);
                if (targets is null) continue;

                var ids = new List<string>();
                foreach (var (path, targetScope) in targets)
                    if (_Load(path, targetScope, import, chain) is { } id) ids.Add(id);
                resolved[import] = [.. ids];
            }
            return resolved;
        }

        /// <summary>
        /// Remplace chaque <c>import json nom;</c> et <c>import xml nom;</c> de <paramref name="unit"/> par la constante que donne <c>nom.json</c> ou <c>nom.xml</c> (lu ici, par son nom, comme un
        /// <c>.forge</c> : où qu'il soit dans le projet ou dans la box du fichier). Une constante vit où une constante vit : pas dans un fichier
        /// de molds, d'index ou de precasts.
        /// </summary>
        private CompilationUnit _ExpandData(CompilationUnit unit, FileScope scope)
        {
            if (!unit.Statements.Any(s => s is ImportDeclaration { Kind: ImportKind.Json or ImportKind.Xml or ImportKind.File or ImportKind.FileOutside })) return unit;

            var statements = new List<IStatement>();
            foreach (var statement in unit.Statements)
            {
                if (statement is not ImportDeclaration { Kind: ImportKind.Json or ImportKind.Xml or ImportKind.File or ImportKind.FileOutside } import) statements.Add(statement);
                else if (_DataConstant(import, unit, scope) is { } constant) statements.Add(constant);
            }
            return unit with { Statements = statements };
        }

        /// <summary>
        /// Donne à chaque <c>dataset Nom from "fichier.json" of|as Mold;</c> de <paramref name="unit"/> les entrées que contient <c>fichier.json</c> (lu ici, par
        /// son nom, comme un <c>import json</c>) : un texte RGL engendré, analysé comme un dataset écrit, dont seules les entrées sont gardées — le nom
        /// et le mold restent ceux du source. Un dataset sans entrée lue après une erreur est retiré.
        /// </summary>
        private CompilationUnit _ExpandDatasets(CompilationUnit unit, FileScope scope)
        {
            if (!unit.Statements.Any(s => s is DatasetDeclaration { Source: not null })) return unit;

            var statements = new List<IStatement>();
            foreach (var statement in unit.Statements)
            {
                if (statement is not DatasetDeclaration { Source: not null } dataset) statements.Add(statement);
                else if (_DatasetEntries(dataset, scope) is { } entries) statements.Add(dataset with { Entries = entries });
            }
            return unit with { Statements = statements };
        }

        /// <summary>Les entrées que le fichier de données d'un dataset donne ; <see langword="null"/> après avoir signalé l'erreur.</summary>
        private IReadOnlyList<DatasetEntry>? _DatasetEntries(DatasetDeclaration dataset, FileScope scope)
        {
            var literal = dataset.Source!;
            var written = literal.Span.Text.Trim('"', '\'');
            var extension = Path.GetExtension(written);
            if (extension.Length > 0 && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Error(literal.Span, DiagnosticCodes.InvalidDataset, $"un dataset ne lit que du JSON : « {written} »", "pas du JSON", ["écris le nom du fichier .json, par exemple : from \"moves.json\""]);
                return null;
            }
            var name = Path.GetFileNameWithoutExtension(written);

            if (!scope.DataFiles.TryGetValue(name, out var candidates))
            {
                _diagnostics.Error(literal.Span, DiagnosticCodes.ImportNotFound, $"fichier « {name}.json » introuvable", "introuvable",
                    ["un dataset désigne son fichier par son nom, où qu'il soit dans le projet",
                     scope.DataFiles.Count == 0 ? "il n'y a aucun fichier .json ici" : $"fichiers .json : {string.Join(", ", scope.DataFiles.Keys.Order(StringComparer.Ordinal))}"]);
                return null;
            }
            if (candidates.Count > 1)
            {
                _diagnostics.Error(literal.Span, DiagnosticCodes.ImportNotFound, $"plusieurs fichiers portent le nom « {name}.json »", "ambigu",
                    [$"fichiers : {string.Join(", ", candidates.Select(c => Path.GetRelativePath(_root, c)))}"]);
                return null;
            }
            if (Path.GetFileNameWithoutExtension(candidates[0]) != name)
            {
                _diagnostics.Error(literal.Span, DiagnosticCodes.ImportNotFound, $"le fichier s'appelle « {Path.GetFileName(candidates[0])} », pas « {name}.json »", "mauvaise casse",
                    ["les noms de fichiers sont sensibles à la casse"]);
                return null;
            }

            var path = candidates[0];
            var source = _openTexts is not null && _openTexts.TryGetValue(Path.GetFullPath(path), out var open) ? new SourceText(path, open) : SourceText.FromPath(path, _diagnostics);
            if (source is null) return null;

            Action<string> report = message => _diagnostics.Error($"{Path.GetFileName(path)} : {message}", path, DiagnosticCodes.InvalidDataset);
            var text = JsonConstants.ToDataset(source.Content, dataset.Name.Name, dataset.CreatesMold ? "as" : "of", dataset.Mold.Name, report);
            if (text is null) return null;
            text = Rgl.Lsp.Formatter.FormatText(path, text);

            // Le texte engendré est analysé comme un dataset écrit ; ses positions sont celles de ce texte, dans le fichier de données.
            var before = _Errors();
            var tokens = new Lexer(new SourceText(path, text), _diagnostics).Tokenize();
            var parsed = _Errors() > before ? null : new Parser(tokens, _diagnostics).Parse();
            if (parsed?.Statements.OfType<DatasetDeclaration>().FirstOrDefault() is not { } generated || _Errors() > before) return null;
            return generated.Entries;
        }

        /// <summary>La constante d'un <c>import json</c> ; <see langword="null"/> après avoir signalé l'erreur.</summary>
        private DataConstant? _DataConstant(ImportDeclaration import, CompilationUnit unit, FileScope scope)
        {
            if (import.Kind is ImportKind.File or ImportKind.FileOutside) return _FileConstant(import, unit, scope);
            var isXml = import.Kind == ImportKind.Xml;
            var format = isXml ? "xml" : "json";
            var files = isXml ? scope.XmlFiles : scope.DataFiles;
            var kind = unit.Header?.Kind?.Name;
            if (kind is not (null or "gears" or "train" or "extend"))
            {
                _diagnostics.Error(import.Span, DiagnosticCodes.MisplacedDeclaration, $"import {format} donne une constante : pas dans un fichier forge: {kind}", "constante ici", ["écris-le dans le .rgl d'entrée ou dans un fichier forge: gears, train ou extend"]);
                return null;
            }

            var written = import.Module.Name;
            if (!files.TryGetValue(written, out var candidates))
            {
                _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"fichier « {written}.{format} » introuvable", "introuvable",
                    [$"un import {format} désigne un fichier par son nom, où qu'il soit dans le projet",
                     files.Count == 0 ? $"il n'y a aucun fichier .{format} ici" : $"fichiers .{format} : {string.Join(", ", files.Keys.Order(StringComparer.Ordinal))}"]);
                return null;
            }
            if (candidates.Count > 1)
            {
                _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"plusieurs fichiers portent le nom « {written}.{format} »", "ambigu",
                    [$"fichiers : {string.Join(", ", candidates.Select(c => Path.GetRelativePath(_root, c)))}"]);
                return null;
            }
            if (Path.GetFileNameWithoutExtension(candidates[0]) != written)
            {
                _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"le fichier s'appelle « {Path.GetFileName(candidates[0])} », pas « {written}.{format} »", "mauvaise casse",
                    ["les noms de fichiers sont sensibles à la casse"]);
                return null;
            }

            var path = candidates[0];
            var source = _openTexts is not null && _openTexts.TryGetValue(Path.GetFullPath(path), out var open) ? new SourceText(path, open) : SourceText.FromPath(path, _diagnostics);
            if (source is null) return null;

            var name = import.LocalModuleName;
            Action<string> report = message => _diagnostics.Error($"{Path.GetFileName(path)} : {message}", path, DiagnosticCodes.InvalidPrecast);
            var text = isXml ? XmlConstants.ToSource(source.Content, name, report) : JsonConstants.ToSource(source.Content, name, report);
            if (text is null) return null;
            text = Rgl.Lsp.Formatter.FormatText(path, text);

            // Le texte engendré est analysé comme n'importe quelle déclaration ; ses positions sont celles de ce texte, dans le fichier de données.
            var before = _Errors();
            var generated = new SourceText(path, text);
            var tokens = new Lexer(generated, _diagnostics).Tokenize();
            var parsed = _Errors() > before ? null : new Parser(tokens, _diagnostics).Parse();
            if (parsed?.Statements.OfType<VarDeclaration>().FirstOrDefault() is not { } declaration || _Errors() > before) return null;
            return new DataConstant(import.Span, declaration with { IsConst = true });
        }

        /// <summary>Le contenu UTF-8 d'un <c>import file</c>, transformé en constante string.</summary>
        private DataConstant? _FileConstant(ImportDeclaration import, CompilationUnit unit, FileScope scope)
        {
            var kind = unit.Header?.Kind?.Name;
            if (kind is not (null or "gears" or "train" or "extend"))
            {
                _diagnostics.Error(import.Span, DiagnosticCodes.MisplacedDeclaration, $"import file donne une constante : pas dans un fichier forge: {kind}", "constante ici",
                    ["écris-le dans le .rgl d'entrée ou dans un fichier forge: gears, train ou extend"]);
                return null;
            }

            var written = _Unquote(import.Module.Span.Text);
            var target = import.Kind == ImportKind.FileOutside && scope.IsStd ? _std! : import.Kind == ImportKind.FileOutside ? ProjectScope : scope;
            if (!target.RawFiles.TryGetValue(written, out var candidates) || candidates.Count == 0)
            {
                _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"fichier « {written} » introuvable", "introuvable",
                    [target.Box is null ? "le fichier est cherché par son nom dans tout le projet" : $"le fichier est cherché dans la box ; « import [..] file \"{written}\" as nom; » cherche hors d'elle",
                     target.RawFiles.Count == 0 ? "il n'y a aucun fichier dans cette portée" : $"fichiers : {string.Join(", ", target.RawFiles.Keys.Order(StringComparer.Ordinal))}"]);
                return null;
            }

            var path = candidates[0]; // La recherche de la portée est déterministe : dossiers et fichiers sont parcourus par ordre ordinal.
            if (Path.GetFileName(path) != written)
            {
                _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"le fichier s'appelle « {Path.GetFileName(path)} », pas « {written} »", "mauvaise casse",
                    ["les noms de fichiers sont sensibles à la casse"]);
                return null;
            }

            var source = _openTexts is not null && _openTexts.TryGetValue(Path.GetFullPath(path), out var open) ? new SourceText(path, open) : SourceText.FromPath(path, _diagnostics);
            if (source is null || import.Alias is null) return null;
            var generated = new SourceText(path, $"const string {import.Alias.Name}: {_QuoteFile(source.Content)};");
            var before = _Errors();
            var parsed = new Parser(new Lexer(generated, _diagnostics).Tokenize(), _diagnostics).Parse();
            if (parsed?.Statements.OfType<VarDeclaration>().FirstOrDefault() is not { } declaration || _Errors() > before) return null;
            return new DataConstant(import.Span, declaration with { IsConst = true });
        }

        /// <summary>Encode tout texte UTF-16 en littéral RGL classique, sans interpolation.</summary>
        private static string _QuoteFile(string content)
        {
            var quoted = new StringBuilder(content.Length + 2).Append('"');
            foreach (var c in content)
            {
                quoted.Append(c switch
                {
                    '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    '\0' => "\\0", '\a' => "\\a", '\b' => "\\b", '\f' => "\\f", '\v' => "\\v",
                    _ when char.IsControl(c) => $"\\u{{{(int)c:X}}}", _ => c.ToString(),
                });
            }
            return quoted.Append('"').ToString();
        }

        /// <summary>
        /// Remplace un fichier <c>forge: native</c> par le fichier de gears que Clang et les liaisons en tirent : Clang lit les en-têtes (au fil de l'eau),
        /// chaque fonction convertible devient un gear. Renvoie aussi ce que l'édition de liens y ajoute ; <see langword="null"/> après une erreur signalée.
        /// </summary>
        private (CompilationUnit Unit, CLibrary Library)? _ExpandNative(CompilationUnit unit, string path)
        {
            var header = unit.Header!;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;

            if (_ResolveHeaderFiles(header, directory) is not var (headers, library)) return null;

            if (_toolchain is null)
            {
                _diagnostics.Error(header.Span, DiagnosticCodes.UnreadableHeader, "la chaîne de compilation est introuvable : les en-têtes C ne peuvent pas être lus", "Zig manquant",
                    ["lance : rgl setup"]);
                return null;
            }

            var includes = headers.Select(h => Path.GetDirectoryName(h.Path)!).Distinct().ToList();

            // Les mêmes en-têtes (et ce qu'ils incluent), avec le même Clang, le même compilateur et la même cible, donnent les mêmes liaisons : le texte est rangé dans le cache.
            var key = _cacheDirectory is null ? null : NativeCache.Key(_toolchain, [.. headers.Select(h => h.Path)], includes, Path.GetFileName(path));
            if (key is not null && NativeCache.Lookup(_cacheDirectory!, key) is { } cached)
            {
                var fromCache = new CLibrary([.. headers.Select(h => h.Path)], includes, library, cached.Summary, cached.Unconverted, cached.OtherDeclarations);
                _NoteLeftOut(path, fromCache);
                return _ParseBindings(path, cached.Source, fromCache);
            }

            List<CFunction> functions = [];
            List<CSkipped> skipped = [];
            List<CRecord> records = [];
            List<CEnum> enums = [];
            List<CMacro> macros = [];
            Dictionary<string, string> aliases = new(StringComparer.Ordinal);
            foreach (var (span, full) in headers)
            {
                CHeader? read = null;
                var dumped = _toolchain.DumpHeaderAst(full, includes, stream => read = ClangAstReader.Read(stream, full));
                if (!dumped.Success || read is null)
                {
                    // L'en-tête est celui de l'utilisateur : ce que Clang en dit est un diagnostic de son fichier, pas un bug du transpileur.
                    var lines = dumped.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("$ ", StringComparison.Ordinal)).Take(6);
                    _diagnostics.Error(span, DiagnosticCodes.UnreadableHeader, $"l'en-tête « {Path.GetFileName(full)} » ne se lit pas", "illisible", [.. lines.Select(l => l.TrimEnd())]);
                    return null;
                }
                functions.AddRange(read.Functions);
                skipped.AddRange(read.Skipped);
                records.AddRange(read.Records ?? []);
                enums.AddRange(read.Enums ?? []);

                // Les macros ne sont pas dans l'arbre : un second passage de Clang, qui ne garde que le préprocesseur.
                IReadOnlyList<CMacro>? readMacros = null;
                var preprocessed = _toolchain.DumpHeaderMacros(full, includes, stream => readMacros = CMacroReader.Read(stream, full));
                if (!preprocessed.Success || readMacros is null)
                {
                    var lines = preprocessed.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("$ ", StringComparison.Ordinal)).Take(6);
                    _diagnostics.Error(span, DiagnosticCodes.UnreadableHeader, $"l'en-tête « {Path.GetFileName(full)} » ne se préprocesse pas", "illisible", [.. lines.Select(l => l.TrimEnd())]);
                    return null;
                }
                macros.AddRange(readMacros);
                foreach (var (alias, target) in read.Aliases ?? new Dictionary<string, string>()) aliases[alias] = target;
            }

            var bindings = CBindings.Generate(new CHeader(functions, skipped, aliases, records, enums, macros), Path.GetFileName(path), _toolchain.Target);
            var summary = $"{Path.GetFileName(path)} : {bindings.Converted} fonction(s), {bindings.Types} type(s) et {bindings.Constants} constante(s) convertis, {bindings.Unconverted.Count} déclaration(s) et {bindings.OtherDeclarations} autre(s) pas encore prises en charge";
            var generated = new CLibrary([.. headers.Select(h => h.Path)], includes, library, summary, bindings.Unconverted, bindings.OtherDeclarations);
            _NoteLeftOut(path, generated);
            if (key is not null) NativeCache.Store(_cacheDirectory!, key, summary, bindings.OtherDeclarations, bindings.Unconverted, bindings.Source);
            return _ParseBindings(path, bindings.Source, generated);
        }

        /// <summary>Une notice quand les liaisons laissent quelque chose de côté ; <c>rgl check</c> dit quoi et pourquoi.</summary>
        private void _NoteLeftOut(string path, CLibrary library)
        {
            if ((library.Unconverted?.Count ?? 0) > 0 || library.OtherDeclarations > 0) _diagnostics.Info(library.Summary, path);
        }

        /// <summary>Lit le texte des liaisons comme un fichier de gears ; <see langword="null"/> après une erreur.</summary>
        private (CompilationUnit Unit, CLibrary Library)? _ParseBindings(string path, string source, CLibrary library)
        {
            var before = _Errors();
            var tokens = new Lexer(new SourceText(path, source), _diagnostics).Tokenize();
            var parsed = _Errors() > before ? null : new Parser(tokens, _diagnostics) { AllowNativeCalls = true }.Parse();
            if (parsed is null || _Errors() > before) return null;
            return (parsed, library);
        }

        /// <summary>
        /// Le fichier <c>.rgl.cpp</c> de chaque guard sans corps du fichier : <c>nom.rgl.cpp</c>, dans le dossier du fichier forge. Un fichier absent est une erreur
        /// (E145) ; <see langword="null"/> après une erreur signalée.
        /// </summary>
        private Dictionary<string, string>? _FindNativeSources(CompilationUnit unit, string path)
        {
            Dictionary<string, string> sources = new(StringComparer.Ordinal);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var ok = true;
            foreach (var gear in unit.Statements.OfType<GearDeclaration>().Where(g => g.IsNative))
            {
                var cpp = Path.Combine(directory, gear.Name.Name + ".rgl.cpp");
                if (!File.Exists(cpp))
                {
                    _diagnostics.Error(gear.Name.Span, DiagnosticCodes.NativeComponent, $"« {gear.Name.Name} » est déclaré sans corps, mais {gear.Name.Name}.rgl.cpp est introuvable", "pas de C++",
                        [$"un guard sans corps est implémenté par le fichier {gear.Name.Name}.rgl.cpp, écrit à côté de {Path.GetFileName(path)}"]);
                    ok = false;
                    continue;
                }
                sources[gear.Name.Name] = cpp;
            }
            foreach (var (name, train) in unit.Statements.OfType<TrainBlock>().Select(b => (b.Name?.Name ?? Path.GetFileNameWithoutExtension(path), b.Train))
                .Concat(unit.Train is { } defaultTrain ? [(Path.GetFileNameWithoutExtension(path), defaultTrain)] : []))
            {
                if (train?.NativeReturnType is null) continue;
                var cpp = Path.Combine(directory, name + ".rgl.cpp");
                if (!File.Exists(cpp))
                {
                    _diagnostics.Error(train.Span, DiagnosticCodes.NativeComponent, $"{name}.rgl.cpp est introuvable", "pas de C++", []);
                    ok = false;
                }
                else sources[name] = cpp;
            }
            return ok ? sources : null;
        }

        /// <summary>
        /// Les en-têtes et la bibliothèque statique de la plateforme que décrit l'en-tête d'un fichier <c>forge: native</c> ou <c>forge: descriptor</c>, en chemins complets, relatifs au
        /// dossier du fichier. Un fichier absent est une erreur (E107) ; <see langword="null"/> après une erreur signalée.
        /// </summary>
        private ((SourceSpan Span, string Path)[] Headers, string? Library)? _ResolveHeaderFiles(FileHeader header, string directory)
        {
            List<(SourceSpan Span, string Path)> headers = [];
            foreach (var written in header.Headers ?? [])
            {
                var full = Path.GetFullPath(_Unquote(written.Text), directory);
                if (!File.Exists(full))
                {
                    _diagnostics.Error(written, DiagnosticCodes.ImportNotFound, $"en-tête « {_Unquote(written.Text)} » introuvable", "introuvable",
                        ["le chemin d'un header: est relatif au dossier du fichier forge"]);
                    return null;
                }
                headers.Add((written, full));
            }

            string? library = null;
            var platform = OperatingSystem.IsWindows() ? "windows" : "linux";
            if (header.Library?.FirstOrDefault(e => e.Platform.Name == platform) is { } entry)
            {
                library = Path.GetFullPath(_Unquote(entry.Path.Text), directory);
                if (!File.Exists(library))
                {
                    _diagnostics.Error(entry.Path, DiagnosticCodes.ImportNotFound, $"bibliothèque « {_Unquote(entry.Path.Text)} » introuvable", "introuvable",
                        ["le chemin d'une bibliothèque est relatif au dossier du fichier forge"]);
                    return null;
                }
            }
            return ([.. headers], library);
        }

        /// <summary>
        /// Remplace un fichier <c>forge: descriptor</c> par le fichier de gears que ses descriptors décrivent : un mold à cycle de vie par descriptor, et ses membres en guards sans
        /// corps dont le C++ est l'expression écrite. Les en-têtes ne sont pas inclus dans le programme (c'est du C++) : seule l'unité de chaque membre les inclut.
        /// </summary>
        private (CompilationUnit Unit, CLibrary Library, IReadOnlyDictionary<int, NativeBody> Bodies)? _ExpandDescriptors(CompilationUnit unit, string path)
        {
            var header = unit.Header!;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (_ResolveHeaderFiles(header, directory) is not var (headers, library)) return null;

            var paths = headers.Select(h => h.Path).ToArray();
            var descriptors = unit.Statements.OfType<DescriptorDeclaration>().ToArray();
            var generated = DescriptorBindings.Generate(descriptors, Path.GetFileName(path), paths);

            var before = _Errors();
            var tokens = new Lexer(new SourceText(path, generated.Source), _diagnostics).Tokenize();
            var parsed = _Errors() > before ? null : new Parser(tokens, _diagnostics).Parse();
            if (parsed is null || _Errors() > before) return null;

            var includes = paths.Select(p => Path.GetDirectoryName(p)!).Distinct().ToList();
            return (parsed, new CLibrary([], includes, library, $"{Path.GetFileName(path)} : {descriptors.Length} descriptor(s)"), generated.Bodies);
        }

        /// <summary>Le texte d'un chemin écrit entre guillemets, sans eux.</summary>
        private static string _Unquote(string text) => text.StartsWith("'") ? LiteralDecoder.Raw(text) : LiteralDecoder.Quoted(text, (_, _) => { });

        /// <summary>Charge un fichier (une fois) et ce qu'il importe ; renvoie son nom unique, ou <see langword="null"/> après une erreur.</summary>
        private string? _Load(string path, FileScope scope, ImportDeclaration? import, List<string> chain)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var id = scope.IdOf(name);
            if (_visiting.TryGetValue(id, out var inProgress))
            {
                if (!inProgress || import is null) return inProgress ? null : id;

                _diagnostics.Error(import.Module.Span, DiagnosticCodes.CircularImport, $"imports circulaires : {string.Join(" → ", chain.SkipWhile(c => c != name).Append(name))}", "ferme le cycle",
                    ["le graphe des fichiers est sans cycle : un fichier n'importe que ce qui est écrit sans lui",
                     "sors ce que les deux fichiers partagent dans un troisième fichier"]);
                return null;
            }

            _visiting[id] = true;
            string? result = null;
            var isIndex = scope.Box is not null && name.Equals(IndexName, StringComparison.OrdinalIgnoreCase);

            // Un fichier de la std ne change pas : son arbre, une fois lu sans erreur, sert à toutes les analyses suivantes (la relire et la parser 70 fois coûtait la moitié d'un chargement).
            var cacheable = scope.IsStd && !(_openTexts is not null && _openTexts.ContainsKey(Path.GetFullPath(path)));
            CompilationUnit? parsed;
            if (cacheable && _stdUnits.TryGetValue(path, out var known))
            {
                parsed = known;
            }
            else
            {
                var source = _openTexts is not null && _openTexts.TryGetValue(Path.GetFullPath(path), out var open) ? new SourceText(path, open) : SourceText.FromPath(path, _diagnostics);
                if (source is not null && Path.GetExtension(path).Equals(PaperCompiler.Extension, StringComparison.OrdinalIgnoreCase) && !PaperCompiler.IsVirtual(source.Content))
                    source = PaperCompiler.Compile(source, _root, _diagnostics)?.Source;
                parsed = source is not null ? Parse(source, isEntry: false, isIndex) : null;
                if (cacheable && parsed is not null) _stdUnits[path] = parsed;
            }
            if (parsed is { } unit)
            {
                if (!_nativePolicy.AllowsCpp(path) && (unit.Header?.Kind?.Name is "native" or "descriptor" || unit.Statements.OfType<GearDeclaration>().Any(g => g.IsNative) || unit.Statements.OfType<TrainDeclaration>().Any(t => t.NativeReturnType is not null)))
                {
                    _diagnostics.Error(unit.Span, DiagnosticCodes.NativePolicy, "cette liaison native est interdite par no-cpp", "C++ non autorisé", ["autorise explicitement ce fichier .forge dans cpp-trust"]);
                    _visiting[id] = false;
                    return null;
                }
                // Un fichier forge: native est remplacé par les gears qui enveloppent les fonctions de son en-tête.
                CLibrary? native = null;
                if (unit.Header?.Kind?.Name == "native")
                {
                    if (_ExpandNative(unit, path) is not { } expanded)
                    {
                        _visiting[id] = false;
                        return null;
                    }
                    (unit, native) = expanded;
                }

                // Un guard sans corps est implémenté par le C++ du fichier « nom.rgl.cpp », écrit à côté du fichier forge.
                var nativeSources = _FindNativeSources(unit, path);
                if (nativeSources is { Count: > 0 } && !_nativePolicy.AllowsCpp(path))
                {
                    _diagnostics.Error(unit.Span, DiagnosticCodes.NativePolicy, "cette implémentation C++ est interdite par no-cpp", "C++ non autorisé", []);
                    nativeSources = null;
                }
                if (nativeSources is null)
                {
                    _visiting[id] = false;
                    return null;
                }

                // Un fichier forge: descriptor devient un fichier de gears dont le C++ est écrit dans le fichier forge lui-même.
                IReadOnlyDictionary<int, NativeBody>? nativeBodies = null;
                if (unit.Header?.Kind?.Name == "descriptor")
                {
                    if (_ExpandDescriptors(unit, path) is not { } described)
                    {
                        _visiting[id] = false;
                        return null;
                    }
                    (unit, native, nativeBodies) = described;
                }

                var imports = Visit(ref unit, scope, [.. chain, name]);
                Modules.Add(new SourceModule(id, unit, false, imports, native, nativeSources.Count == 0 ? null : nativeSources, nativeBodies));
                result = id;
            }
            _visiting[id] = false;
            return result;
        }

        /// <summary>Les fichiers qu'un import désigne, avec la portée de chacun ; <see langword="null"/> après une erreur signalée (E107).</summary>
        private List<(string Path, FileScope Scope)>? _Resolve(ImportDeclaration import, FileScope scope)
        {
            var written = import.Module.Name;
            switch (import.Kind)
            {
                case ImportKind.Box:
                    // Les boxes de la std ne se nomment que depuis la std : l'utilisateur écrit use <Nom>.
                    if (!_boxes.TryGetValue(written, out var boxes) && !(scope.IsStd && _stdBoxes.TryGetValue(written, out boxes)))
                        return _NotFound(import, $"box « {written}{BoxSuffix} »", "boxes", _boxes.Keys, "les boxes se cherchent par leur nom dans tout le projet");
                    var index = _Exact(boxes[0].Files, IndexName);
                    if (index is null)
                        return _NotFound(import, $"fichier « {IndexName}{RglProject.ForgeExtension} » dans « {written}{BoxSuffix} »", "fichiers de la box", boxes[0].Files.Keys, "l'index d'une box expose ce qu'elle rend visible");
                    return [(index, boxes[0])];

                case ImportKind.Tools:
                    var parts = written.Split('.');
                    if (!scope.Tools.TryGetValue(parts[0], out var folders))
                        return _NotFound(import, $"dossier « {parts[0]}{ToolsSuffix} »", "dossiers tools", scope.Tools.Keys, "un tools se cherche par son nom dans tout le projet");
                    if (folders.Count > 1)
                        return _Ambiguous(import, parts[0] + ToolsSuffix, folders);

                    var members = scope.Files.Values.SelectMany(f => f)
                        .Where(f => Path.GetFullPath(f).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folders[0])) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        .Order(StringComparer.Ordinal).ToList();
                    if (parts.Length == 1) return [.. members.Select(m => (m, scope))];
                    var file = members.FirstOrDefault(m => Path.GetFileNameWithoutExtension(m) == parts[1]);
                    return file is null
                        ? _NotFound(import, $"fichier « {parts[1]}{RglProject.ForgeExtension} » dans « {parts[0]}{ToolsSuffix} »", "fichiers du dossier", members.Select(Path.GetFileNameWithoutExtension)!, "un import tools Dossier.fichier désigne un fichier du dossier")
                        : [(file, scope)];

                case ImportKind.Std:
                    return _ResolveStd(import, written);

                default:
                    var target = import.Kind == ImportKind.Outside && scope.IsStd ? _std! : import.Kind == ImportKind.Outside ? ProjectScope : scope;
                    var forgeCandidates = target.Files.GetValueOrDefault(written, []);
                    var paperCandidates = target.PaperFiles.GetValueOrDefault(written, []);
                    var candidates = forgeCandidates.Concat(paperCandidates).ToList();
                    if (candidates.Count == 0)
                        return _NotFound(import, $"fichier « {written}{RglProject.ForgeExtension} »", target.Box is null ? "fichiers .forge du projet" : "fichiers .forge de la box", target.Files.Keys.Concat(target.PaperFiles.Keys),
                            _std is not null && _std.Files.ContainsKey(written) || _stdBoxes.ContainsKey(written)
                                ? $"« {written} » est dans la bibliothèque standard : écris use <{written}>;"
                                : target.Box is null ? "un import désigne un fichier par son nom, où qu'il soit dans le projet" : $"dans une box, un import cherche dans la box ; « import [..] {written}; » cherche hors d'elle");
                    var exact = candidates.Count == 1 && Path.GetFileNameWithoutExtension(candidates[0]) == written ? candidates[0] : null;
                    if (exact is not null) return [(exact, target)];

                    _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound,
                        $"le fichier s'appelle « {Path.GetFileName(candidates[0])} », pas « {written} »", "mauvaise casse",
                        ["les noms de fichiers sont sensibles à la casse"]);
                    return null;
            }
        }

        /// <summary>
        /// <c>use &lt;Nom&gt;;</c> : le fichier de la std de ce nom, ou sa box (son index : seul l'index d'une box est visible), sans que l'utilisateur ait
        /// à savoir lequel. <c>use &lt;std&gt;;</c> désigne tous les fichiers de la std et l'index de chaque box, à plat.
        /// </summary>
        private List<(string Path, FileScope Scope)>? _ResolveStd(ImportDeclaration import, string written)
        {
            if (_std is null) return _NotFound(import, "bibliothèque standard", "std", [], "la std est embarquée dans le compilateur : elle n'a pas pu être chargée");

            var boxIndexes = _stdBoxes.Values.SelectMany(b => b).Select(b => (Path: _Exact(b.Files, IndexName), Scope: b)).Where(b => b.Path is not null).Select(b => (b.Path!, b.Scope));
            if (written == "std")
                return [.. _std.Files.Values.Where(l => l.Count == 1).Select(l => (l[0], _std)).Concat(boxIndexes).OrderBy(f => f.Item1, StringComparer.Ordinal)];

            var file = _Exact(_std.Files, written);
            var box = _stdBoxes.GetValueOrDefault(written)?.FirstOrDefault() is { } found && _Exact(found.Files, IndexName) is { } indexPath ? (indexPath, found) : default;
            if (file is not null && box != default)
            {
                _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"la std a un fichier et une box nommés « {written} »", "ambigu", ["un nom de la std désigne une seule chose"]);
                return null;
            }
            if (file is not null) return [(file, _std)];
            if (box != default) return [(box.Item1, box.Item2)];

            var names = _std.Files.Keys.Concat(_stdBoxes.Keys);
            return _NotFound(import, $"« {written} » dans la std", "la std contient", names, "use <Nom>; importe un fichier ou une box de la bibliothèque standard");
        }

        /// <summary>Le fichier de ce nom exact (casse comprise) dans la portée, s'il est unique.</summary>
        private static string? _Exact(Dictionary<string, List<string>> files, string name) =>
            files.TryGetValue(name, out var candidates) && candidates.Count == 1 && Path.GetFileNameWithoutExtension(candidates[0]) == name ? candidates[0] : null;

        private List<(string, FileScope)>? _NotFound(ImportDeclaration import, string what, string listing, IEnumerable<string> available, string help)
        {
            var names = available.Order(StringComparer.Ordinal).ToArray();
            _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"{what} introuvable", "introuvable",
                [help, names.Length == 0 ? "il n'y en a aucun ici" : $"{listing} : {string.Join(", ", names)}"]);
            return null;
        }

        private List<(string, FileScope)>? _Ambiguous(ImportDeclaration import, string what, List<string> paths)
        {
            _diagnostics.Error(import.Module.Span, DiagnosticCodes.ImportNotFound, $"plusieurs dossiers portent le nom « {what} »", "ambigu",
                [$"dossiers : {string.Join(", ", paths.Select(p => Path.GetRelativePath(_root, p)))}"]);
            return null;
        }
    }
}
