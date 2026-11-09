using System.Text.RegularExpressions;
using Rgl.AST;
using Rgl.AST.Collections;
using Rgl.AST.Datasets;
using Rgl.AST.Enums;
using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Generics;
using Rgl.AST.Ingots;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.AST.Molds;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>
/// Le type des expressions et les membres des types, déduits de l'arbre. Un type est son texte normalisé (<c>[Vec2]</c>,
/// <c>Pair&lt;int, string&gt;</c>, <c>int?</c>) : la déduction est volontairement courte — littéraux, noms, appels, accès,
/// index, casts — et renonce (<see langword="null"/>) à la moindre incertitude.
/// </summary>
internal sealed partial class SemanticDocument
{
    private int _depth;

    /// <summary>Le type d'une variable, d'un champ ou d'un paramètre : écrit, sinon déduit de la valeur de sa déclaration.</summary>
    public string? TypeOfSymbol(RglSymbol symbol)
    {
        if (symbol.Kind == RglSymbolKind.Gear || symbol.Kind == RglSymbolKind.Guard || symbol.Kind == RglSymbolKind.Emit) return symbol.TypeText;
        if (symbol.TypeText is { } written) return written;
        if (symbol.Initializer is null || symbol.Module != Self || _depth > 6) return null;

        ++_depth;
        try { return TypeOf(symbol.Initializer, (int)symbol.NameSpan.Start.Value); }
        finally { --_depth; }
    }

    /// <summary>Le type de <paramref name="expression"/>, vu des noms visibles à <paramref name="offset"/> ; <see langword="null"/> s'il n'est pas connu.</summary>
    public string? TypeOf(IExpression expression, int offset)
    {
        if (_depth > 12) return null;
        ++_depth;
        try
        {
            return expression switch
            {
                CppExpression cpp => Signatures.Collapse(cpp.ReturnType.Span.Text),
                LiteralExpression literal => literal.Kind switch
                {
                    TokenKind.IntegerLiteral => "int",
                    TokenKind.FloatLiteral => "double",
                    TokenKind.StringLiteral or TokenKind.RawStringLiteral => "string",
                    TokenKind.MutableStringLiteral => "mstring",
                    TokenKind.SymbolLiteral => "symbol",
                    TokenKind.DurationLiteral => "duration",
                    TokenKind.RegexLiteral => "regex",
                    TokenKind.TrueKeyword or TokenKind.FalseKeyword => "bool",
                    _ => null,
                },
                TemplateExpression or ReadExpression => "string",
                ParenthesizedExpression parenthesized => TypeOf(parenthesized.Inner, offset),
                NameExpression name => ResolveName(name.Name.Name, offset) is { IsCallable: false, IsType: false, Kind: not RglSymbolKind.Module } symbol ? TypeOfSymbol(symbol) : null,
                MemberAccessExpression access => _TypeOfMember(access, offset),
                CallExpression call => _TypeOfCall(call, offset),
                PipeExpression pipe => _TypeOfGearCall(pipe.Gear.Name, null, [pipe.Value, .. pipe.Arguments], offset),
                IndexExpression index => TypeOf(index.Target, offset) is { } target ? (target == "string" ? "string" : ElementOf(target)) : null,
                CastExpression cast => Signatures.Collapse(cast.Type.Span.Text) is var casted ? (cast.Kind == CastKind.Optional ? casted + "?" : casted) : null,
                MoldExpression { Type: { } type } => Signatures.Collapse(type.Span.Text),
                ConditionalExpression conditional => TypeOf(conditional.WhenTrue, offset) ?? TypeOf(conditional.WhenFalse, offset),
                BinaryExpression binary => binary.Operator is TokenKind.EqKeyword or TokenKind.Less or TokenKind.Greater or TokenKind.LessEqual or TokenKind.GreaterEqual or TokenKind.AndKeyword or TokenKind.OrKeyword or TokenKind.XorKeyword or TokenKind.IsKeyword
                    ? "bool"
                    : TypeOf(binary.Left, offset),
                UnaryExpression { Operator: TokenKind.Ampersand } unary when TypeOf(unary.Operand, offset) is { } addressed => $"uptr<{addressed}>",
                UnaryExpression { Operator: TokenKind.Star } unary when TypeOf(unary.Operand, offset) is { } pointer => SplitGeneric(pointer).Arguments.FirstOrDefault(),
                UnaryExpression { Operator: TokenKind.DeleteKeyword } => "void",
                UnaryExpression unary => unary.Operator == TokenKind.NotKeyword ? "bool" : TypeOf(unary.Operand, offset),
                IncrementExpression increment => TypeOf(increment.Operand, offset),
                ArrayExpression { Elements.Count: > 0 } array when array.Elements[0] is not SpreadElement => TypeOf(array.Elements[0], offset) is { } element ? $"[{element}]" : null,
                RangeExpression range => TypeOf(range.Start, offset) is { } bound ? $"[{bound}]" : null,
                UnbrandExpression unbrand => TypeOf(unbrand.Value, offset) is { } branded && ResolveType(StripNullable(branded), offset) is { Node: IngotDeclaration ingot } ? Signatures.Collapse(ingot.Base.Span.Text) : null,
                TupleExpression tuple => _TypeOfTuple(tuple, offset),
                _ => null,
            };
        }
        finally { --_depth; }
    }

