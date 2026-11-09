using Rgl.AST;
using Rgl.AST.Collections;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Les collections : tableaux <c>[T]</c>, <c>[T;N]</c>, <c>[T;N..M]</c>, <c>[T+]</c>, dictionnaires
/// <c>[K:V]</c> et tuples <c>(T, U)</c>. Les éléments se lisent (<c>a[i]</c>, <c>d[k]</c>, <c>t.Item1</c>)
/// mais ne se modifient jamais en place (E013) ; un tableau ou un dictionnaire déclaré localement
/// grandit par <c>push</c> et <c>add</c>, jamais un paramètre (E014). Un index hors limites ou une clé
/// absente arrête le programme, ou fait échouer le guard qui les lit ; un index connu est vérifié à
/// la compilation (E086).
/// </summary>
internal sealed partial class Binder
{
    #region Types

    /// <summary>
    /// <c>[T]</c>, <c>[T;N]</c>, <c>[T;N..M]</c> ou <c>[T+]</c> : les tailles sont des entiers connus à
    /// la compilation, N au moins 1 pour <c>[T;N]</c>, N au plus M et M au moins 1 pour <c>[T;N..M]</c> (E092).
    /// Une taille invalide donne <c>[T]</c> : la valeur se lie quand même, sans erreur en cascade.
    /// </summary>
    private ArrayType? _BindArrayType(AstTypes.ArrayType array)
    {
        if (_BindType(array.Element) is not { } element) return null;
        if (element == PrimitiveType.Void)
        {
            _Error(array.Element.Span, DiagnosticCodes.TypeMismatch, "un tableau ne contient pas de void", "void");
            return null;
        }

        switch (array.Kind)
        {
            case AstTypes.ArrayTypeKind.Fixed:
                if (_ArraySize(array.Size!, minimum: 1) is not { } size) return new ArrayType(element);
                return new ArrayType(element, ArrayKind.Fixed, size);

            case AstTypes.ArrayTypeKind.Bounded:
                var (min, max) = (_ArraySize(array.Size!, minimum: 0), _ArraySize(array.Max!, minimum: 1));
                if (min is null || max is null) return new ArrayType(element);
                if (min > max)
                {
                    _Error(array.Span, DiagnosticCodes.InvalidArraySize, $"la taille minimale {min} dépasse la taille maximale {max}", "bornes inversées",
                        $"écris [{element};{max}..{min}]");
                    return new ArrayType(element);
                }
                return new ArrayType(element, ArrayKind.Bounded, min.Value, max.Value);

            case AstTypes.ArrayTypeKind.NonEmpty:
                return new ArrayType(element, ArrayKind.NonEmpty);

            default:
                return new ArrayType(element);
        }
    }

    /// <summary>Une taille de tableau : un entier connu à la compilation, au moins <paramref name="minimum"/> (E092 sinon).</summary>
    private uint? _ArraySize(IExpression written, uint minimum)
    {
        var value = _BindExpression(written);
        if (value is null) return null;
        if (ConstantFolder.Fold(value) is long size && size >= minimum && size <= uint.MaxValue) return (uint)size;

        _Error(value.Span, DiagnosticCodes.InvalidArraySize,
            ConstantFolder.Fold(value) is long ? $"une taille de tableau vaut au moins {minimum}" : "une taille de tableau est un entier connu à la compilation",
            "taille invalide", "par exemple : [int;3] ou [int;1..5]");
        return null;
    }

    /// <summary><c>[K:V]</c> : une clé se compare et se range (E093 sinon), une valeur n'est pas void.</summary>
    private DictionaryType? _BindDictionaryType(AstTypes.DictionaryType dictionary)
    {
        var key = _BindType(dictionary.Key);
        var value = _BindType(dictionary.Value);
        if (key is null || value is null) return null;

        if (!_IsKeyType(key))
        {
            _Error(dictionary.Key.Span, DiagnosticCodes.InvalidDictionaryKey, $"un « {key} » ne peut pas être une clé de dictionnaire", "clé invalide",
                "une clé se range par ordre : un entier, un bool, une string, un symbol, un enum, ou un ingot de l'un d'eux");
            return null;
        }
        return new DictionaryType(key, value);
    }

