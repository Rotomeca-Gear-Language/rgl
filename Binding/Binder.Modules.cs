using Rgl.AST;
using Rgl.AST.Datasets;
using Rgl.AST.Enums;
using Rgl.AST.Extensions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Ingots;
using Rgl.AST.Interfaces;
using Rgl.AST.Notes;
using Rgl.AST.Molds;
using Rgl.AST.Precasts;
using Rgl.AST.Statements;
using Rgl.AST.Train;
using Rgl.AST.Variables;
using Rgl.AST.Expressions;
using Rgl.Diagnostics;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Train;
using Rgl.Text;
using Rotomeca.Core.Collections;

namespace Rgl.Binding;

/// <summary>
/// Un fichier source à lier : son nom (celui d'un import, sans extension), son arbre, et s'il est le fichier
/// d'entrée du projet, le <c>.rgl</c>.
/// </summary>
/// <param name="Name">Le nom du fichier sans extension, tel qu'un import l'écrit ; ignoré pour le fichier d'entrée.</param>
/// <param name="Unit">L'arbre du fichier, sans erreur de syntaxe.</param>
/// <param name="IsEntry">Vrai pour le fichier <c>.rgl</c> d'entrée, qui contient le train d'entrée.</param>
/// <param name="Imports">
/// Pour chaque import du fichier, les noms uniques des fichiers qu'il désigne (un seul, sauf <c>import tools Dossier;</c>) ; un import
/// absent désigne le fichier de ce nom.
/// </param>
internal sealed record SourceModule(string Name, CompilationUnit Unit, bool IsEntry, IReadOnlyDictionary<ImportDeclaration, string[]>? Imports = null, Native.CLibrary? Native = null,
    IReadOnlyDictionary<string, string>? NativeSources = null, IReadOnlyDictionary<int, IR.Symbols.NativeBody>? NativeBodies = null);