    private string? _TypeOfTuple(TupleExpression tuple, int offset)
    {
        var parts = new List<string>();
        foreach (var element in tuple.Elements)
        {
            if (TypeOf(element.Value, offset) is not { } type) return null;
            parts.Add(element.Name is { IsMissing: false } name ? $"{type} {name.Name}" : type);
        }
        return "(" + string.Join(", ", parts) + ")";
    }

    private string? _TypeOfCall(CallExpression call, int offset)
    {
        if (call.Callee is MemberAccessExpression nativeAccess && _ResolveMember(nativeAccess, offset) is { Node: AST.Descriptors.DescriptorMember nativeMember } nativeSymbol)
            return nativeMember.Kind == AST.Descriptors.DescriptorMemberKind.Constructor
                ? Signatures.Collapse(nativeAccess.Target.Span.Text) : nativeSymbol.TypeText;
        var typeArguments = call.TypeArguments?.Select(t => Signatures.Collapse(t.Span.Text)).ToArray();
        if (call.Callee is NameExpression { Name.Name: "pack" } && ResolveName("pack", offset) is null && call.Arguments.Count == 1)
            return TypeOf(call.Arguments[0], offset);
        if (call.Callee is MemberAccessExpression { Member.Name: "free" } free && TypeOf(free.Target, offset) is { } raw && SplitGeneric(raw).Name is "ptr" or "uptr")
            return "void";
        switch (call.Callee)
        {
            case NameExpression { Name: var name }:
                // UserId(42) : une valeur de l'ingot.
                if (ResolveName(name.Name, offset) is { Kind: RglSymbolKind.Ingot } ingot) return ingot.Name;
                // parseJson<T>(texte) et toJson(valeur) : des intrinsèques, comme sleep ; ils rendent un résultat.
                if (ResolveName(name.Name, offset) is null)
                {
                    if (name.Name == "parseJson" && typeArguments is [var parsed]) return parsed + " ! ParseError";
                    if (name.Name == "toJson" && call.Arguments.Count == 1) return "string ! ParseError";
                }
                return _TypeOfGearCall(name.Name, typeArguments, call.Arguments, offset);

            // d.keys(), d.values(), d.put(k, v), d.without(k) : sur un dictionnaire seulement.
            case MemberAccessExpression { IsSafe: false, Member.Name: "keys" or "values" or "put" or "without" } dictionaryCall
                when TypeOf(dictionaryCall.Target, offset) is { } dictionaryType
                    && StripNullable(dictionaryType) is var dictionaryText
                    && IsArray(dictionaryText, out var valueType, out var isDictionary) && isDictionary:
                return dictionaryCall.Member.Name switch
                {
                    "keys" => "[" + SplitTop(dictionaryText[1..^1], ':')[0].Trim() + "]",
                    "values" => "[" + valueType + "]",
                    _ => dictionaryText,
                };

            case MemberAccessExpression { IsSafe: false, Member.Name: "has" }:
                return "bool";

            // Moves.find(nom) : l'identifiant d'une entrée, ou null.
            case MemberAccessExpression { IsSafe: false, Member.Name: "find" } finder when DatasetOf(finder.Target, offset) is not null:
                return Signatures.Collapse(finder.Target.Span.Text) + ".id?";

            // id.get() : l'entrée du dataset ; id.name() : son nom.
            case MemberAccessExpression { IsSafe: false, Member.Name: "get" or "name" } idCall when TypeOf(idCall.Target, offset) is { } idType && DatasetOfIdType(idType, offset) is { Node: DatasetDeclaration idDataset }:
                return idCall.Member.Name == "get" ? idDataset.Mold.Name : "string";

            case MemberAccessExpression { IsSafe: false, Member.Name: "push" or "add" }:
                return "void";

            case MemberAccessExpression { Target: NameExpression qualifier } access when !_IsValue(qualifier.Name.Name, offset) && QualifiedNames(qualifier.Name.Name) is { } names:
                return names.TryGetValue(access.Member.Name, out var symbol) ? _ReturnTypeOf(symbol, typeArguments, call.Arguments, offset) : null;

            default:
                return null;
        }
    }