    /// <summary>Indique si un type peut être une clé : il a un ordre fiable (pas de flottant : NaN ; pas de mold).</summary>
    private static bool _IsKeyType(IrType type) => type switch
    {
        PrimitiveType { IsInteger: true } => true,
        PrimitiveType { Kind: PrimitiveKind.Bool or PrimitiveKind.String or PrimitiveKind.Symbol } => true,
        EnumType => true,
        IngotType ingot => _IsKeyType(ingot.Underlying),
        _ => false,
    };

    /// <summary><c>(int, string)</c> ou <c>(int x, int y)</c> : au moins deux éléments, aucun void.</summary>
    private TupleType? _BindTupleType(AstTypes.TupleType tuple)
    {
        var types = new List<IrType>();
        foreach (var element in tuple.Elements)
        {
            if (_BindType(element.Type) is not { } type) return null;
            types.Add(type);
        }
        if (types.Count < 2) return null;   // signalé par le parseur
        return new TupleType(types, [.. tuple.Elements.Select(e => e.Name?.Name)]);
    }

    #endregion

    #region Valeurs écrites

    /// <summary>
    /// Le type de collection <typeparamref name="T"/> attendu par le contexte : lui-même, le type d'un
    /// nullable, ou le seul membre de cette sorte dans une union.
    /// </summary>
    private static T? _ExpectedOf<T>(IrType? expected) where T : IrType => expected switch
    {
        T exact => exact,
        // Un ingot de tableau, de dictionnaire ou de tuple : le littéral prend le type de sa base (puis, constant, entre dans l'ingot).
        IngotType { Underlying: T baseType } => baseType,
        NullableType { Inner: T inner } => inner,
        UnionType union when union.Members.OfType<T>().ToArray() is [var only] => only,
        _ => null,
    };

    /// <summary>
    /// <c>[a, b, ...c]</c> : ses éléments prennent le type des éléments attendu, sinon leur type commun
    /// (un tableau vide sans type attendu est E001). Un élément étalé est un tableau de mêmes éléments.
    /// Pour une sorte de tableau attendue plus stricte que <c>[T]</c>, un nombre d'éléments connu est
    /// vérifié ici (E086), un nombre inconnu (avec un spread) à l'exécution.
    /// </summary>
    private IrExpression? _BindArrayLiteral(ArrayExpression literal, IrType? expected)
    {
        var target = _ExpectedOf<ArrayType>(expected);
        var parts = new List<IrArrayPart>();
        var failed = false;
        foreach (var element in literal.Elements)
        {
            if (element is SpreadElement spread)
            {
                var value = _BindExpression(spread.Value);
                if (value is null) failed = true;
                else if (_Unbranded(value).Type is not ArrayType)
                {
                    _Error(spread.Span, DiagnosticCodes.TypeMismatch, $"« ... » étale un tableau, pas un « {value.Type} »", $"de type « {value.Type} »",
                        "par exemple : [...autres, 4]");
                    failed = true;
                }
                else parts.Add(new IrArrayPart(_Unbranded(value), true));
            }
            else if (_BindExpected(element, target?.Element) is { } value) parts.Add(new IrArrayPart(value, false));
            else failed = true;
        }
        if (failed) return null;

        var elementType = target?.Element ?? _CommonElement(literal, parts);
        if (elementType is null) return null;

        var typed = new List<IrArrayPart>();
        foreach (var part in parts)
        {
            if (part.IsSpread)
            {
                var spreadType = (ArrayType)part.Value.Type;
                if (spreadType.Element != elementType)
                {
                    _Error(part.Value.Span, DiagnosticCodes.AssignmentTypeMismatch, $"« ...{part.Value.Span.Text} » étale des « {spreadType.Element} », et ce tableau contient des « {elementType} »",
                        $"de type « {spreadType} »", "les éléments étalés gardent leur type : ils doivent être du type des éléments du tableau");
                    return null;
                }
                typed.Add(part);
            }
            else if (_ConvertTo(part.Value, elementType, $"les éléments de ce tableau sont des « {elementType} »") is { } converted) typed.Add(part with { Value = converted });
            else return null;
        }

        var dynamic = new IrArrayValue(literal.Span, new ArrayType(elementType), typed);
        if (target is null || target.Kind == ArrayKind.Dynamic) return dynamic;
        return _ToArrayKind(dynamic, target);
    }

