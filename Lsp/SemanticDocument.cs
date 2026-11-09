using System.Text.RegularExpressions;
using Rgl.AST;
using Rgl.AST.Collections;
using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Generics;
using Rgl.AST.Ingots;
using Rgl.AST.Interfaces;
using Rgl.AST.InputOutput;
using Rgl.AST.Molds;
using Rgl.AST.Statements;
using Rgl.AST.Steps;
using Rgl.AST.Train;
using Rgl.AST.Types;
using Rgl.AST.Variables;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>
/// Un document lu pour l'éditeur, sans le compiler : son arbre (tolérant aux erreurs), ce qu'il importe, les noms visibles à
/// chaque endroit, et le type des expressions, déduit sur l'arbre. Il sert à l'autocomplétion, au survol, à l'aide à la saisie
/// d'un appel et à la navigation, qui doivent répondre même quand le fichier est en cours d'écriture et que le binder
/// refuserait de le lier.
/// </summary>
internal sealed partial class SemanticDocument
{
    /// <summary>Les erreurs standard, déclarées d'office.</summary>
    private static readonly Dictionary<string, string> _standardErrors = new(StringComparer.Ordinal)
    {
        ["Exception"] = "L'erreur standard : un texte, et l'endroit du `fail` qui l'a envoyée.",
        ["CastError"] = "L'erreur d'un cast, d'un `as!` ou d'une borne contrôlés qui échouent dans un guard.",
        ["LoopError"] = "L'erreur d'une boucle de guard `*<!N>` qui atteint sa limite.",
    };

    private readonly Dictionary<string, ModuleInfo> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RglSymbol> _imported = new(StringComparer.Ordinal);
    private readonly Dictionary<ModuleInfo, IReadOnlyDictionary<string, RglSymbol>> _foreignImports = [];
    private readonly Dictionary<string, IReadOnlyDictionary<string, RglSymbol>> _qualified = new(StringComparer.Ordinal);

    private SemanticDocument(ModuleInfo self, ProjectContext project)
    {
        Self = self;
        Project = project;
        _ReadImports();
        _ImplicitStd();
    }

    /// <summary>Le document lui-même.</summary>
    public ModuleInfo Self { get; }

    /// <summary>Les fichiers qu'il peut importer.</summary>
    public ProjectContext Project { get; }

    public SourceText Source => Self.Source;

    public CompilationUnit Unit => Self.Unit;

    /// <summary>Lit <paramref name="text"/> comme le contenu de <paramref name="path"/>.</summary>
    public static SemanticDocument Create(string path, string text, ProjectContext project) => new(ModuleInfo.Build(path, text), project);

    /// <summary>L'indice dans le texte d'une position de l'éditeur, ramené à la fin de sa ligne ou du texte.</summary>
    public int OffsetOf(uint line, uint character)
    {
        if (line >= Source.LineStart.Count) return Source.Content.Length;

        var start = (int)Source.LineStart[(int)line].Value;
        var end = line + 1 < Source.LineStart.Count ? (int)Source.LineStart[(int)line + 1].Value : Source.Content.Length;
        return Math.Min(start + (int)character, end);
    }

    #region Imports