    /// <summary>Le type de retour d'un appel du gear <paramref name="name"/>, les paramètres génériques remplacés par ce que les arguments donnent.</summary>
    private string? _TypeOfGearCall(string name, IReadOnlyList<string>? typeArguments, IReadOnlyList<IExpression> arguments, int offset) =>
        ResolveName(name, offset) is { IsCallable: true } symbol ? _ReturnTypeOf(symbol, typeArguments, arguments, offset) : null;

    private string? _ReturnTypeOf(RglSymbol symbol, IReadOnlyList<string>? typeArguments, IReadOnlyList<IExpression> arguments, int offset)
    {
        if (symbol.Kind == RglSymbolKind.Ingot) return symbol.Name;
        if (symbol.Node is not GearDeclaration gear) return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var generics = (gear.TypeParameters ?? []).Where(p => p.ValueType is null).Select(p => p.Name.Name).ToArray();
        for (var i = 0; i < Math.Min(generics.Length, typeArguments?.Count ?? 0); ++i) map[generics[i]] = typeArguments![i];
        if (generics.Length > 0)
        {
            for (var i = 0; i < Math.Min(gear.Parameters.Count, arguments.Count); ++i)
            {
                if (TypeOf(arguments[i], offset) is { } argument) Unify(Signatures.Collapse(gear.Parameters[i].Type.Span.Text), argument, generics, map);
            }
        }
        return Substitute(Signatures.ReturnTextOf(gear), map);
    }

    private string? _TypeOfMember(MemberAccessExpression access, int offset)
    {
        if (access.Member.IsMissing) return null;
        var member = access.Member.Name;

        // int.max : la constante d'un type numérique.
        if (access.Target is NameExpression { Name.Name: var numeric } && NumericTypes.Contains(numeric) && member is "min" or "max" && !_IsValue(numeric, offset)) return numeric;

        // Moves.attaquer : l'identifiant d'une entrée de dataset ; Moves.length : le nombre d'entrées.
        if (DatasetOf(access.Target, offset) is { } dataset)
            return dataset.Node is DatasetDeclaration declaration && declaration.Entries.Any(e => e.Name.Name == member) ? Signatures.Collapse(access.Target.Span.Text) + ".id" : member == "length" ? "uint" : null;
        // Direction.Nord : la variante d'un enum a le type de l'enum.
        if (_AsTypeSymbol(access.Target, offset) is { Kind: RglSymbolKind.Enum } enumeration) return enumeration.Name;
        // line.ZERO : une constante ou un gear que une extension ajoute au type.
        if (_AsTypeSymbol(access.Target, offset) is { IsType: true } named && ExtensionMethods.StaticMembers(Project, named.Name, this).FirstOrDefault(m => m.Name == member) is { } staticMember)
            return staticMember.TypeText ?? named.Name;
        // Geometry.ORIGINE : une constante d'un fichier importé.
        if (access.Target is NameExpression qualifier && !_IsValue(qualifier.Name.Name, offset) && QualifiedNames(qualifier.Name.Name) is { } names)
            return names.TryGetValue(member, out var symbol) ? TypeOfSymbol(symbol) : null;

        var target = TypeOf(access.Target, offset);
        if (target is null) return null;
        if (member == "length") return "uint";
        var type = StripNullable(target);
        var found = MembersOf(type, offset).FirstOrDefault(m => m.Name == member);
        return found is null ? null : access.IsSafe && found.TypeText is { } text && !text.EndsWith('?') ? text + "?" : found.TypeText;
    }

