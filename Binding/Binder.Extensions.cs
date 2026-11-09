using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.Extensions;
using Rgl.AST.Files;
using Rgl.AST.Gears;
using Rgl.AST.Interfaces;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;

namespace Rgl.Binding;

/// <summary>
/// Les extensions (<c>extend T { … }</c>), pour le compilateur seulement (décisions : une extension d'un type déclaré dans
/// le même fichier suit ce type, partout où il se nomme — <c>Fichier.T.nom</c> pour un fichier importé en entier, <c>T.nom</c>
/// une fois le type importé ; une extension d'un autre type — primitif, tableau, type importé — n'est vue que du fichier où elle
/// est écrite ; un gear d'extension est un gear ordinaire dont le premier paramètre est marqué <c>this</c> comme en C#, une
/// constante s'appelle <c>T.nom</c>, un gear sans <c>this</c> aussi, <c>v.f(a)</c> est exactement <c>f(v, a)</c>, un champ
/// du mold passe avant une extension du même nom, une méthode d'une collection aussi). Rien n'est ajouté au C++ : les
/// gears sont des gears, les constantes des constantes.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Ce que les extensions d'un fichier ajoutent à un type.</summary>
    private sealed class Extension(IrType target, string? space)
    {
        /// <summary>Le namespace du fichier qui l'écrit ; <see langword="null"/> s'il n'en a pas.</summary>
        public string? Namespace { get; } = space;

        /// <summary>Le type étendu.</summary>
        public IrType Target { get; } = target;

        /// <summary>Ses gears par nom ; vrai pour une méthode (premier paramètre <c>this</c>).</summary>
        public Dictionary<string, List<(GearSymbol Gear, bool IsMethod)>> Gears { get; } = new(StringComparer.Ordinal);

        /// <summary>Ses constantes par nom.</summary>
        public Dictionary<string, ScopeEntry> Constants { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Les extensions des types déclarés dans le fichier même de l'extension : tous les fichiers qui voient le type les voient.</summary>
    private readonly List<Extension> _typeExtensions = [];

    /// <summary>Les extensions écrites dans un fichier <c>forge: extend</c> : elles s'appliquent à tout le programme, sans import.</summary>
    private readonly List<Extension> _globalExtensions = [];

    /// <summary>Les gears des extensions génériques écrites dans un fichier <c>forge: extend</c> : vus de tout le programme.</summary>
    private readonly List<GenericExtensionGear> _globalGenericExtensions = [];

    /// <summary>Les gears d'extension déclarés (génériques exclus) : ceux que rien n'appelle ne sont pas émis.</summary>
    private readonly HashSet<GearSymbol> _extensionGears = [];

    /// <summary>Les extensions visibles du fichier en cours : celles de tout le programme, celles qui suivent leur type, et les siennes.</summary>
    private IEnumerable<Extension> _VisibleExtensions() => _globalExtensions.Concat(_typeExtensions).Concat(_ns.Module.Extensions);

    /// <summary>Les extensions génériques visibles du fichier en cours : celles de tout le programme et les siennes.</summary>
    private IEnumerable<GenericExtensionGear> _VisibleGenericExtensions() => _globalGenericExtensions.Concat(_ns.Module.GenericExtensions);

    private IEnumerable<Extension> _ExtensionsOf(IrType type) => _VisibleExtensions().Where(e => e.Target == type);

    /// <summary>Vrai si <paramref name="type"/> est un mold, un enum ou un ingot déclaré dans le fichier de <paramref name="span"/>.</summary>
    private static bool _DeclaredIn(IrType type, SourceSpan span) => type switch
    {
        MoldType { Symbol.Declaration: var declaration } => declaration.Source == span.Source,
        EnumType { Symbol.Declaration: var declaration } => declaration.Source == span.Source,
        IngotType { Symbol.Declaration: var declaration } => declaration.Source == span.Source,
        _ => false,
    };

    /// <summary>L'extension de chaque déclaration, pour lier ses constantes.</summary>
    private readonly Dictionary<ExtendDeclaration, Extension> _extensions = [];

    /// <summary>Le nombre de gears d'extension déclarés : il numérote leur nom C++.</summary>
    private uint _extensionCount;

    #region Déclarations

    /// <summary>
    /// Les types étendus et les signatures des gears d'extension ; leurs corps attendent <see cref="_BindOverloadBodies"/>,
    /// leurs constantes <see cref="_BindExtensionConstants"/>.
    /// </summary>
    private void _DeclareExtensions(IEnumerable<ExtendDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration.TypeParameters is not null)
            {
                _DeclareGenericExtension(declaration);
                continue;
            }

            if (_BindType(declaration.Target) is not { } target) continue;
            var shared = _DeclaredIn(target, declaration.Span);
            var list = shared ? _typeExtensions : _ns.Module.IsExtensionFile ? _globalExtensions : _ns.Module.Extensions;
            var extension = list.FirstOrDefault(e => e.Target == target && e.Namespace == _ns.Module.NamespaceName);
            if (extension is null)
            {
                extension = new Extension(target, _ns.Module.NamespaceName);
                list.Add(extension);
            }

            _extensions[declaration] = extension;
            var written = declaration.Target is AST.Types.NamedType named ? named.Name.Name : null;
            foreach (var gear in declaration.Members.OfType<GearDeclaration>()) _DeclareExtensionGear(extension, written, shared, gear);
        }
    }

    /// <param name="written">Le nom du type tel qu'écrit après <c>extend</c> ; <see langword="null"/> si ce n'est pas un nom simple.</param>
    /// <param name="shared">Vrai si l'extension suit son type (déclaré dans ce fichier) : son gear se nomme aussi <c>Fichier.Type.f</c> depuis un autre fichier.</param>
    private void _DeclareExtensionGear(Extension extension, string? written, bool shared, GearDeclaration gear)
    {
        if (gear.TypeParameters is not null)
        {
            _Unsupported(gear.Name.Span, "un gear générique dans une extension");
            return;
        }

        var name = gear.Name.Name;
        var display = $"{extension.Target}.{name}";
        if (_BindGearSignature(gear, display, display, $"ext{_extensionCount++}_{name}") is not { } symbol) return;
        _extensionGears.Add(symbol);

        var isMethod = gear.Parameters.Count > 0 && gear.Parameters[0].IsThis;
        var overloads = extension.Gears.GetValueOrDefault(name) ?? [];
        var problem = overloads.Any(o => _SameCallSignature(o.Gear, symbol)) ? $"« {name} » a déjà cette signature pour « {extension.Target} »"
            : isMethod && symbol.Parameters[0].Type != extension.Target ? $"le paramètre this de « {name} » doit être un « {extension.Target} », le type étendu"
            : !isMethod && extension.Target is not (MoldType or EnumType or IngotType) ? $"« {name} » n'a pas de this et « {extension.Target} » n'est pas un type nommé : il ne peut pas s'appeler sur le type"
            : null;
        if (problem is not null)
        {
            _Error(gear.Name.Span, DiagnosticCodes.InvalidExtension, problem, "invalide",
                "un gear d'extension commence par this Type nom (le type étendu) pour s'appeler sur une valeur : v.f() ; sans this, il s'appelle sur le type : Type.f()");
            return;
        }

        overloads.Add((symbol, isMethod));
        extension.Gears[name] = overloads;
        _pendingOverloads.Add((gear, symbol));

        // Sous un nom de type, le gear se nomme aussi « Type.f » comme un gear d'un fichier : étape de train, pipeline, parcours.
        // Sa clé contient « :: » pour ne jamais être prise pour le nom d'un fichier importé ni pour un nom du fichier.
        if (written is null) return;
        var key = _DeclKey(shared ? $"{written}::{name}" : $"~{written}::{name}");
        if (!_gearOverloads.TryGetValue(key, out var callable)) _gearOverloads[key] = callable = [];
        callable.Add(symbol);
        _gears.TryAdd(key, symbol);
        _ns.Module.ExtensionKeys[$"{written}.{name}"] = key;
    }

    /// <summary>
    /// Les constantes d'une extension, liées dans l'ordre du fichier comme les constantes de premier niveau : chacune a sa propre
    /// portée, <c>T.nom</c> la désigne. Une constante demande un type nommé (mold, enum, ingot).
    /// </summary>
    private void _BindExtensionConstants(ExtendDeclaration declaration, RArray<IrStatement> constants)
    {
        if (!_extensions.TryGetValue(declaration, out var extension)) return;

        foreach (var member in declaration.Members.OfType<VarDeclaration>())
        {
            var name = member.Name.Name;
            if (extension.Target is not (MoldType or EnumType or IngotType))
            {
                _Error(member.Name.Span, DiagnosticCodes.InvalidExtension, $"« {extension.Target} » n'est pas un type nommé : « {name} » ne peut pas s'écrire {extension.Target}.{name}", "type non nommé",
                    "une constante d'extension s'étend sur un mold, un enum ou un ingot");
                continue;
            }

            var outer = _scope;
            var own = new Scope(outer);
            _scope = own;
            var bound = _BindStatement(member);
            _scope = outer;
            if (bound is not null) constants.Push(bound);

            if (extension.Gears.ContainsKey(name) || extension.Constants.ContainsKey(name))
                _Error(member.Name.Span, DiagnosticCodes.InvalidExtension, $"« {name} » est déjà défini pour « {extension.Target} »", "en double", "chaque nom d'une extension est unique par type");
            else if (own[name] is { HasValue: true } entry) extension.Constants[name] = entry.Value;
        }
    }

    #endregion

    #region Extensions génériques

    /// <summary>Un gear d'une extension générique : un gear générique dont les paramètres sont ceux de l'extension, et le type étendu.</summary>
    private sealed record GenericExtensionGear(GenericGear Generic, ITypeNode Target, bool IsMethod, string? Namespace);

    /// <summary>Le nombre de gears d'extensions génériques : il numérote leur clé, donc leur nom C++.</summary>
    private uint _genericExtensionCount;

    /// <summary>
    /// Une extension générique : chaque gear est un gear générique, instancié à l'appel comme les autres ; le type étendu écrit
    /// (<c>[T]</c>) est le motif que le type de la valeur doit avoir. Décisions : seulement des gears à <c>this</c> (pas de constante, de
    /// guard, d'emit ni de gear sans <c>this</c>), des paramètres de type, et vue du seul fichier où elle est écrite.
    /// </summary>
    private void _DeclareGenericExtension(ExtendDeclaration declaration)
    {
        var parameters = declaration.TypeParameters!;
        if (declaration.Members.OfType<GearDeclaration>().Any(g => _NativeBodyFor(g) is not null))
        {
            _descriptorTemplates.Add((declaration, _ns));
            return;
        }
        if (!_CheckTypeParameters(parameters, false, [])) return;
        if (parameters.Any(p => p.ValueType is not null))
        {
            _Unsupported(declaration.Span, "une valeur générique dans une extension");
            return;
        }

        foreach (var member in declaration.Members)
        {
            if (member is not GearDeclaration gear)
            {
                _Unsupported(member.Span, "une constante dans une extension générique");
                continue;
            }
            if (gear.TypeParameters is not null)
            {
                _Unsupported(gear.Name.Span, "un gear générique dans une extension générique : ses paramètres sont ceux du extend");
                continue;
            }
            if (gear.Keyword != TokenKind.GearKeyword)
            {
                _Unsupported(gear.Name.Span, "un guard ou un emit dans une extension générique");
                continue;
            }

            var name = gear.Name.Name;
            var problem = gear.Parameters.Count == 0 || !gear.Parameters[0].IsThis ? $"« {name} » d'une extension générique commence par this : elle s'appelle sur une valeur"
                : gear.Parameters[0].Type.Span.Text != declaration.Target.Span.Text ? $"le paramètre this de « {name} » doit être écrit « {declaration.Target.Span.Text} », le type étendu"
                : _VisibleGenericExtensions().Any(g => g.Generic.Declaration.Name.Name == name && g.Target.Span.Text == declaration.Target.Span.Text) ? $"« {name} » est déjà défini pour « {declaration.Target.Span.Text} »"
                : null;
            if (problem is not null)
            {
                _Error(gear.Name.Span, DiagnosticCodes.InvalidExtension, problem, "invalide",
                    "un gear d'une extension générique commence par this Type nom, avec le type écrit après extend : extend<T: number> [T] { gear pure somme(this [T] v): T => … }");
                continue;
            }
            if (!_CheckTypeParameters(parameters, false, gear.Parameters.Select(p => p.Name.Name))) continue;

            var key = $"ext{_genericExtensionCount++}_{name}";
            (_ns.Module.IsExtensionFile ? _globalGenericExtensions : _ns.Module.GenericExtensions).Add(new GenericExtensionGear(new GenericGear(gear with { TypeParameters = parameters }, _ns, key), declaration.Target, true, _ns.Module.NamespaceName));
        }
    }

    /// <summary>
    /// L'instance du gear générique <paramref name="name"/> d'une extension du fichier pour la valeur <paramref name="receiver"/> : le type
    /// de la valeur doit avoir la forme du type étendu, dont les paramètres se déduisent. <see langword="null"/> si aucune ne convient ;
    /// <paramref name="found"/> dit si l'une avait ce nom et cette forme (l'erreur d'instanciation est alors déjà signalée).
    /// </summary>
    private IrType?[] _GenericExtensionParameterTypes(IEnumerable<GenericExtensionGear> generics, string name, IrExpression receiver)
    {
        foreach (var candidate in generics.Where(g => g.Generic.Declaration.Name.Name == name))
        {
            var environment = new GenericEnvironment(candidate.Generic.Declaration.TypeParameters!);
            _Infer(candidate.Target, receiver.Type, null, environment, []);
            if (environment.Parameters.Any(p => !environment.Types.ContainsKey(p.Name.Name) || environment.Types[p.Name.Name] is NullType)) continue;
            if (_InGeneric(candidate.Generic.Namespace, environment, () => _BindType(candidate.Target)) != receiver.Type) continue;

            return [.. candidate.Generic.Declaration.Parameters.Skip(1).Select(p => p.Type is AST.Types.FunctionType
                ? _InGeneric(candidate.Generic.Namespace, environment, () => _BindType(p.Type))
                : null)];
        }
        return [];
    }

    private GearSymbol? _GenericExtensionFor(IEnumerable<GenericExtensionGear> generics, string name, IrExpression receiver, IReadOnlyList<IrExpression> arguments, SourceSpan span, out bool found)
    {
        found = false;
        foreach (var candidate in generics.Where(g => g.Generic.Declaration.Name.Name == name))
        {
            var environment = new GenericEnvironment(candidate.Generic.Declaration.TypeParameters!);
            _Infer(candidate.Target, receiver.Type, null, environment, []);
            if (environment.Parameters.Any(p => !environment.Types.ContainsKey(p.Name.Name) || environment.Types[p.Name.Name] is NullType)) continue;
            if (_InGeneric(candidate.Generic.Namespace, environment, () => _BindType(candidate.Target)) != receiver.Type) continue;

            found = true;
            return _InstantiateGear(candidate.Generic, null, [receiver, .. arguments], span);
        }
        return null;
    }

    #endregion

    #region Utilisation

    /// <summary>
    /// Le type que nomme <paramref name="expression"/> (mold, enum ou ingot), écrit <c>T</c> ou <c>Fichier.T</c> : une variable ou un
    /// paramètre du même nom n'est pas un type.
    /// </summary>
    private IrType? _NamedTypeOf(IExpression expression)
    {
        if (expression is GenericTypeExpression generic)
        {
            var qualified = generic.Target is NameExpression simpleType ? simpleType.Name.Name : _QualifiedName(generic.Target);
            return qualified is null ? null : _BindGenericType(new AST.Types.NamedType(new Identifier(generic.Target.Span), generic.Arguments, generic.Span));
        }
        string? name;
        if (expression is NameExpression { Name.Name: var simple })
            name = _scope[simple].HasValue || _parameters.ContainsKey(simple) ? null : simple;
        else name = _QualifiedName(expression);
        if (name is null) return null;
        if (_TryFind(_molds, name, out var mold)) return new MoldType(mold);
        if (_TryFind(_enums, name, out var enumeration)) return new EnumType(enumeration);
        return _TryFind(_ingots, name, out var ingot) ? new IngotType(ingot) : null;
    }

    /// <summary>
    /// <c>Type.nom</c> : une constante d'extension ; <see langword="null"/> si <paramref name="access"/> n'en désigne pas. Deux extensions qui
    /// donnent le même nom se distinguent par leur namespace : <c>Type.&lt;Mon/Ns&gt;nom</c>.
    /// </summary>
    private IrExpression? _ExtensionConstant(MemberAccessExpression access, out bool ambiguous)
    {
        ambiguous = false;
        if (_NamedTypeOf(access.Target) is not { } type) return null;
        var found = _ExtensionsOf(type)
            .Where(e => (access.Namespace is null || e.Namespace == access.Namespace) && e.Constants.ContainsKey(access.Member.Name))
            .ToArray();
        if (found.Length > 1)
        {
            ambiguous = true;
            _ReportAmbiguousExtension(access.Member.Span, access.Member.Name, type.ToString(), found.Select(e => e.Namespace), $"{access.Target.Span.Text}.<{_ExampleNamespace(found.Select(e => e.Namespace))}>{access.Member.Name}");
            return null;
        }
        return found.Length == 1 && found[0].Constants[access.Member.Name].Symbol is { } variable ? new IrVariableReference(access.Span, variable) : null;
    }

    private static string _ExampleNamespace(IEnumerable<string?> namespaces) => namespaces.FirstOrDefault(n => n is not null) ?? "Mon/Namespace";

    /// <summary>Plusieurs extensions donnent <paramref name="name"/> à <paramref name="type"/> : seul un namespace écrit dans l'appel les départage.</summary>
    private void _ReportAmbiguousExtension(SourceSpan span, string name, string type, IEnumerable<string?> namespaces, string example)
    {
        var written = namespaces.Select(n => n ?? "(sans namespace)").ToArray();
        _Error(span, DiagnosticCodes.InvalidExtension, $"« {name} » est défini pour « {type} » par plusieurs extensions : {string.Join(", ", written)}", "ambigu",
            $"écris le namespace de celle que tu veux, par exemple : {example}", "un fichier sans namespace ne peut pas être désigné : donne-lui un namespace (namespace Mon/Ns;)");
    }

    /// <summary>
    /// <c>Type.f(a)</c> ou <c>v.f(a)</c> quand <c>f</c> est un gear d'extension : l'appel <c>f(a)</c> ou <c>f(v, a)</c>. Un champ lambda de la
    /// valeur passe avant une extension du même nom. Si plusieurs extensions conviennent, l'appel doit écrire le namespace de celle
    /// voulue (<c>v.&lt;Mon/Ns&gt;f(a)</c>) ; une extension d'un type précis passe avant une extension générique.
    /// </summary>
    /// <param name="handled">Faux si l'appel n'est pas celui d'une extension : l'appel garde son chemin habituel.</param>
    private IrExpression? _TryBindExtensionCall(CallExpression call, MemberAccessExpression member, out bool handled)
    {
        handled = false;
        var space = member.Namespace;
        var extensions = _VisibleExtensions().Where(e => space is null || e.Namespace == space).ToArray();
        var generics = _VisibleGenericExtensions().Where(g => space is null || g.Namespace == space).ToArray();
        if (_QualifiedName(member) is not null) return null;
        if (space is not null)
        {
            // Un namespace écrit doit être celui d'une extension visible.
            handled = true;
            if (extensions.Length == 0 && generics.Length == 0)
            {
                _Error(member.Member.Span, DiagnosticCodes.InvalidExtension, $"aucune extension visible n'a le namespace « {space} »", "namespace inconnu",
                    "le namespace s'écrit dans le fichier des extensions (namespace Mon/Ns;), qui doit être importé");
                return null;
            }
        }
        else if (extensions.Length == 0 && generics.Length == 0) return null;

        var name = member.Member.Name;
        if (_NamedTypeOf(member.Target) is { } named)
        {
            var own = extensions.Where(e => e.Target == named && e.Gears.ContainsKey(name)).ToArray();
            if (own.Length > 1)
            {
                handled = true;
                _ReportAmbiguousExtension(member.Member.Span, name, named.ToString(), own.Select(e => e.Namespace), $"{member.Target.Span.Text}.<{_ExampleNamespace(own.Select(e => e.Namespace))}>{name}(…)");
                return null;
            }
            if (own.Length == 1)
            {
                handled = true;
                return _CallExtensionOverload(call, member, own[0].Gears[name].Select(g => g.Gear), null);
            }
        }
        if (!extensions.Any(e => e.Gears.TryGetValue(name, out var gears) && gears.Any(g => g.IsMethod)) && !generics.Any(g => g.Generic.Declaration.Name.Name == name))
        {
            if (space is not null)
                _Error(member.Member.Span, DiagnosticCodes.InvalidExtension, $"l'extension de namespace « {space} » n'a pas de « {name} »", "inconnu", "le namespace désigne les extensions d'un fichier : vérifie le nom du gear");
            return null;
        }

        handled = true;
        var target = _BindExpression(member.Target);
        if (target is null) return null;

        var unbranded = _Unbranded(target);
        var hasField = space is null && unbranded.Type is MoldType { Symbol: var mold } && mold[name] is not null;
        var methods = hasField ? [] : extensions.Where(e => e.Target == target.Type && e.Gears.TryGetValue(name, out var gears) && gears.Any(g => g.IsMethod)).ToArray();
        if (methods.Length > 1)
        {
            _ReportAmbiguousExtension(member.Member.Span, name, target.Type.ToString(), methods.Select(e => e.Namespace), $"{member.Target.Span.Text}.<{_ExampleNamespace(methods.Select(e => e.Namespace))}>{name}(…)");
            return null;
        }
        if (methods.Length == 1) return _CallExtensionOverload(call, member, methods[0].Gears[name].Where(g => g.IsMethod).Select(g => g.Gear), target);

        if (!hasField && generics.Length > 0)
        {
            // Le type des paramètres, une fois ceux du extend déduits de la valeur : il donne à une lambda écrite en argument ses types.
            var expectedTypes = _GenericExtensionParameterTypes(generics, name, target);
            var written = call.Arguments.Select((a, i) => _BindExpected(a, i < expectedTypes.Length ? expectedTypes[i] : null)).ToArray();
            if (written.Any(a => a is null)) return null;
            var instance = _GenericExtensionFor(generics, name, target, written!, call.Span, out var matched);
            if (instance is not null) _extensionGears.Add(instance);
            if (matched) return instance is null ? null : _BindGearCall(call.Span, member.Member, target, written, resolved: instance);
        }

        if (space is not null)
        {
            _Error(member.Member.Span, DiagnosticCodes.InvalidExtension, $"l'extension de namespace « {space} » n'a pas de « {name} » pour « {target.Type} »", "inconnu", "le type de la valeur n'est pas celui que l'extension étend");
            return null;
        }

        // Pas d'extension pour ce type : un champ lambda, ou l'erreur habituelle d'un champ inconnu.
        var value = _FieldAccess(member, unbranded, unbranded.Type);
        return value is null ? null : _BindInvoke(call.Span, value, null, call.Arguments);
    }

    private IrExpression? _CallExtensionOverload(CallExpression call, MemberAccessExpression member, IEnumerable<GearSymbol> source, IrExpression? target)
    {
        var candidates = source.ToArray();
        if (candidates.Length == 0) return null;
        var written = call.Arguments.Select(a => _BindExpression(a)).ToArray();
        if (written.Any(a => a is null)) return null;
        var all = target is null ? written! : [target, .. written!];
        var ranked = candidates.Select(g => (Gear: g, Rank: _CallRank(g, all!))).Where(c => c.Rank is not null).ToArray();
        if (ranked.Length == 0) return _BindGearCall(call.Span, member.Member, target, written, resolved: candidates[0]);
        var bestRank = ranked.Select(c => c.Rank!.Value).Aggregate((a, b) => _CompareRank(a, b) <= 0 ? a : b);
        var best = ranked.Where(c => _CompareRank(c.Rank!.Value, bestRank) == 0).ToArray();
        if (best.Length != 1)
        {
            _ReportAmbiguousGear(member.Member, best.Select(c => c.Gear));
            return null;
        }
        return _BindGearCall(call.Span, member.Member, target, written, resolved: best[0].Gear);
    }

    /// <summary>
    /// Un namespace est unique dans tout le projet, boxes comprises (E123) : deux fichiers qui l'écrivent se confondraient dans l'appel
    /// qui doit les distinguer.
    /// </summary>
    private void _CheckNamespaces(IReadOnlyList<SourceModule> modules)
    {
        var seen = new Dictionary<string, SourceModule>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (module.Unit.Statements.OfType<NamespaceDeclaration>().FirstOrDefault() is not { } space || space.Name.Length == 0) continue;
            if (seen.TryAdd(space.Name, module)) continue;

            _Error(space.Span, DiagnosticCodes.InvalidNamespace, $"le namespace « {space.Name} » est déjà celui du fichier « {seen[space.Name].Name} »", "déjà pris",
                "un namespace est unique dans le projet, boxes comprises : choisis-en un autre");
        }
    }

    private IrExpression? _CallExtension(CallExpression call, MemberAccessExpression member, GearSymbol gear, IrExpression? receiver)
    {
        var offset = receiver is null ? 0 : 1;
        var arguments = call.Arguments.Select((argument, i) => _BindExpected(argument, i + offset < gear.Parameters.Count ? gear.Parameters[i + offset].Type : null)).ToArray();
        return _BindGearCall(call.Span, member.Member, receiver, arguments, resolved: gear);
    }

    #endregion
}