    /// <summary>
    /// Ce que les imports du fichier rendent visible : <c>import X;</c> donne un accès qualifié et le train par défaut,
    /// <c>import X only {a, b as c};</c> les noms choisis, <c>import tools T;</c> les noms du dossier.
    /// </summary>
    private void _ReadImports()
    {
        foreach (var import in Unit.Imports)
        {
            if (import.Module.IsMissing) continue;

            if (import.Kind == ImportKind.Tools)
            {
                var parts = import.Module.Name.Split('.');
                var folder = parts[0] + ".tools";
                var members = Project.Modules.Where(m => Path.GetDirectoryName(m.Path)!.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment.Equals(folder, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (parts.Length > 1)
                {
                    if (members.FirstOrDefault(m => m.Name == parts[1]) is { } file) _ImportFrom(import, file);
                    continue;
                }

                var flat = new Dictionary<string, RglSymbol>(StringComparer.Ordinal);
                foreach (var symbol in members.SelectMany(m => m.Exported.Values)) flat.TryAdd(symbol.Name, symbol);
                _qualified[import.LocalModuleName] = flat;
                continue;
            }

            if (import.Kind is ImportKind.Box or ImportKind.Json) continue;
            if (import.Kind == ImportKind.Std)
            {
                if (Project.FindStd(import.Module.Name) is { } stdModule) _ImportFrom(import, stdModule);
                continue;
            }
            if (Project.Find(import.Module.Name) is { } module) _ImportFrom(import, module);
        }
    }

    /// <summary><c>std.nom</c> : la std est importée dans tous les fichiers, à plat.</summary>
    private void _ImplicitStd()
    {
        if (Project.Std is null || _qualified.ContainsKey("std")) return;
        _qualified["std"] = Project.StdNames.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private void _ImportFrom(ImportDeclaration import, ModuleInfo module)
    {
        if (import.Only is null)
        {
            _modules[import.LocalModuleName] = module;
            _qualified[import.LocalModuleName] = module.Exported;
            return;
        }

        foreach (var name in import.Only)
        {
            if (!name.Name.IsMissing && module.Exported.TryGetValue(name.Name.Name, out var symbol)) _imported[name.LocalName.Name] = symbol with { Name = name.LocalName.Name };
        }
    }

    /// <summary>Les fichiers importés en entier, par le nom sous lequel ils sont écrits.</summary>
    public IReadOnlyDictionary<string, ModuleInfo> ImportedModules => _modules;

    /// <summary>Les noms importés un à un, ou par un import tools de fichier.</summary>
    public IReadOnlyDictionary<string, RglSymbol> ImportedNames => _imported;

    /// <summary>Les noms qu'un accès qualifié (<c>Fichier.nom</c>) atteint, pour ce qualificatif ; <see langword="null"/> si ce n'est pas un fichier importé.</summary>
    public IReadOnlyDictionary<string, RglSymbol>? QualifiedNames(string qualifier) => _qualified.GetValueOrDefault(qualifier);

    #endregion

    #region Noms visibles

    /// <summary>Les variables, paramètres et paramètres génériques visibles à <paramref name="offset"/>, les plus proches en dernier.</summary>
    public List<RglSymbol> LocalsAt(int offset)
    {
        var scope = new List<RglSymbol>();
        foreach (var statement in Unit.Statements)
        {
            if (!_Contains(statement.Span, offset)) continue;
            _Walk(statement, offset, scope);
            break;
        }
        return scope;
    }

    private static bool _Contains(SourceSpan span, int offset) => span.Start.Value <= offset && offset <= span.End.Value;

    private void _Walk(IAstNode node, int offset, List<RglSymbol> scope)
    {
        switch (node)
        {
            case GearDeclaration gear:
                _AddTypeParameters(gear.TypeParameters, scope);
                foreach (var parameter in gear.Parameters) scope.Add(_ParameterSymbol(parameter));
                _WalkChildren(node, offset, scope);
                return;

            case MoldDeclaration mold:
                _AddTypeParameters(mold.TypeParameters, scope);
                return;

            case TrainBlock block:
                scope.AddRange(ModuleInfo.SymbolsOf(block.Declarations, Self));
                _WalkChildren(node, offset, scope);
                return;

            case TrainDeclaration train:
                foreach (var parameter in train.Parameters ?? []) scope.Add(_ParameterSymbol(parameter));
                _WalkChildren(node, offset, scope);
                return;

            case LambdaExpression lambda:
                foreach (var parameter in lambda.Parameters)
                    scope.Add(new RglSymbol(parameter.Name.Name, RglSymbolKind.Parameter, parameter.Name.Span, parameter, Self, parameter.Type is null ? null : Signatures.Collapse(parameter.Type.Span.Text)));
                _WalkChildren(node, offset, scope);
                return;

            case MatchArm { Name: { IsMissing: false } caught } arm:
                scope.Add(new RglSymbol(caught.Name, RglSymbolKind.Variable, caught.Span, arm, Self, arm.Type is null ? null : Signatures.Collapse(arm.Type.Span.Text)));
                _WalkChildren(node, offset, scope);
                return;

            case BlockStatement block:
                // Un bloc a sa portée : ce qu'il déclare ne sort pas, et une variable n'est visible qu'après sa déclaration.
                var inner = new List<RglSymbol>(scope);
                foreach (var statement in block.Statements)
                {
                    if (statement.Span.Start.Value >= offset && !_Contains(statement.Span, offset)) break;
                    if (_Contains(statement.Span, offset))
                    {
                        _Walk(statement, offset, inner);
                        _Merge(scope, inner);
                        return;
                    }
                    if (statement is VarDeclaration { Name.IsMissing: false } declaration) inner.Add(_VariableSymbol(declaration));
                }
                _Merge(scope, inner);
                return;

            default:
                _WalkChildren(node, offset, scope);
                return;
        }
    }

    /// <summary>Remplace le contenu de <paramref name="scope"/> par celui de <paramref name="inner"/> : la portée vue de l'endroit.</summary>
    private static void _Merge(List<RglSymbol> scope, List<RglSymbol> inner)
    {
        scope.Clear();
        scope.AddRange(inner);
    }

    private void _WalkChildren(IAstNode node, int offset, List<RglSymbol> scope)
    {
        foreach (var child in AstPrinter.ChildrenOf(node))
        {
            if (!_Contains(child.Span, offset)) continue;
            _Walk(child, offset, scope);
            return;
        }
    }

    private void _AddTypeParameters(IReadOnlyList<TypeParameter>? parameters, List<RglSymbol> scope)
    {
        foreach (var parameter in parameters ?? [])
        {
            if (parameter.Name.IsMissing) continue;
            scope.Add(parameter.ValueType is null
                ? new RglSymbol(parameter.Name.Name, RglSymbolKind.TypeParameter, parameter.Name.Span, parameter, Self, parameter.Constraint is null ? null : Signatures.Collapse(parameter.Constraint.Span.Text))
                : new RglSymbol(parameter.Name.Name, RglSymbolKind.Constant, parameter.Name.Span, parameter, Self, Signatures.Collapse(parameter.ValueType.Span.Text)));
        }
    }

    private RglSymbol _ParameterSymbol(Parameter parameter) =>
        new(parameter.Name.Name, RglSymbolKind.Parameter, parameter.Name.Span, parameter, Self, Signatures.Collapse(parameter.Type.Span.Text));

    private RglSymbol _VariableSymbol(VarDeclaration declaration) =>
        new(declaration.Name.Name, declaration.IsConst ? RglSymbolKind.Constant : RglSymbolKind.Variable, declaration.Name.Span, declaration, Self,
            declaration.Type is null ? null : Signatures.Collapse(declaration.Type.Span.Text), declaration.Expression, Documentation: Self.DocumentationFor(declaration.Span));

    /// <summary>Ce que désigne <paramref name="name"/> à <paramref name="offset"/> : une variable, une déclaration du fichier, un nom importé, un fichier importé ou une erreur standard.</summary>
    public RglSymbol? ResolveName(string name, int offset)
    {
        var locals = LocalsAt(offset);
        for (var i = locals.Count - 1; i >= 0; --i)
        {
            if (locals[i].Name == name) return locals[i];
        }
        return ResolveGlobal(name, Self);
    }

    /// <summary>Ce que <paramref name="name"/> désigne hors de tout corps, vu des noms de <paramref name="context"/>.</summary>
    public RglSymbol? ResolveGlobal(string name, ModuleInfo context)
    {
        if (context.Symbols.TryGetValue(name, out var own)) return own;
        if (context == Self)
        {
            if (_imported.TryGetValue(name, out var imported)) return imported;
            if (_modules.TryGetValue(name, out var module)) return module.DefaultTrain ?? ModuleSymbol(module);
        }
        else if (_ImportedBy(context).TryGetValue(name, out var foreign)) return foreign;
        if (_standardErrors.TryGetValue(name, out var doc)) return StandardError(name, doc);
        return null;
    }

    /// <summary>
    /// Une déclaration que <paramref name="accept"/> retient, cherchée dans tout le projet puis dans la std, pour un nom écrit dans un autre fichier
    /// que le document : un type est un texte, que le document relit avec ses propres imports ; un champ de <c>Player</c> de type <c>Moves.id</c>
    /// ou un <c>get()</c> qui rend un <c>Move</c> garde un nom que le document n'a peut-être pas importé. Utilisé seulement quand le document
    /// ne le résout pas lui-même.
    /// </summary>
    private RglSymbol? _FindAnywhere(string name, Func<RglSymbol, bool> accept)
    {
        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            var file = Project.Find(name[..dot]) ?? Project.FindStd(name[..dot]);
            return file is not null && file.Exported.TryGetValue(name[(dot + 1)..], out var member) && accept(member) ? member : null;
        }

        foreach (var module in Project.Modules.Concat(Project.Std ?? []))
        {
            if (module.Exported.TryGetValue(name, out var symbol) && accept(symbol)) return symbol;
        }
        return null;
    }

    /// <summary>Les noms qu'un autre fichier que le document a importés un à un (<c>import X only {a}</c>) : ce qu'il faut pour lire ses déclarations, un <c>...Autre</c> d'un mold par exemple.</summary>
    private IReadOnlyDictionary<string, RglSymbol> _ImportedBy(ModuleInfo context)
    {
        if (_foreignImports.TryGetValue(context, out var cached)) return cached;

        var names = new Dictionary<string, RglSymbol>(StringComparer.Ordinal);
        _foreignImports[context] = names;
        foreach (var import in context.Unit.Imports)
        {
            if (import.Module.IsMissing || import.Only is null || import.Kind is ImportKind.Box or ImportKind.Json or ImportKind.Tools) continue;

            var module = import.Kind == ImportKind.Std ? Project.FindStd(import.Module.Name) : Project.Find(import.Module.Name);
            if (module is null) continue;
            foreach (var name in import.Only)
            {
                if (!name.Name.IsMissing && module.Exported.TryGetValue(name.Name.Name, out var symbol)) names[name.LocalName.Name] = symbol with { Name = name.LocalName.Name };
            }
        }
        return names;
    }

    private static RglSymbol StandardError(string name, string doc) =>
        new(name, RglSymbolKind.Error, default, null, null, Documentation: doc);

    /// <summary>Le symbole d'un fichier importé, pour l'afficher et aller à son début.</summary>
    public static RglSymbol ModuleSymbol(ModuleInfo module) =>
        new(module.Name, RglSymbolKind.Module, new SourceSpan(module.Source, default, default), null, module, Documentation: module.Description);

    /// <summary>Le type que <paramref name="name"/> désigne à <paramref name="offset"/>, écrit éventuellement avec un qualificatif (<c>Geometry.Vec2</c>).</summary>
    public RglSymbol? ResolveType(string name, int offset, ModuleInfo? context = null)
    {
        context ??= Self;
        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            var qualifier = name[..dot];
            return _qualified.TryGetValue(qualifier, out var names) && names.TryGetValue(name[(dot + 1)..], out var member) && member.IsType ? member : null;
        }

        if (context == Self)
        {
            var locals = LocalsAt(offset);
            for (var i = locals.Count - 1; i >= 0; --i)
            {
                if (locals[i].Name == name && locals[i].IsType) return locals[i];
            }
        }
        return ResolveGlobal(name, context) is { IsType: true } symbol ? symbol : null;
    }

    #endregion

    #region Parcours de l'arbre

    /// <summary>Les nœuds qui contiennent <paramref name="offset"/>, de la racine au plus profond.</summary>
    public List<IAstNode> PathAt(int offset, bool inclusiveEnd = false)
    {
        var path = new List<IAstNode> { Unit };
        var current = (IAstNode)Unit;
        while (true)
        {
            var next = AstPrinter.ChildrenOf(current).FirstOrDefault(c => c.Span.Start.Value <= offset && (inclusiveEnd ? offset <= c.Span.End.Value : offset < c.Span.End.Value));
            if (next is null) return path;
            path.Add(next);
            current = next;
        }
    }

    #endregion
}