    /// <summary>Le type commun des éléments d'un tableau écrit sans type attendu (E001 vide, E040 sans type commun).</summary>
    private IrType? _CommonElement(ArrayExpression literal, List<IrArrayPart> parts)
    {
        if (parts.Count == 0)
        {
            _Error(literal.Span, DiagnosticCodes.CannotInferType, "type d'un tableau vide inconnu", "de type inconnu",
                "donne-lui un type, par exemple : [int] liste: [];");
            return null;
        }

        // Les éléments écrits trouvent leur type commun ; un tableau étalé impose le sien.
        if (parts.FirstOrDefault(p => p.IsSpread) is { } spread) return ((ArrayType)spread.Value.Type).Element;

        var common = parts[0].Value;
        foreach (var part in parts.Skip(1))
        {
            if (_Unify(common, part.Value) is not { } unified)
            {
                _Error(part.Value.Span, DiagnosticCodes.TypeMismatch, $"les éléments de ce tableau n'ont pas de type commun : « {common.Type} » et « {part.Value.Type} »",
                    $"de type « {part.Value.Type} »", "donne au tableau un type précis, par exemple : [int | string] valeurs: […];");
                return null;
            }
            common = unified.Left;
        }
        if (common.Type is NullType)
        {
            _Error(literal.Span, DiagnosticCodes.CannotInferType, "un tableau qui ne contient que null n'a pas de type", "de type inconnu",
                "donne-lui un type, par exemple : [int?] valeurs: [null];");
            return null;
        }
        return common.Type;
    }

    /// <summary>
    /// Un tableau écrit, mis dans une sorte plus stricte : son nombre d'éléments, s'il est connu, y est
    /// vérifié (E086) ; sinon, la conversion est contrôlée à l'exécution.
    /// </summary>
    private IrExpression? _ToArrayKind(IrArrayValue literal, ArrayType target)
    {
        if (!literal.HasKnownCount) return new IrArrayConvert(literal.Span, target, literal, target, ArrayConversion.Checked);

        var count = literal.Parts.Count;
        if (target.Fits((ulong)count)) return literal with { Array = target };

        _Error(literal.Span, DiagnosticCodes.OutOfRange, $"{count} élément(s) là où un « {target} » en attend {_CountText(target)}", "mauvaise taille",
            "la taille d'un tableau écrit est vérifiée à la compilation");
        return null;
    }

    /// <summary>Le nombre d'éléments que permet une sorte de tableau, pour les messages : « 3 », « de 1 à 5 », « au moins 1 ».</summary>
    private static string _CountText(ArrayType type) => type.Kind switch
    {
        ArrayKind.Fixed => $"{type.Size}",
        ArrayKind.Bounded => $"de {type.Size} à {type.Max}",
        ArrayKind.NonEmpty => "au moins 1",
        _ => "un nombre quelconque",
    };