    /// <summary>Vrai si <paramref name="name"/> est une variable ou un paramètre à cet endroit : elle cache alors un fichier ou un type du même nom.</summary>
    private bool _IsValue(string name, int offset) =>
        LocalsAt(offset).LastOrDefault(l => l.Name == name) is { IsType: false };

    /// <summary>Le type que désigne l'expression <paramref name="expression"/> quand elle en est un (<c>Direction</c>, <c>Geometry.Vec2</c>) ; <see langword="null"/> sinon.</summary>
    public RglSymbol? AsTypeSymbol(IExpression expression, int offset) => _AsTypeSymbol(expression, offset);

    private RglSymbol? _AsTypeSymbol(IExpression expression, int offset) => expression switch
    {
        GenericTypeExpression generic => _AsTypeSymbol(generic.Target, offset),
        NameExpression name when !_IsValue(name.Name.Name, offset) => ResolveType(name.Name.Name, offset),
        MemberAccessExpression { Target: NameExpression qualifier, IsSafe: false } access when !_IsValue(qualifier.Name.Name, offset)
            && QualifiedNames(qualifier.Name.Name) is { } names && names.TryGetValue(access.Member.Name, out var symbol) && symbol.IsType => symbol,
        _ => null,
    };

    /// <summary>Ce que désigne <paramref name="expression"/> quand c'est une déclaration nommée (un type ou un gear qualifié, un nom), sinon <see langword="null"/>.</summary>
    public RglSymbol? ResolveExpression(IExpression expression, int offset) => expression switch
    {
        NameExpression name => ResolveName(name.Name.Name, offset),
        MemberAccessExpression { Target: NameExpression qualifier, IsSafe: false } access when !_IsValue(qualifier.Name.Name, offset)
            && QualifiedNames(qualifier.Name.Name) is { } names && names.TryGetValue(access.Member.Name, out var symbol) => symbol,
        _ => null,
    };

    /// <summary>Le symbole d'un type ou d'un nom écrit comme un nom de type : <c>Pair&lt;int, string&gt;</c> désigne <c>Pair</c>.</summary>
    public RglSymbol? ResolveTypeText(string typeText, int offset, ModuleInfo? context = null)
    {
        var (name, _) = SplitGeneric(StripNullable(typeText));
        return ResolveType(name, offset, context);
    }

    #region Membres des types

    /// <summary>Les membres de collections : tableaux et dictionnaires.</summary>
    private static readonly RglSymbol[] _collectionMembers =
    [
        new("length", RglSymbolKind.Field, default, null, null, "uint", Documentation: "Nombre d'éléments."),
        new("push", RglSymbolKind.Method, default, null, null, "push(élément): void", Documentation: "Ajoute un élément à la fin du tableau."),
        new("add", RglSymbolKind.Method, default, null, null, "add(clé, valeur): void", Documentation: "Ajoute une clé et sa valeur au dictionnaire ; une clé déjà présente arrête le programme."),
        new("has", RglSymbolKind.Method, default, null, null, "has(clé): bool", Documentation: "Vrai si le dictionnaire contient la clé."),
        new("keys", RglSymbolKind.Method, default, null, null, "keys(): [K]", Documentation: "Les clés du dictionnaire, dans l'ordre de ses entrées."),
        new("values", RglSymbolKind.Method, default, null, null, "values(): [V]", Documentation: "Les valeurs du dictionnaire, dans l'ordre de ses entrées."),
        new("put", RglSymbolKind.Method, default, null, null, "put(clé, valeur): [K:V]", Documentation: "Un nouveau dictionnaire où la clé vaut la valeur (ajoutée ou remplacée) ; écris `d <- d.put(k, v);`."),
        new("without", RglSymbolKind.Method, default, null, null, "without(clé): [K:V]", Documentation: "Un nouveau dictionnaire sans la clé ; écris `d <- d.without(k);`."),
    ];