/// <summary>
/// Les fichiers d'un programme. Chaque déclaration a un nom unique dans le programme : le sien dans le fichier
/// d'entrée, <c>Fichier.nom</c> dans un fichier importé, <c>Fichier.train.nom</c> et <c>Fichier.train.nom</c>
/// dans un train nommé. Un fichier ne voit que ses propres noms (son train nommé, puis ses déclarations communes), ce qu'il
/// importe, et les types d'erreur standard ; il est lié après tous ceux qu'il importe.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Ce que le binder retient d'un fichier pour ceux qui l'importent et pour résoudre ses propres noms.</summary>
    private sealed class ModuleInfo(string name, bool isEntry)
    {
        /// <summary>Le nom du fichier sans extension.</summary>
        public string Name { get; } = name;

        /// <summary>Vrai pour le fichier d'entrée, dont les noms n'ont pas de préfixe.</summary>
        public bool IsEntry { get; } = isEntry;

        /// <summary>Ce qui précède le nom d'une déclaration du fichier dans le programme.</summary>
        public string Prefix => IsEntry ? "" : Name + ".";

        /// <summary>Le nom du train par défaut du fichier : <c>train</c> étant un mot-clé, aucune déclaration ne le porte.</summary>
        public string DefaultTrainKey => Prefix + "train";

        /// <summary>Les noms importés un à un (<c>only</c>) : le nom local, puis le nom unique de ce qu'il désigne.</summary>
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

        /// <summary>Les fichiers importés en entier, accessibles par leur nom.</summary>
        public Dictionary<string, ModuleInfo> Imported { get; } = new(StringComparer.Ordinal);

        /// <summary>La portée du fichier : ses constantes.</summary>
        public Scope Scope { get; } = new(null);

        /// <summary>
        /// Pour l'index d'une box, ou les noms d'un dossier tools : les noms visibles de l'extérieur et le nom unique de ce qu'ils
        /// désignent ; <see langword="null"/> pour un fichier ordinaire, dont tout le premier niveau est visible.
        /// </summary>
        public Dictionary<string, string>? Exports { get; set; }

        /// <summary>Les constantes qu'<see cref="Exports"/> rend aussi visibles.</summary>
        public Dictionary<string, ScopeEntry>? ExportedConstants { get; set; }

        /// <summary>
        /// Pour l'index d'une box : les fichiers que l'index exporte tels quels (<c>export Fichier;</c>), dont les noms restent
        /// qualifiés (<c>Box.Fichier.nom</c>) ; <see langword="null"/> sinon.
        /// </summary>
        public Dictionary<string, ModuleInfo>? Modules { get; set; }

        /// <summary>Les extensions du fichier : elles ne sont vues que de lui.</summary>
        public List<Extension> Extensions { get; } = [];

        /// <summary>Vrai pour un fichier <c>forge: extend</c> : ses extensions s'appliquent à tout le programme.</summary>
        public bool IsExtensionFile { get; init; }

        /// <summary>Le namespace que le fichier déclare (<c>namespace Mon/Ns;</c>) ; <see langword="null"/> sans.</summary>
        public string? NamespaceName { get; set; }

        /// <summary>Les gears des extensions génériques écrites dans ce fichier (<c>extend&lt;T&gt; [T] { … }</c>), vues de lui seul.</summary>
        public List<GenericExtensionGear> GenericExtensions { get; } = [];

        /// <summary>Les gears des extensions écrits <c>Type.nom</c> dans le fichier, et le nom unique de ce qu'ils désignent.</summary>
        public Dictionary<string, string> ExtensionKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Pour un fichier ordinaire qui écrit au moins un <c>export</c> : les noms qu'il exporte ; <see langword="null"/> s'il
        /// n'en écrit aucun, et tout son premier niveau est alors exporté.
        /// </summary>
        public HashSet<string>? Visible { get; set; }

        /// <summary>Vrai si <paramref name="name"/>, une déclaration du fichier, est visible de ceux qui l'importent.</summary>
        public bool Exposes(string name) => Visible is null || Visible.Contains(name);
    }

    /// <summary>
    /// Où se trouvent les noms en cours de liaison : leurs préfixes, du plus local au plus général (un train nommé,
    /// puis le fichier), et la portée des constantes.
    /// </summary>
    private sealed record Namespace(ModuleInfo Module, string[] Prefixes, Scope Scope);

    private readonly Dictionary<string, ModuleInfo> _modules = new(StringComparer.Ordinal);

    /// <summary>Les noms de la portée courante.</summary>
    private Namespace _ns = null!;

    /// <summary>Ce que désigne chaque nom unique déclaré : sa nature, pour dire ce qui le prend déjà.</summary>
    private readonly Dictionary<string, (string What, string Category)> _declared = new(StringComparer.Ordinal);

    /// <summary>Les trains du fichier en cours qui ne sont pas encore liés : un appel qui les vise est écrit au mauvais endroit.</summary>
    private readonly HashSet<string> _pendingTrains = new(StringComparer.Ordinal);

    /// <summary>Le nom unique que prend dans le programme une déclaration de la portée courante.</summary>
    private string _DeclKey(string name) => _ns.Prefixes[0] + name;

    /// <summary>
    /// Cherche le nom écrit <paramref name="name"/> dans <paramref name="map"/> : d'abord ce que déclare la portée courante,
    /// puis le fichier, ce qu'il importe un à un, le train par défaut d'un fichier importé (<c>Commandes(…)</c>) et,
    /// pour un fichier importé, les types d'erreur standard. Un nom qualifié (<c>Commandes.ajouter</c>) vise un fichier
    /// importé en entier.
    /// </summary>
    private bool _TryFind<T>(Dictionary<string, T> map, string name, out T value)
    {
        value = default!;
        if (name.Contains('.'))
        {
            if (_ns.Module.ExtensionKeys.TryGetValue(name, out var extensionKey) && map.TryGetValue(extensionKey, out value!)) return true;

            var parts = name.Split('.');
            // Type.f : un gear d'une extension d'un type du fichier, ou importé un à un.
            if (parts.Length == 2)
            {
                foreach (var prefix in _ns.Prefixes)
                    if (map.TryGetValue($"{prefix}{parts[0]}::{parts[1]}", out value!)) return true;
                if (_ns.Module.Aliases.TryGetValue(parts[0], out var aliasedType) && map.TryGetValue($"{aliasedType}::{parts[1]}", out value!)) return true;
            }

            if (!_ns.Module.Imported.TryGetValue(parts[0], out var qualifier)) return false;
            // Un fichier que l'index d'une box exporte tel quel : Box.Fichier.nom.
            var at = 1;
            while (at < parts.Length - 1 && qualifier.Modules?.GetValueOrDefault(parts[at]) is { } inner)
            {
                qualifier = inner;
                ++at;
            }

            var member = parts[^1];
            // Fichier.Type.f : un gear d'une extension d'un type que ce fichier déclare et exporte.
            if (at == parts.Length - 2)
            {
                var typeKey = qualifier.Exports is { } typeExports ? typeExports.GetValueOrDefault(parts[at]) : qualifier.Exposes(parts[at]) ? qualifier.Prefix + parts[at] : null;
                return typeKey is not null && map.TryGetValue($"{typeKey}::{member}", out value!);
            }
            if (at != parts.Length - 1) return false;
            // L'index d'une box, un dossier tools : seuls les noms exportés sont visibles ; un fichier ordinaire n'expose que ses exports.
            return qualifier.Exports is { } exports
                ? exports.TryGetValue(member, out var exportedKey) && map.TryGetValue(exportedKey, out value!)
                : qualifier.Exposes(member) && map.TryGetValue(qualifier.Prefix + member, out value!);
        }

        foreach (var prefix in _ns.Prefixes)
            if (map.TryGetValue(prefix + name, out value!)) return true;
        if (_ns.Module.Aliases.TryGetValue(name, out var key) && map.TryGetValue(key, out value!)) return true;
        if (_ns.Module.Imported.TryGetValue(name, out var imported) && map.TryGetValue(imported.DefaultTrainKey, out value!)) return true;
        // Les types d'erreur standard sont visibles de tous les fichiers, sous leur nom court.
        if (_IsStandardError(name) && map.TryGetValue("std.Errors." + name, out value!)) return true;
        return !_ns.Module.IsEntry && map.TryGetValue(name, out value!);
    }

    /// <summary>Les noms que la portée courante voit dans <paramref name="map"/>, pour les messages.</summary>
    private IEnumerable<string> _VisibleNames<T>(Dictionary<string, T> map) =>
        _ns.Prefixes.SelectMany(prefix => map.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('.', prefix.Length) < 0 && !k.Contains("::")).Select(k => k[prefix.Length..]))
            .Concat(_ns.Module.Aliases.Where(a => map.ContainsKey(a.Value)).Select(a => a.Key))
            .Distinct().Order(StringComparer.Ordinal);

    /// <summary>
    /// Réserve <paramref name="name"/> dans la portée courante. Trains, gears, molds, enums, ingots, constantes et imports
    /// partagent un seul espace de noms : un nom qui désigne déjà autre chose, ici ou dans une portée englobante, est
    /// E103. Deux déclarations de même nature sont signalées par leur propre règle (E055, E075, E044).
    /// </summary>
    /// <param name="name">Le nom déclaré.</param>
    /// <param name="what">Ce qui le déclare, pour le message : « un gear », « une constante »…</param>
    /// <param name="category">Sa nature, pour reconnaître deux déclarations de même nature.</param>
    /// <param name="reportSameCategory">Vrai pour signaler aussi une déclaration de même nature (deux imports du même nom).</param>
    /// <returns>Vrai si le nom était libre.</returns>
    private bool _ClaimName(Identifier name, string what, string category, bool reportSameCategory = false) =>
        _ClaimName(name.Name, name.Span, what, category, reportSameCategory);

    /// <inheritdoc cref="_ClaimName(Identifier, string, string, bool)"/>
    /// <param name="text">Le nom déclaré.</param>
    /// <param name="span">Où le signaler.</param>
    private bool _ClaimName(string text, SourceSpan span, string what, string category, bool reportSameCategory = false)
    {
        foreach (var prefix in _ns.Prefixes)
        {
            if (!_declared.TryGetValue(prefix + text, out var existing)) continue;

            // Un nom que l'utilisateur déclare masque celui qu'il a pris dans la std : le sien l'emporte, avec un avertissement.
            if (category != "import" && _stdImportKeys.Remove(prefix + text))
            {
                _diagnostics.Warning(span, DiagnosticCodes.StdNameMasked, $"« {text} » masque le nom du même nom pris dans la std", "masque la std",
                    [$"ce fichier utilise sa propre définition ; pour la std, écris std.{text} ou renomme-la : use <Fichier> only {{{text} as autre}};"]);
                _declared[prefix + text] = (what, category);
                return true;
            }
            // Deux déclarations de même nature dans la même portée sont signalées par leur propre règle.
            if (existing.Category == category && !reportSameCategory && prefix == _ns.Prefixes[0]) return true;

            _Error(span, DiagnosticCodes.NameInUse, $"« {text} » désigne déjà {existing.What} dans ce fichier", "nom déjà pris",
                "un nom ne désigne qu'une chose dans un fichier : trains, gears, molds, enums, ingots, constantes et imports partagent le même espace de noms",
                $"« as » renomme un import qui gêne : import Fichier only {{{text} as autre}};");
            return false;
        }

        _declared[_DeclKey(text)] = (what, category);
        return true;
    }

    /// <summary>
    /// Le nom unique d'un fichier importé accédé par <c>Fichier.nom</c> dans <paramref name="expression"/>, si
    /// <c>Fichier</c> est un fichier importé et pas une variable ou un paramètre ; sinon <see langword="null"/>.
    /// </summary>
    private string? _QualifiedName(IExpression expression) =>
        expression is MemberAccessExpression { IsSafe: false, Target: var target, Member.Name: var member } && _ModulePath(target) is { } module
            ? module + "." + member
            : null;

    /// <summary>
    /// Le chemin d'un fichier importé écrit dans <paramref name="expression"/> : <c>Fichier</c>, ou <c>Box.Fichier</c> quand l'index
    /// d'une box exporte le fichier tel quel ; <see langword="null"/> si ce n'est pas un fichier (une variable, un paramètre…).
    /// </summary>
    private string? _ModulePath(IExpression expression) => expression switch
    {
        NameExpression { Name.Name: var module } when !_IsValueName(module) && _ns.Module.Imported.ContainsKey(module) => module,
        MemberAccessExpression { IsSafe: false, Target: var target, Member.Name: var member }
            when _ModulePath(target) is { } parent && _FindModule(parent + "." + member) is not null => parent + "." + member,
        _ => null,
    };

    /// <summary>Le fichier importé que désigne le chemin <paramref name="path"/> (<c>Fichier</c> ou <c>Box.Fichier</c>), s'il y en a un.</summary>
    private ModuleInfo? _FindModule(string path)
    {
        var parts = path.Split('.');
        if (!_ns.Module.Imported.TryGetValue(parts[0], out var module)) return null;
        for (var i = 1; i < parts.Length; ++i)
        {
            if (module.Modules?.GetValueOrDefault(parts[i]) is not { } inner) return null;
            module = inner;
        }
        return module;
    }

    #region Precasts

    /// <summary>Les constantes que des precasts ont données au fichier en cours : leur valeur doit être connue à la compilation.</summary>
    private readonly HashSet<VarDeclaration> _precastConstants = [];

    /// <summary>
    /// Un precast est une constante du fichier, de type mold, importable comme les autres : <paramref name="statements"/> avec chaque
    /// precast remplacé par <c>const Type nom: new Type { … }</c>. Un precast qui déclare son mold a pour type le mold
    /// anonyme de ses champs.
    /// </summary>
    private IReadOnlyList<IStatement> _DesugarPrecasts(IReadOnlyList<IStatement> statements)
    {
        if (!statements.Any(s => s is PrecastDeclaration or DataConstant or DatasetDeclaration or DatasetExtension)) return statements;

        var result = new List<IStatement>();
        foreach (var statement in statements)
        {
            // Une constante qui vient d'un fichier de données (import json) se lie comme un precast : donnée résolue, écrite si on la lit.
            if (statement is DataConstant data)
            {
                _precastConstants.Add(data.Declaration);
                result.Add(data.Declaration);
            }
            else if (statement is DatasetDeclaration dataset) _DesugarDataset(dataset, result);
            // Une extension se fond dans son dataset avant la liaison (SourceGraph) : en rester une, c'est qu'aucun fichier importé ne le déclare.
            else if (statement is DatasetExtension extension)
                _Error(extension.Name.Span, DiagnosticCodes.InvalidDataset, $"le dataset « {extension.Name.Name} » n'est déclaré par aucun fichier que celui-ci importe", "dataset introuvable",
                    "importe le fichier qui déclare le dataset : import Moves;");
            else if (statement is not PrecastDeclaration precast) result.Add(statement);
            else if (_PrecastConstant(precast) is { } constant)
            {
                _precastConstants.Add(constant);
                result.Add(constant);
            }
        }
        return result;
    }

    private VarDeclaration? _PrecastConstant(PrecastDeclaration precast)
    {
        ITypeNode type;
        IReadOnlyList<MoldFieldValue> values;
        if (precast.Type is { } written) (type, values) = (written, precast.Values);
        else
        {
            if (precast.Fields.FirstOrDefault(f => f.Default is null) is { } missing)
            {
                _Error(missing.Name.Span, DiagnosticCodes.InvalidPrecast, $"le champ « {missing.Name.Name} » du precast « {precast.Name.Name} » n'a pas de valeur", "valeur manquante",
                    $"un precast est entièrement résolu à la compilation : écris-la, par exemple : {missing.Type.Span.Text} {missing.Name.Name}: …;");
                return null;
            }
            type = new AST.Types.AnonymousMoldType(precast.Span, [.. precast.Fields.Select(f => (IMoldMember)new MoldField(f.Span, f.Type, f.Name, null))]);
            values = [.. precast.Fields.Select(f => new MoldFieldValue(f.Span, f.Name, f.Default!, false))];
        }
        return new VarDeclaration(precast.Span, true, false, type, precast.Name, new MoldExpression(precast.Span, type, [.. values.Cast<IMoldEntry>()]));
    }

    /// <summary>
    /// Retient la variable d'un precast, et signale un precast qui n'est pas une donnée résolue à la compilation : des littéraux, des
    /// constantes, des molds, des tableaux (<c>[string] data: ['a', 'b']</c>), des dictionnaires et des tuples, jamais un calcul.
    /// Constexpr quand le C++ le permet ; un tableau dynamique alloue, il est construit une fois au démarrage.
    /// </summary>
    private void _CheckPrecast(VarDeclaration constant, IrVariableDeclaration declared)
    {
        _precastVariables.Add(declared.Variable);
        if (declared.Variable.IsCompileTime || _IsPrecastData(declared.Value)) return;
        _Error(constant.Expression.Span, DiagnosticCodes.InvalidPrecast, $"la valeur du precast « {constant.Name.Name} » n'est pas résolue à la compilation", "calculée à l'exécution",
            "un precast est une donnée : des littéraux, des constantes, des molds, des tableaux, des dictionnaires et des tuples, sans appel");
    }

    #endregion

    #region Liaison des fichiers

    /// <summary>
    /// Lie un fichier : ses imports, ses déclarations communes, ses trains nommés dans l'ordre, puis son train par défaut.
    /// Les constantes et les gears vont dans <paramref name="constants"/> et <paramref name="gears"/>.
    /// </summary>
    /// <returns>Le train d'entrée pour le fichier d'entrée ; <see langword="null"/> sinon, ou après une erreur.</returns>
    private IrEntryTrain? _BindModule(SourceModule module, RArray<IrStatement> constants, RArray<IrGear> gears)
    {
        var unit = module.Unit;
        _nativeSources = module.NativeSources;
        _nativeBodies = module.NativeBodies;
        if (module.NativeBodies is not null) _nativeMetadata[module.Unit.Span.Source] = module.NativeBodies;
        var info = new ModuleInfo(module.Name, module.IsEntry)
        {
            NamespaceName = unit.Statements.OfType<NamespaceDeclaration>().FirstOrDefault()?.Name,
            IsExtensionFile = unit.Header?.Kind.Name == "extend",
        };
        if (!module.IsEntry) _modules[module.Name] = info;
        _EnterNamespace(new Namespace(info, [info.Prefix], info.Scope));

        _ProcessImports(module, info);
        _ImplicitStd(module, info);
        _ProcessExports(unit, info);

        // Les trains du fichier, avant toute déclaration : un gear ne peut pas porter leur nom.
        _pendingTrains.Clear();
        var blocks = unit.Statements.OfType<TrainBlock>().ToArray();
        foreach (var named in blocks.Where(b => b.Name is not null))
        {
            if (_ClaimName(named.Name!, "un train", "train")) _pendingTrains.Add(info.Prefix + named.Name!.Name);
        }
        if (!module.IsEntry && (unit.Train is not null || blocks.Any(b => b.Name is null))) _pendingTrains.Add(info.DefaultTrainKey);

        _DeclareAnnotations(unit.Statements.OfType<AnnotationDeclaration>());
        foreach (var statement in unit.Statements) _CheckNotes(statement);
        _BindDeclarations(_DesugarPrecasts(unit.Statements), constants, gears);
        var fileNamespace = _ns;

        foreach (var block in blocks.Where(b => b.Name is not null))
            _BindTrainBlock(info, block, constants, gears);

        IrEntryTrain? entry = null;
        if (module.IsEntry) entry = unit.Train is { } train ? _BindEntryTrain(train) : null;
        else if (unit.Train is { } defaultTrain)
        {
            _EnterNamespace(fileNamespace);
            if (_BindCallableTrain(defaultTrain, info.Name, info.DefaultTrainKey, defaultTrain.Span) is { } gear) gears.Push(gear);
        }
        else if (blocks.FirstOrDefault(b => b.Name is null) is { } defaultBlock)
            _BindTrainBlock(info, defaultBlock, constants, gears);

        _EnterNamespace(fileNamespace);
        _ApplyExports(unit, info);
        if (module.Name == "std.Errors") _TakeStandardErrors();
        if (module.Name == "std.Xml") _xmlNode = _molds.GetValueOrDefault("std.Xml.XmlNode");
        if (module.Name == "std.Process")
        {
            _processResult = _molds.GetValueOrDefault("std.Process.ProcessResult");
            _spawnError = _molds.GetValueOrDefault("std.Process.SpawnError");
        }
        return entry;
    }

    /// <summary>Fait de <paramref name="ns"/> la portée courante des noms et des constantes.</summary>
    private void _EnterNamespace(Namespace ns)
    {
        _ns = ns;
        _scope = ns.Scope;
        _fileScope = ns.Scope;
    }

    /// <summary>
    /// Lie un train nommé (ou le bloc du train par défaut) : ses déclarations locales, dans une portée dont ce train
    /// est seul à voir les noms, puis le train.
    /// </summary>
    private void _BindTrainBlock(ModuleInfo info, TrainBlock block, RArray<IrStatement> constants, RArray<IrGear> gears)
    {
        var isDefault = block.Name is null;
        var name = isDefault ? info.Name : block.Name!.Name;
        var key = isDefault ? info.DefaultTrainKey : info.Prefix + name;
        var local = isDefault ? "train." : name + ".";
        var parent = _ns;
        var scope = new Scope(info.Scope);
        _EnterNamespace(new Namespace(info, [info.Prefix + local, .. parent.Prefixes], scope));

        _BindDeclarations(block.Declarations, constants, gears);
        if (block.Train is null)
        {
            _Error(block.Span, DiagnosticCodes.MissingDefaultTrain, isDefault ? "le bloc du train par défaut ne contient aucun train" : $"le train « {name} » ne contient aucun train",
                "train manquant", "un bloc de train se termine par son train : train(params) étape |> … ;");
            _pendingTrains.Remove(key);
        }
        else if (_BindCallableTrain(block.Train, name, key, block.Name?.Span ?? block.Train.Span) is { } gear) gears.Push(gear);

        _EnterNamespace(parent);
    }

    /// <summary>
    /// Les imports d'un fichier : <c>import X;</c> donne l'accès qualifié et le train par défaut ; <c>import X only {a as b, c};</c>
    /// rend visibles les noms choisis ; <c>import box B;</c> donne l'accès qualifié à ce que l'index de la box exporte ;
    /// <c>import tools T;</c> aux noms de tous les fichiers du dossier, <c>import tools T.f;</c> aux noms d'un fichier, en accès direct.
    /// Un nom inconnu du fichier importé est E109.
    /// </summary>
    private void _ProcessImports(SourceModule module, ModuleInfo info)
    {
        foreach (var import in module.Unit.Imports)
        {
            var ids = module.Imports is not null && module.Imports.TryGetValue(import, out var found) ? found : [import.Module.Name];
            var targets = ids.Select(id => _modules.GetValueOrDefault(id)).OfType<ModuleInfo>().ToArray();
            if (targets.Length != ids.Length) continue;

            // import tools T.f : les noms d'un seul fichier, en accès direct.
            if (import.Kind == ImportKind.Tools && import.Module.Name.Contains('.'))
            {
                if (import.Only is { } picked)
                    foreach (var c in picked) _ImportName(info, targets[0], c.Name.Span, c.LocalName.Name, c.Name.Name);
                else
                    foreach (var name in _PublicNames(targets[0]).ToArray()) _ImportName(info, targets[0], import.Module.Span, name, name);
                continue;
            }

            // import tools T : les noms de tous les fichiers du dossier, comme ceux d'un seul fichier.
            var isStd = import.Kind == ImportKind.Std && import.Module.Name == "std" && targets.Length > 1;
            var target = import.Kind == ImportKind.Tools || isStd ? _FlattenTools(import.Module.Name, import.Module.Span, targets) : targets.Length == 1 ? targets[0] : null;
            if (target is null) continue;

            if (import.Only is not { } only)
            {
                var local = import.Alias ?? import.Module;
                if (_ClaimName(local, "un import", "import", reportSameCategory: true))
                {
                    info.Imported[local.Name] = target;
                    if (import.Kind == ImportKind.Std) _stdImportKeys.Add(_DeclKey(local.Name));
                }
                continue;
            }

            foreach (var chosen in only)
            {
                _ImportName(info, target, chosen.Name.Span, chosen.LocalName.Name, chosen.Name.Name);
                if (import.Kind == ImportKind.Std) _stdImportKeys.Add(_DeclKey(chosen.LocalName.Name));
            }
        }
    }

    /// <summary>Les noms (avec leur préfixe) qu'un fichier a pris dans la std par <c>use &lt;…&gt;</c> : une déclaration du fichier peut les masquer.</summary>
    private readonly HashSet<string> _stdImportKeys = new(StringComparer.Ordinal);

    /// <summary>Les noms de tous les fichiers de la std, à plat, pour l'accès <c>std.nom</c> : calculés une fois.</summary>
    private ModuleInfo? _stdNames;

    /// <summary>
    /// La std est importée dans tous les fichiers hors d'elle : <c>std.nom</c> y désigne un nom de n'importe lequel de ses fichiers (un fichier
    /// ou un import du projet qui prend le nom <c>std</c> l'emporte). <c>import std only {a, b as c};</c> les rend visibles tels quels.
    /// </summary>
    private void _ImplicitStd(SourceModule module, ModuleInfo info)
    {
        if (module.Name.StartsWith("std.", StringComparison.Ordinal) || info.Imported.ContainsKey("std")) return;

        // Les fichiers de la std et l'index de chacune de ses boxes.
        var files = _modules.Values.Where(m => m.Name.StartsWith("std.", StringComparison.Ordinal) && (m.Name.IndexOf('.', 4) < 0 || m.Name.EndsWith(".box.index", StringComparison.Ordinal))).ToArray();
        if (files.Length == 0) return;
        info.Imported["std"] = _stdNames ??= _FlattenTools("std", module.Unit.Span, files);
    }

    /// <summary>Rend visible dans <paramref name="info"/>, sous le nom <paramref name="local"/>, le nom <paramref name="name"/> de <paramref name="target"/> (E109 s'il n'y est pas).</summary>
    private void _ImportName(ModuleInfo info, ModuleInfo target, SourceSpan span, string local, string name)
    {
        var key = _MemberKey(target, name);
        // Un dataset s'importe avec sa table : le nom désigne les deux.
        var constant = key is null || _datasets.ContainsKey(key) ? _MemberConstant(target, name) : null;
        if (key is null && constant is null)
        {
            // Un fichier que l'index d'une box exporte tel quel s'importe par son nom.
            if (target.Modules?.GetValueOrDefault(name) is { } module)
            {
                if (_ClaimName(local, span, "un import", "import", reportSameCategory: true)) info.Imported[local] = module;
                return;
            }

            var hidden = target.Exports is null && _DeclaresName(target, name);
            var names = _PublicNames(target).Concat(target.Modules?.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal);
            _Error(span, DiagnosticCodes.UnknownImportedName,
                hidden ? $"« {name} » n'est pas exporté par « {target.Name} »" : $"« {target.Name} » ne déclare pas « {name} »", hidden ? "pas exporté" : "inconnu",
                hidden ? $"écris export devant sa déclaration dans {target.Name}, ou ajoute-le à un export {{…}};" : $"noms de {target.Name} : {string.Join(", ", names)}");
            return;
        }

        if (!_ClaimName(local, span, "un import", "import", reportSameCategory: true)) return;
        if (key is not null) info.Aliases[local] = key;
        if (constant is not null) info.Scope.Import(local, constant.Value);
    }

    /// <summary>
    /// Ce que <c>import tools T;</c> rend visible : les noms déclarés par chacun des fichiers du dossier, comme ceux d'un
    /// seul fichier. Deux fichiers qui déclarent le même nom sont E103.
    /// </summary>
    private ModuleInfo _FlattenTools(string toolsName, SourceSpan span, ModuleInfo[] files)
    {
        var all = new ModuleInfo(toolsName, false) { Exports = new(StringComparer.Ordinal), ExportedConstants = new(StringComparer.Ordinal) };
        foreach (var file in files)
        {
            foreach (var name in _PublicNames(file))
            {
                // Le même nom venu de deux fichiers n'est pas un doublon s'il désigne la même déclaration (une box exportée par une autre).
                var duplicate = _MemberKey(file, name) is { } key ? !all.Exports!.TryAdd(name, key) && all.Exports[name] != key
                    : !all.ExportedConstants!.TryAdd(name, _MemberConstant(file, name)!.Value) && !all.ExportedConstants[name].Equals(_MemberConstant(file, name)!.Value);
                if (duplicate)
                    _Error(span, DiagnosticCodes.NameInUse, $"plusieurs fichiers de « {toolsName} » déclarent « {name} »", "nom en double",
                        "les noms d'un dossier tools sont uniques : importe un fichier à la fois (import tools Dossier.fichier;) ou renomme l'un d'eux");
            }
        }
        return all;
    }

    /// <summary>Le nom unique de la déclaration (gear, train, mold, enum, ingot) de ce nom dans <paramref name="module"/> ; <see langword="null"/> s'il n'en a pas.</summary>
    private string? _MemberKey(ModuleInfo module, string name)
    {
        if (module.Exports is { } exports) return exports.GetValueOrDefault(name);
        var key = module.Prefix + name;
        return module.Exposes(name) && (_gears.ContainsKey(key) || _molds.ContainsKey(key) || _enums.ContainsKey(key) || _ingots.ContainsKey(key)
            || _genericGears.ContainsKey(key) || _genericMolds.ContainsKey(key) || _datasets.ContainsKey(key)) ? key : null;
    }

    /// <summary>La constante de ce nom dans <paramref name="module"/> ; <see langword="null"/> s'il n'en a pas.</summary>
    private static ScopeEntry? _MemberConstant(ModuleInfo module, string name)
    {
        if (module.Exports is not null) return module.ExportedConstants!.TryGetValue(name, out var exported) ? exported : null;
        return module.Scope.DeclaresHere(name) && module.Exposes(name) ? module.Scope[name].Value : null;
    }

    /// <summary>Les noms qu'un fichier rend visibles : ses déclarations de premier niveau et ses constantes, ou ce que son index exporte.</summary>
    private IEnumerable<string> _PublicNames(ModuleInfo module)
    {
        if (module.Exports is { } exports) return exports.Keys.Concat(module.ExportedConstants!.Keys);

        return _gears.Keys.Concat(_molds.Keys).Concat(_enums.Keys).Concat(_ingots.Keys).Concat(_genericGears.Keys).Concat(_genericMolds.Keys).Concat(_datasets.Keys)
            .Where(k => k.StartsWith(module.Prefix, StringComparison.Ordinal) && k.IndexOf('.', module.Prefix.Length) < 0 && k != module.DefaultTrainKey && !k.Contains("::"))
            .Select(k => k[module.Prefix.Length..])
            .Concat(module.Scope.Names)
            .Distinct()
            .Where(module.Exposes);
    }

    /// <summary>
    /// Les exports d'un fichier ordinaire, une fois ses déclarations liées : s'il écrit au moins un <c>export</c>, ceux qui
    /// l'importent ne voient que les noms exportés (E113 pour un nom que le fichier ne déclare pas, ou un export réservé à
    /// l'index d'une box) ; sinon tout son premier niveau est exporté. Seuls les noms vus par le compilateur changent : le C++ n'en sait rien.
    /// </summary>
    private void _ApplyExports(CompilationUnit unit, ModuleInfo info)
    {
        if (info.IsEntry || unit.Header?.Kind?.Name == "index") return;
        var exports = unit.Statements.OfType<ExportDeclaration>().SelectMany(e => e.Names).ToArray();
        if (exports.Length == 0) return;

        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in exports)
        {
            var name = export.Name.Name;
            if (export.Kind != ExportKind.Name)
                _Error(export.Span, DiagnosticCodes.UnknownExport, $"« {export.Span.Text} » ne s'écrit que dans l'index d'une box", "réservé à l'index",
                    "un fichier ordinaire exporte ses propres déclarations : export {nom};");
            else if (!visible.Add(name))
                _Error(export.Span, DiagnosticCodes.NameInUse, $"« {name} » est déjà exporté", "en double");
            else if (!_DeclaresName(info, name))
                _Error(export.Span, DiagnosticCodes.UnknownExport, $"« {name} » n'est pas déclaré dans ce fichier : il ne peut pas être exporté", "pas déclaré",
                    "export nomme un gear, un guard, un emit, un mold, un enum, un ingot, une constante ou un train de ce fichier");
        }
        info.Visible = visible;
    }

    /// <summary>Vrai si <paramref name="module"/> déclare <paramref name="name"/> (gear, train, mold, enum, ingot ou constante), exporté ou non.</summary>
    private bool _DeclaresName(ModuleInfo module, string name)
    {
        var key = module.Prefix + name;
        return _gears.ContainsKey(key) || _molds.ContainsKey(key) || _enums.ContainsKey(key) || _ingots.ContainsKey(key)
            || _genericGears.ContainsKey(key) || _genericMolds.ContainsKey(key) || module.Scope.DeclaresHere(name);
    }

    /// <summary>
    /// Les exports de l'index d'une box : <c>export {a, b};</c> (chaque nom doit avoir été importé dans l'index, E113),
    /// <c>export {...Fichier};</c> (tous les noms exportés d'un fichier importé en entier, à plat) et <c>export Fichier;</c> (le
    /// fichier tel quel, ses noms restent qualifiés). L'export est une déclaration, pas une valeur.
    /// </summary>
    private void _ProcessExports(CompilationUnit unit, ModuleInfo info)
    {
        if (unit.Header?.Kind?.Name != "index") return;

        info.Exports = new(StringComparer.Ordinal);
        info.ExportedConstants = new(StringComparer.Ordinal);
        info.Modules = new(StringComparer.Ordinal);

        bool Taken(string name) => info.Exports.ContainsKey(name) || info.ExportedConstants.ContainsKey(name) || info.Modules.ContainsKey(name);

        void Duplicate(SourceSpan span, string name) => _Error(span, DiagnosticCodes.NameInUse, $"« {name} » est déjà exporté", "en double");

        foreach (var export in unit.Statements.OfType<ExportDeclaration>().SelectMany(e => e.Names))
        {
            var name = export.Name;
            if (export.Kind != ExportKind.Name)
            {
                if (!info.Imported.TryGetValue(name.Name, out var target))
                {
                    _Error(name.Span, DiagnosticCodes.UnknownExport, $"« {name.Name} » n'est pas un fichier importé en entier dans l'index : il ne peut pas être exporté", "pas importé",
                        $"importe-le d'abord : import {name.Name}; puis export {name.Name}; ou export {{...{name.Name}}};");
                    continue;
                }

                if (export.Kind == ExportKind.Module)
                {
                    if (Taken(name.Name)) Duplicate(name.Span, name.Name);
                    else info.Modules[name.Name] = target;
                    continue;
                }

                foreach (var inherited in _PublicNames(target).ToArray())
                {
                    if (Taken(inherited)) Duplicate(name.Span, inherited);
                    else if (_MemberKey(target, inherited) is { } inheritedKey) info.Exports[inherited] = inheritedKey;
                    else if (_MemberConstant(target, inherited) is { } inheritedConstant) info.ExportedConstants[inherited] = inheritedConstant;
                }
                foreach (var (inner, module) in target.Modules ?? [])
                {
                    if (Taken(inner)) Duplicate(name.Span, inner);
                    else info.Modules[inner] = module;
                }
                continue;
            }

            if (Taken(name.Name)) Duplicate(name.Span, name.Name);
            else if (info.Aliases.TryGetValue(name.Name, out var key)) info.Exports[name.Name] = key;
            else if (info.Scope[name.Name] is { HasValue: true } constant) info.ExportedConstants[name.Name] = constant.Value;
            else
                _Error(name.Span, DiagnosticCodes.UnknownExport, $"« {name.Name} » n'est pas importé dans l'index : il ne peut pas être exporté", "pas importé",
                    "importe-le d'abord : import Fichier only {nom}; puis export {nom};");
        }
    }

    /// <summary>
    /// Les déclarations d'une portée — le fichier (ses déclarations communes) ou un train nommé (ses déclarations
    /// locales) : enums, ingots, molds, signatures des gears, constantes, puis corps des gears. Une construction qui n'a pas
    /// sa place au niveau du fichier est signalée (E053, E054, E059).
    /// </summary>
    private void _BindDeclarations(IReadOnlyList<IStatement> declarations, RArray<IrStatement> constants, RArray<IrGear> gears)
    {
        // Les molds puis les signatures de tous les gears d'abord : un gear peut en appeler un écrit
        // plus bas, et tous deux utiliser un mold déclaré plus bas.
        // Les constantes réservent leur nom d'abord : un gear ou un type du même nom, écrit plus bas, est celui qu'on signale.
        var claimed = new HashSet<VarDeclaration>();
        foreach (var constant in declarations.OfType<VarDeclaration>().Where(c => c.IsConst))
        {
            if (_ClaimName(constant.Name, "une constante", "constante")) claimed.Add(constant);
        }

        _DeclareEnums(declarations.OfType<EnumDeclaration>());
        _DeclareIngots(declarations.OfType<IngotDeclaration>());
        _DeclareDatasets();
        _DeclareMolds(declarations.OfType<MoldDeclaration>());
        _ResolveIngots();
        _ResolveDatasetMolds();
        _CheckMoldContainment();
        _DeclareOverloads(declarations.OfType<MoldDeclaration>());
        _DeclareExtensions(declarations.OfType<ExtendDeclaration>());
        foreach (var gear in declarations.OfType<GearDeclaration>()) _DeclareGear(gear);
        _ResolveDestructors(declarations.OfType<MoldDeclaration>());
        _CheckErrorMolds(declarations.OfType<MoldDeclaration>());
        _CheckIngotNames();

        // Au niveau du fichier : les constantes en tête (E054 après un gear ou le train), visibles
        // partout ; toute autre instruction s'écrit dans un gear (E053).
        var afterDeclarations = false;
        foreach (var statement in declarations)
        {
            switch (statement)
            {
                case GearDeclaration or TrainDeclaration or TrainBlock:
                    afterDeclarations = true;
                    break;

                case MoldDeclaration or EnumDeclaration or IngotDeclaration or ImportDeclaration or ExportDeclaration or NamespaceDeclaration or AnnotationDeclaration:
                    break;

                case VarDeclaration { IsConst: true } constant when afterDeclarations:
                    _Error(constant.Name.Span, DiagnosticCodes.ConstantAfterGear, $"la constante « {constant.Name.Name} » vient après un gear ou le train", "trop bas",
                        "les constantes de niveau fichier s'écrivent en tête, avant les gears et le train");
                    break;

                case VarDeclaration { IsConst: true } constant:
                    if (!claimed.Contains(constant)) break;
                    if (_BindStatement(constant) is { } bound)
                    {
                        constants.Push(bound);
                        if (bound is IrVariableDeclaration declared && _datasetTables.TryGetValue(constant, out var dataset))
                        {
                            dataset.Table = declared.Variable;
                            constants.Push(_DatasetNames(dataset, declared.Span));
                        }
                        if (bound is IrVariableDeclaration declaredConstant && _precastConstants.Contains(constant)) _CheckPrecast(constant, declaredConstant);
                    }
                    break;

                case ExtendDeclaration extend:
                    _BindExtensionConstants(extend, constants);
                    break;

                case ReturnStatement:
                    _BindStatement(statement);   // E059 : return hors d'un gear
                    break;

                default:
                    _Error(statement.Span, DiagnosticCodes.StatementOutsideGear, "cette instruction s'écrit dans un gear", "au niveau du fichier",
                        "au niveau du fichier, seuls les constantes (const), les gears et le train sont permis",
                        "par exemple : gear calcul(): uint { … }, puis !train |> $ calcul();");
                    break;
            }
        }

        // Les valeurs des variantes et des champs voient toutes les constantes du fichier.
        _BindEnumValues();
        _CheckMoldDefaults();

        // Les corps des opérateurs puis des gears ; tous voient les constantes de la portée.
        _BindOverloadBodies(gears);
        var fileScope = _scope;
        foreach (var gear in declarations.OfType<GearDeclaration>())
        {
            _scope = new Scope(fileScope);
            if (_BindGear(gear) is { } bound) gears.Push(bound);
        }
        _scope = fileScope;
        _ResolveGuardErrors();
    }

    #endregion
}