    /// <summary>
    /// <c>["a": 1, "b": 2]</c> (<c>[:]</c> vide) : clés et valeurs prennent les types attendus, sinon
    /// leurs types communs (un dictionnaire vide sans type attendu est E001) ; deux clés connues égales
    /// sont E094.
    /// </summary>
    private IrExpression? _BindDictionaryLiteral(DictionaryExpression literal, IrType? expected)
    {
        var target = _ExpectedOf<DictionaryType>(expected);
        var entries = new List<(IrExpression Key, IrExpression Value)>();
        var failed = false;
        foreach (var entry in literal.Entries)
        {
            var key = _BindExpected(entry.Key, target?.Key);
            var value = _BindExpected(entry.Value, target?.Value);
            if (key is null || value is null) failed = true;
            else entries.Add((key, value));
        }
        if (failed) return null;

        DictionaryType type;
        if (target is not null) type = target;
        else
        {
            if (entries.Count == 0)
            {
                _Error(literal.Span, DiagnosticCodes.CannotInferType, "type d'un dictionnaire vide inconnu", "de type inconnu",
                    "donne-lui un type, par exemple : [string:int] ages: [:];");
                return null;
            }
            var (key, value) = (_CommonOf(entries.Select(e => e.Key), "les clés"), _CommonOf(entries.Select(e => e.Value), "les valeurs"));
            if (key is null || value is null) return null;
            if (!_IsKeyType(key))
            {
                _Error(literal.Entries[0].Key.Span, DiagnosticCodes.InvalidDictionaryKey, $"un « {key} » ne peut pas être une clé de dictionnaire", "clé invalide",
                    "une clé se range par ordre : un entier, un bool, une string, un symbol, un enum, ou un ingot de l'un d'eux");
                return null;
            }
            type = new DictionaryType(key, value);
        }

        var typed = new List<(IrExpression Key, IrExpression Value)>();
        var known = new HashSet<object>();
        foreach (var (key, value) in entries)
        {
            var convertedKey = _ConvertTo(key, type.Key, $"les clés de ce dictionnaire sont des « {type.Key} »");
            var convertedValue = _ConvertTo(value, type.Value, $"les valeurs de ce dictionnaire sont des « {type.Value} »");
            if (convertedKey is null || convertedValue is null) return null;

            if (ConstantFolder.Fold(convertedKey) is { } constant && !known.Add(constant))
            {
                _Error(key.Span, DiagnosticCodes.DuplicateKey, $"la clé {key.Span.Text} est déjà dans ce dictionnaire", "en double",
                    "chaque clé d'un dictionnaire est unique");
                return null;
            }
            typed.Add((convertedKey, convertedValue));
        }
        return new IrDictionaryValue(literal.Span, type, typed);
    }

    /// <summary>Le type commun de plusieurs valeurs (E040 sans type commun).</summary>
    private IrType? _CommonOf(IEnumerable<IrExpression> values, string what)
    {
        IrExpression? common = null;
        foreach (var value in values)
        {
            if (common is null) common = value;
            else if (_Unify(common, value) is { } unified) common = unified.Left;
            else
            {
                _Error(value.Span, DiagnosticCodes.TypeMismatch, $"{what} de ce dictionnaire n'ont pas de type commun : « {common.Type} » et « {value.Type} »",
                    $"de type « {value.Type} »", "donne au dictionnaire un type précis, par exemple : [string:int] ages: […];");
                return null;
            }
        }
        return common!.Type;
    }