    /// <summary>Les types numériques, qui portent les constantes <c>min</c> et <c>max</c> (<c>int.max</c>).</summary>
    public static readonly IReadOnlySet<string> NumericTypes = new HashSet<string>(StringComparer.Ordinal)
        { "byte", "ubyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double" };

    /// <summary>Les constantes <c>min</c> et <c>max</c> d'un type numérique.</summary>
    public static IReadOnlyList<RglSymbol> NumericLimits(string type) =>
    [
        new("min", RglSymbolKind.Field, default, null, null, type, Container: type, Documentation: $"La plus petite valeur d'un `{type}`" + (type is "float" or "double" ? " (la plus négative des finies)." : ".")),
        new("max", RglSymbolKind.Field, default, null, null, type, Container: type, Documentation: $"La plus grande valeur d'un `{type}`."),
    ];

    /// <summary>Les champs, éléments et méthodes qu'une valeur de ce type offre, à l'endroit <paramref name="offset"/>.</summary>
    /// <param name="typeText">Le type, sans nullable.</param>
    /// <param name="offset">Où résoudre les noms de types écrits.</param>
    /// <param name="context">Le fichier où le type est écrit, s'il n'est pas le document.</param>
    public IReadOnlyList<RglSymbol> MembersOf(string typeText, int offset, ModuleInfo? context = null)
    {
        var members = new List<RglSymbol>();
        _AddMembers(StripNullable(typeText), offset, context, members, new HashSet<string>(StringComparer.Ordinal), 0);
        return members;
    }

    private void _AddMembers(string typeText, int offset, ModuleInfo? context, List<RglSymbol> members, HashSet<string> spreading, int depth)
    {
        if (depth > 6) return;
        var text = typeText.Trim();

        if (text == "string")
        {
            members.Add(new RglSymbol("length", RglSymbolKind.Field, default, null, null, "uint", Documentation: "Nombre de caractères."));
            return;
        }

        // [T], [T;3], [K:V] : la longueur, et les méthodes qui vont avec.
        if (IsArray(text, out _, out var dictionary))
        {
            // Un [T;N] a une taille fixe : on n'y ajoute rien. Un [T], un [T+] et un [T;N..M] grandissent.
            var size = dictionary ? [] : SplitTop(text[1..^1], ';');
            var fixedSize = size.Count == 2 && !size[1].Contains("..", StringComparison.Ordinal);
            members.AddRange(dictionary ? _collectionMembers.Where(m => m.Name != "push") : _collectionMembers.Where(m => m.Name == "length" || m.Name == "push" && !fixedSize));
            return;
        }

        // (int x, string y) : les éléments d'un tuple, par leur nom ou Item1, Item2…
        if (text.StartsWith('(') && text.EndsWith(')') && !text.Contains("=>", StringComparison.Ordinal))
        {
            var index = 0;
            foreach (var element in SplitTop(text[1..^1], ','))
            {
                var (elementType, elementName) = _SplitNamed(element);
                ++index;
                if (elementName is not null) members.Add(new RglSymbol(elementName, RglSymbolKind.Field, default, null, null, elementType, Container: text));
                members.Add(new RglSymbol($"Item{index}", RglSymbolKind.Field, default, null, null, elementType, Container: text));
            }
            return;
        }

        // {int x, int y} : un mold anonyme.
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            foreach (var field in SplitTop(text[1..^1], ','))
            {
                var (fieldType, fieldName) = _SplitNamed(field);
                if (fieldName is not null) members.Add(new RglSymbol(fieldName, RglSymbolKind.Field, default, null, null, fieldType, Container: text));
            }
            return;
        }

        // Moves.id : un identifiant de dataset a get() et name().
        if (DatasetOfIdType(text, offset) is { } idDataset)
        {
            members.AddRange(DatasetIdMembers(idDataset));
            return;
        }

        var (name, arguments) = SplitGeneric(text);
        if (name is "ptr" or "uptr" && arguments.Count == 1)
        {
            members.Add(new RglSymbol("free", RglSymbolKind.Method, default, null, null, "free(): void",
                Documentation: "Libère l’objet pointé et invalide la variable."));
            return;
        }
        if ((ResolveType(name, offset, context) ?? _FindAnywhere(name, s => s.IsType)) is not { } symbol || !spreading.Add(symbol.Name + "@" + symbol.Module?.Path)) return;

        var declaringContext = symbol.Module is { } module && module != Self ? module : context;
        if (symbol.Node is AST.Descriptors.DescriptorDeclaration) { members.AddRange(DescriptorEditorModel.Members(symbol, false, arguments)); return; }
        switch (symbol.Node)
        {
            case MoldDeclaration mold:
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                var generics = (mold.TypeParameters ?? []).Select(p => p.Name.Name).ToArray();
                for (var i = 0; i < Math.Min(generics.Length, arguments.Count); ++i) map[generics[i]] = arguments[i];
                if (mold.IsError)
                {
                    members.Add(new RglSymbol("message", RglSymbolKind.Field, default, null, null, "string", Container: symbol.Name, Documentation: "Le texte de l'erreur."));
                    members.Add(new RglSymbol("where", RglSymbolKind.Field, default, null, null, "string", Container: symbol.Name, Documentation: "L'endroit du `fail` qui l'a envoyée."));
                }
                foreach (var member in mold.Members)
                {
                    switch (member)
                    {
                        case MoldField { Name.IsMissing: false } field:
                            members.Add(new RglSymbol(field.Name.Name, RglSymbolKind.Field, field.Name.Span, field, symbol.Module, Substitute(Signatures.Collapse(field.Type.Span.Text), map),
                                Container: symbol.Name, Documentation: symbol.Module?.DocumentationFor(field.Span)));
                            break;

                        case MoldSpread spread:
                            _AddMembers(Substitute(Signatures.Collapse(spread.Source.Span.Text), map), offset, declaringContext, members, spreading, depth + 1);
                            break;
                    }
                }
                break;

            case EnumDeclaration:
                break;

            case IngotDeclaration ingot:
                // Un ingot de mold lit les champs de sa base ; un ingot d'un tableau, d'un texte ou d'un nombre n'en a aucun : il faut le quitter par °.
                var baseText = Signatures.Collapse(ingot.Base.Span.Text);
                if (ResolveType(SplitGeneric(baseText).Name, offset, declaringContext) is { Kind: RglSymbolKind.Mold or RglSymbolKind.Error or RglSymbolKind.Ingot })
                    _AddMembers(baseText, offset, declaringContext, members, spreading, depth + 1);
                break;

            case null when symbol.Kind == RglSymbolKind.Error:
                members.Add(new RglSymbol("message", RglSymbolKind.Field, default, null, null, "string", Container: symbol.Name, Documentation: "Le texte de l'erreur."));
                members.Add(new RglSymbol("where", RglSymbolKind.Field, default, null, null, "string", Container: symbol.Name, Documentation: "L'endroit du `fail` qui l'a envoyée."));
                break;
        }
    }

    /// <summary>Les variantes d'un enum.</summary>
    public IReadOnlyList<RglSymbol> VariantsOf(RglSymbol enumeration) =>
        enumeration.Node is EnumDeclaration declaration
            ? [.. declaration.Variants.Where(v => !v.Name.IsMissing).Select(v => new RglSymbol(v.Name.Name, RglSymbolKind.Variant, v.Name.Span, v, enumeration.Module,
                v.Value is null ? null : Signatures.Collapse(v.Value.Span.Text), Container: enumeration.Name, Documentation: enumeration.Module?.DocumentationFor(v.Span)))]
            : [];

    #endregion

    #region Types écrits

    /// <summary>Le type sans son <c>?</c> final.</summary>
    public static string StripNullable(string type)
    {
        var text = type.Trim();
        return text.EndsWith('?') && !text.EndsWith("??", StringComparison.Ordinal) ? text[..^1].TrimEnd() : text;
    }

    /// <summary>Vrai pour <c>[T]</c>, <c>[T;N]</c>, <c>[T+]</c> et <c>[K:V]</c> ; <paramref name="isDictionary"/> distingue le dernier.</summary>
    public static bool IsArray(string type, out string element, out bool isDictionary)
    {
        element = "";
        isDictionary = false;
        var text = type.Trim();
        if (!text.StartsWith('[') || !text.EndsWith(']')) return false;

        var inner = text[1..^1];
        var colon = SplitTop(inner, ':');
        if (colon.Count == 2)
        {
            isDictionary = true;
            element = colon[1].Trim();
            return true;
        }

        var size = SplitTop(inner, ';');
        element = size[0].Trim();
        if (element.EndsWith('+')) element = element[..^1].Trim();
        return true;
    }

    /// <summary>Le type d'un élément d'un tableau ou la valeur d'un dictionnaire ; <see langword="null"/> pour un autre type.</summary>
    public static string? ElementOf(string type) => IsArray(StripNullable(type), out var element, out _) ? element : null;

    /// <summary>Sépare <c>Pair&lt;int, string&gt;</c> en son nom et ses arguments ; un type sans argument a une liste vide.</summary>
    public static (string Name, IReadOnlyList<string> Arguments) SplitGeneric(string type)
    {
        var match = Regex.Match(type.Trim(), @"^([\w.]+)\s*<(.*)>$", RegexOptions.Singleline);
        return match.Success ? (match.Groups[1].Value, [.. SplitTop(match.Groups[2].Value, ',').Select(a => a.Trim())]) : (type.Trim(), []);
    }

    /// <summary>Coupe <paramref name="text"/> aux <paramref name="separator"/> qui ne sont dans aucun groupe <c>()</c>, <c>[]</c>, <c>{}</c> ou <c>&lt;&gt;</c>.</summary>
    public static List<string> SplitTop(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; ++i)
        {
            var c = text[i];
            if (c is '(' or '[' or '{' or '<') ++depth;
            else if (c is ')' or ']' or '}' or '>' && !(c == '>' && i > 0 && text[i - 1] == '=')) --depth;
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>Un élément <c>int x</c> en son type et son nom ; sans nom, <c>int</c> en son type seul.</summary>
    private static (string Type, string? Name) _SplitNamed(string element)
    {
        var text = element.Trim();
        var match = Regex.Match(text, @"^(.*\S)\s+(\w+)$", RegexOptions.Singleline);
        return match.Success && !match.Groups[1].Value.EndsWith(',') ? (match.Groups[1].Value, match.Groups[2].Value) : (text, null);
    }

    /// <summary>Remplace dans <paramref name="type"/> chaque nom de <paramref name="map"/> par son type.</summary>
    public static string Substitute(string type, IReadOnlyDictionary<string, string> map) =>
        map.Count == 0 ? type : Regex.Replace(type, @"\b\w+\b", m => map.TryGetValue(m.Value, out var replacement) ? replacement : m.Value);

    /// <summary>
    /// Cherche dans <paramref name="actual"/>, le type d'un argument, ce que valent les paramètres génériques de
    /// <paramref name="pattern"/>, le type écrit du paramètre ; ce qui est déjà connu n'est pas remplacé.
    /// </summary>
    public static void Unify(string pattern, string actual, IReadOnlyCollection<string> generics, Dictionary<string, string> map)
    {
        var (p, a) = (pattern.Trim(), actual.Trim());
        if (generics.Contains(p))
        {
            map.TryAdd(p, a);
            return;
        }

        if (p.EndsWith('?') && !p.EndsWith("??", StringComparison.Ordinal))
        {
            Unify(p[..^1], StripNullable(a), generics, map);
            return;
        }

        if (IsArray(p, out var patternElement, out var patternDictionary) && IsArray(a, out var actualElement, out var actualDictionary) && patternDictionary == actualDictionary)
        {
            if (patternDictionary)
            {
                var (pk, ak) = (SplitTop(p[1..^1], ':'), SplitTop(a[1..^1], ':'));
                Unify(pk[0], ak[0], generics, map);
            }
            Unify(patternElement, actualElement, generics, map);
            return;
        }

        var (patternName, patternArguments) = SplitGeneric(p);
        var (actualName, actualArguments) = SplitGeneric(a);
        if (patternArguments.Count > 0 && patternName == actualName)
        {
            for (var i = 0; i < Math.Min(patternArguments.Count, actualArguments.Count); ++i) Unify(patternArguments[i], actualArguments[i], generics, map);
        }
    }

    #endregion
}