    /// <summary>
    /// <c>(1, "a")</c> ou <c>(x: 1, y: 2)</c> : ses éléments prennent les types d'un tuple attendu de
    /// même taille, sinon leur propre type (<c>null</c> seul n'en a pas : E001).
    /// </summary>
    private IrExpression? _BindTupleLiteral(TupleExpression literal, IrType? expected)
    {
        var target = _ExpectedOf<TupleType>(expected) is { } tuple && tuple.Elements.Count == literal.Elements.Count ? tuple : null;
        var values = new List<IrExpression>();
        var failed = false;
        for (var i = 0; i < literal.Elements.Count; ++i)
        {
            var value = _BindExpected(literal.Elements[i].Value, target?.Elements[i]);
            if (value is null) failed = true;
            else if (target is not null)
            {
                if (_ConvertTo(value, target.Elements[i], $"l'élément {i + 1} de ce tuple est un « {target.Elements[i]} »") is { } converted) values.Add(converted);
                else failed = true;
            }
            else if (value.Type is NullType or PrimitiveType { Kind: PrimitiveKind.Void })
            {
                _Error(value.Span, DiagnosticCodes.CannotInferType, $"l'élément {i + 1} de ce tuple n'a pas de type", "de type inconnu",
                    "donne au tuple un type précis, par exemple : (int, string?) paire: (1, null);");
                failed = true;
            }
            else values.Add(value);
        }
        if (failed || values.Count < 2) return null;

        var names = literal.Elements.Select((e, i) => e.Name?.Name ?? target?.Names[i]).ToArray();
        return new IrTupleValue(literal.Span, new TupleType([.. values.Select(v => v.Type)], names), values);
    }

    #endregion

    #region Lecture

    /// <summary>
    /// <c>a[i]</c> sur un tableau (un index entier, E040 sinon) ou <c>d[k]</c> sur un dictionnaire (la
    /// clé prend le type des clés). Un index connu est vérifié à la compilation (E086) ; dans les
    /// limites sûres de la sorte de tableau, il n'est plus contrôlé.
    /// </summary>
    private IrExpression? _BindIndex(IndexExpression index)
    {
        var target = _BindExpression(index.Target);
        var position = _BindExpression(index.Index);
        if (target is null || position is null) return null;
        target = _Unbranded(target);

        switch (target.Type)
        {
            case ArrayType array:
                position = _Unbounded(_Unbranded(position));
                if (position.Type is not PrimitiveType { IsInteger: true })
                {
                    _Error(position.Span, DiagnosticCodes.TypeMismatch, $"un index de tableau est un entier, pas un « {position.Type} »", $"de type « {position.Type} »",
                        "par exemple : noms[0]");
                    return null;
                }

                var isChecked = true;
                if (ConstantFolder.Fold(position) is long known)
                {
                    // Un index négatif compte depuis la fin : -1 est le dernier élément.
                    var reach = array.Maximum is { } limit ? (long)limit : long.MaxValue;
                    if (known >= reach || known < 0 && -known > reach)
                    {
                        _Error(position.Span, DiagnosticCodes.OutOfRange, $"l'index {known} est hors des limites d'un « {array} »", "hors limites",
                            array.Maximum is { } bound ? $"les index de ce tableau vont de -{bound} à {bound - 1}" : "un index négatif compte depuis la fin : -1 est le dernier élément");
                        return null;
                    }
                    if (known < 0 && array.Kind == ArrayKind.Fixed) position = new IrIntegerConstant(position.Span, position.Type, array.Size + known);
                    isChecked = known < 0 ? array.Kind != ArrayKind.Fixed : known >= array.Minimum;
                }
                return new IrIndex(index.Span, array.Element, target, position, isChecked);

            case PrimitiveType { Kind: PrimitiveKind.String }:
                position = _Unbounded(_Unbranded(position));
                if (position.Type is not PrimitiveType { IsInteger: true })
                {
                    _Error(position.Span, DiagnosticCodes.TypeMismatch, $"un index de texte est un entier, pas un « {position.Type} »", $"de type « {position.Type} »",
                        "par exemple : texte[0], ou texte[-1] pour le dernier caractère");
                    return null;
                }
                return new IrIndex(index.Span, PrimitiveType.String, target, position, true);

            case DictionaryType dictionary:
                var key = _ConvertTo(position, dictionary.Key, $"les clés de « {target.Span.Text} » sont des « {dictionary.Key} »");
                return key is null ? null : new IrIndex(index.Span, dictionary.Value, target, key, true);

            case MoldType mold:
                return _BindIndexerRead(index.Span, target, position, mold);

            default:
                _Error(index.Span, DiagnosticCodes.TypeMismatch, $"« [ ] » lit un élément de tableau, un caractère de texte ou une valeur de dictionnaire, pas d'un « {target.Type} »",
                    $"de type « {target.Type} »",
                    target.Type is NullableType
                        ? "la valeur peut être null : vérifie-la avant (if (… not eq null) { … }), ou donne une valeur de secours avec ??"
                        : "par exemple : noms[0], ages[\"Alix\"]");
                return null;
        }
    }

    /// <summary>
    /// <c>a.length</c> sur un tableau ou un dictionnaire, <c>t.Item1</c> ou <c>t.nom</c> sur un tuple ;
    /// <see langword="null"/> sans rien signaler si la valeur n'est pas une collection.
    /// </summary>
    /// <param name="handled">Vrai si la valeur est une collection : le résultat, ou l'erreur signalée, est le sien.</param>
    private IrExpression? _CollectionMember(MemberAccessExpression access, IrExpression target, out bool handled)
    {
        var name = access.Member.Name;
        handled = target.Type is ArrayType or DictionaryType or TupleType || target.Type == PrimitiveType.String && name == "length";
        switch (target.Type)
        {
            case PrimitiveType { Kind: PrimitiveKind.String } when name == "length":
                return new IrLength(access.Span, target);

            case ArrayType or DictionaryType when name == "length":
                return new IrLength(access.Span, target);

            case TupleType tuple when tuple.IndexOf(name) is var position and >= 0:
                return new IrTupleItem(access.Span, tuple.Elements[position], target, position);

            case TupleType tuple:
                var members = Enumerable.Range(0, tuple.Elements.Count).Select(i => tuple.Names[i] is { } given ? $"Item{i + 1} ({given})" : $"Item{i + 1}");
                _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"« {tuple} » n'a pas d'élément « {name} »", "élément inconnu",
                    $"éléments : {string.Join(", ", members)}");
                return null;

            case ArrayType or DictionaryType:
                _Error(access.Member.Span, DiagnosticCodes.UnknownField, $"« {target.Type} » n'a pas de champ « {name} »", "champ inconnu",
                    target.Type is ArrayType
                        ? "un tableau a le champ length et la méthode push ; ses éléments se lisent par index : noms[0]"
                        : "un dictionnaire a le champ length et les méthodes has et add ; ses valeurs se lisent par clé : ages[\"Alix\"]");
                return null;

            default:
                return null;
        }
    }

    #endregion

    #region Méthodes

    /// <summary>
    /// <c>nums.push(v)</c>, <c>ages.add(k, v)</c>, <c>ages.has(k)</c>. <c>push</c> et <c>add</c> modifient
    /// un tableau ou un dictionnaire : seulement une variable déclarée localement (E014 pour un
    /// paramètre, E005 pour une constante, E013 pour un champ ou un élément), et jamais un <c>[T;N]</c>.
    /// </summary>
    private IrExpression? _BindMethodCall(CallExpression call, MemberAccessExpression member)
    {
        var name = member.Member.Name;
        var target = _BindExpression(member.Target);
        if (target is null) return null;

        bool Count(int expected)
        {
            if (call.Arguments.Count == expected) return true;
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"« {name} » attend {expected} argument(s), pas {call.Arguments.Count}", $"{call.Arguments.Count} argument(s)",
                name == "add" ? "par exemple : ages.add(\"Alix\", 12);" : $"par exemple : {member.Target.Span.Text}.{name}(valeur)");
            return false;
        }

        switch (_Unbranded(target).Type, name)
        {
            case (ArrayType array, "push"):
                if (!Count(1) || !_CheckGrowTarget(member.Target, target, name)) return null;
                if (array.Kind == ArrayKind.Fixed)
                {
                    _Error(member.Member.Span, DiagnosticCodes.TypeMismatch, $"un « {array} » a une taille fixe : on n'y ajoute rien", "taille fixe",
                        $"pour un tableau qui grandit, déclare-le [{array.Element}] ou [{array.Element};1..N]");
                    return null;
                }
                var element = _BindExpected(call.Arguments[0], array.Element);
                var pushed = element is null ? null : _ConvertTo(element, array.Element, $"les éléments de « {member.Target.Span.Text} » sont des « {array.Element} »");
                return pushed is null ? null : new IrPush(call.Span, target, pushed);

            case (DictionaryType dictionary, "add"):
                if (!Count(2) || !_CheckGrowTarget(member.Target, target, name)) return null;
                var key = _BindExpected(call.Arguments[0], dictionary.Key);
                var value = _BindExpected(call.Arguments[1], dictionary.Value);
                if (key is null || value is null) return null;
                var convertedKey = _ConvertTo(key, dictionary.Key, $"les clés de « {member.Target.Span.Text} » sont des « {dictionary.Key} »");
                var convertedValue = _ConvertTo(value, dictionary.Value, $"les valeurs de « {member.Target.Span.Text} » sont des « {dictionary.Value} »");
                return convertedKey is null || convertedValue is null ? null : new IrAdd(call.Span, target, convertedKey, convertedValue);

            case (DictionaryType dictionary, "has"):
                if (!Count(1)) return null;
                var probe = _BindExpected(call.Arguments[0], dictionary.Key);
                var searched = probe is null ? null : _ConvertTo(probe, dictionary.Key, $"les clés de « {member.Target.Span.Text} » sont des « {dictionary.Key} »");
                return searched is null ? null : new IrContains(call.Span, _Unbranded(target), searched);

            case (DictionaryType dictionary, "keys" or "values"):
                if (!Count(0)) return null;
                return name == "keys"
                    ? new IrDictionaryParts(call.Span, _Unbranded(target), false, new ArrayType(dictionary.Key))
                    : new IrDictionaryParts(call.Span, _Unbranded(target), true, new ArrayType(dictionary.Value));

            case (DictionaryType dictionary, "without"):
                if (!Count(1)) return null;
                var removed = _BindExpected(call.Arguments[0], dictionary.Key);
                var removedKey = removed is null ? null : _ConvertTo(removed, dictionary.Key, $"les clés de « {member.Target.Span.Text} » sont des « {dictionary.Key} »");
                return removedKey is null ? null : new IrDictionaryWithout(call.Span, _Unbranded(target), removedKey);

            case (DictionaryType dictionary, "put"):
                if (!Count(2)) return null;
                var putKey = _BindExpected(call.Arguments[0], dictionary.Key);
                var putValue = _BindExpected(call.Arguments[1], dictionary.Value);
                if (putKey is null || putValue is null) return null;
                var storedKey = _ConvertTo(putKey, dictionary.Key, $"les clés de « {member.Target.Span.Text} » sont des « {dictionary.Key} »");
                var storedValue = _ConvertTo(putValue, dictionary.Value, $"les valeurs de « {member.Target.Span.Text} » sont des « {dictionary.Value} »");
                return storedKey is null || storedValue is null ? null : new IrDictionaryPut(call.Span, _Unbranded(target), storedKey, storedValue);

            case (var type, _):
                _Error(member.Member.Span, DiagnosticCodes.UnknownField, $"« {type} » n'a pas de méthode « {name} »", "méthode inconnue",
                    type switch
                    {
                        ArrayType => "un tableau a la méthode push et le champ length",
                        DictionaryType => "un dictionnaire a les méthodes has, add, put, keys, values et without, et le champ length",
                        _ => "seuls les tableaux (push) et les dictionnaires (has, add) ont des méthodes pour l'instant",
                    });
                return null;
        }
    }

    #endregion
}
