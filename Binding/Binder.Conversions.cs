using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Binding;

/// <summary>
/// Toutes les conversions implicites, en un seul endroit : ce qu'une valeur devient quand elle
/// entre dans un type attendu (<see cref="_Coerce"/>, <see cref="_ConvertTo"/> qui signale E003),
/// et le type commun de deux valeurs (<see cref="_Unify"/>). Les règles :
/// <list type="bullet">
/// <item>nombres : sans perte seulement (<c>int</c> vers <c>long</c>…), avec une notice I001 ; une
/// constante qui tient dans le type y entre sans rien dire ; le reste s'écrit avec un cast ;</item>
/// <item>types bornés : une valeur connue est vérifiée à la compilation, une autre contrôlée à
/// l'exécution, sauf si ses propres bornes sont contenues ; un borné vaut sa base partout ;</item>
/// <item>nullables : <c>null</c> ou un <c>T</c> entrent dans un <c>T?</c> ;</item>
/// <item>molds : duck typing (les champs en trop sont laissés, les absents prennent leur défaut),
/// mais jamais de champ perdu en silence pour un type commun ;</item>
/// <item>unions et résultats : le membre exact, sinon le seul qui accepte la valeur (celui qui garde
/// tous ses champs l'emporte) ; une union plus étroite s'élargit ; <c>ok</c>/<c>err</c> implicites ;</item>
/// <item>ingots : seule une constante de la base y entre sans être marquée.</item>
/// </list>
/// <see cref="_CanCoerce"/> essaie une conversion sans rien signaler.
/// </summary>
internal sealed partial class Binder
{
    #region Point d'entrée

    /// <summary>
    /// <paramref name="value"/>, dans le type <paramref name="target"/> qui la reçoit, par une
    /// conversion implicite si elle existe (E003 sinon).
    /// </summary>
    /// <param name="value">La valeur, déjà liée.</param>
    /// <param name="target">Le type de la variable qui la reçoit.</param>
    /// <param name="why">D'où vient le type attendu, pour le message.</param>
    private IrExpression? _ConvertTo(IrExpression value, IrType target, string why)
    {
        if (target is ArrayType && value.Type is ArrayType sourceArray && sourceArray != _NativeBoundaryType(sourceArray) && target == _NativeBoundaryType(sourceArray) && value is not IrArrayValue)
        {
            _Error(value.Span, DiagnosticCodes.NativePackRequired, "ce tableau doit être empaqueté avant de passer au C++", "pack(tableau)", "pack reconstruit un tableau de structures sans changer la disposition de la source");
            return null;
        }
        if (_Coerce(value, target) is { } converted) return converted;

        // Une valeur connue hors des bornes d'un type borné : la valeur elle-même est en cause.
        if (_OutOfBounds(value, target) is { } outside)
        {
            _Error(value.Span, DiagnosticCodes.OutOfRange, $"{outside} est hors des bornes de « {target} »", "hors des bornes", why);
            return null;
        }

        // Un gear nommé sans parenthèses est le gear lui-même : le piège le plus courant d'un bras de match (« Exception v |> etom; »).
        var reference = value is IrGearReference { Gear: var named } && target is not FunctionType
            ? $"« {named.Name} » est un gear : écrit sans parenthèses, il désigne le gear lui-même et non son résultat ; pour l'appeler, écris {named.Name}(…)"
            : null;
        string?[] help =
        [
            why,
            target switch
            {
                MoldType mold => _WhyNotMold(value, mold),
                UnionType or ResultType => _WhyNotUnion(value, target),
                _ => _WhyNotConverted(value.Type, target),
            },
            reference,
        ];
        _Error(value.Span, DiagnosticCodes.AssignmentTypeMismatch, $"valeur de type « {value.Type} » là où « {target} » est attendu", $"de type « {value.Type} »", [.. help.Where(h => h is not null)!]);
        return null;
    }

    /// <summary>
    /// <paramref name="value"/> dans le type <paramref name="target"/> : telle quelle, adaptée si c'est
    /// une constante qui y tient, convertie avec une notice si la conversion est sans perte.
    /// </summary>
    /// <returns>La valeur convertie, ou <see langword="null"/> si aucune conversion implicite n'existe (rien n'est signalé).</returns>
    private IrExpression? _Coerce(IrExpression value, IrType target)
    {
        if (value.Type == target) return value;
        if (target is PointerType pointer && value.Type is PointerType pointerSource && pointer.Element == pointerSource.Element
            && (value is IrMove or IrRawMemory { Operation: RawMemoryOperation.Allocate }) && !(pointer.Owned && _BorrowedPointer(value)))
            return new IrRawMemory(value.Span, target, RawMemoryOperation.Transfer, [value]);
        if (target is PointerType && value is IrNullConstant) return new IrRetype(value.Span, target, value);
        if (target is PointerType allocating && value.Type is not PointerType && value is not IrNullConstant)
        {
            var initial = _NoCopy(_Coerce(value, allocating.Element), value.Span, "la valeur du pointeur");
            return initial is null ? null : new IrRawMemory(value.Span, target, RawMemoryOperation.Allocate, [initial]);
        }
        // Une conversion déclarée dans le mold l'emporte sur le duck typing.
        if (_FindConversion(value.Type, target, "implicit") is { } conversion) return _OverloadCall(value.Span, conversion, [value]);
        if (target is MoldType mold) return value.Type is UnionType ? _CoerceMembers(value, mold) : _CoerceMold(value, mold);
        if (target is UnionType union) return _CoerceUnion(value, union);
        if (target is ResultType result) return _CoerceResult(value, result);
        if (target is IngotType ingot) return _CoerceIngot(value, ingot);
        if (target is PrimitiveType { Bounds: not null } bounded) return _CoerceBounded(value, bounded);
        if (target is ArrayType array) return _CoerceArray(value, array);
        if (target is DictionaryType dictionary) return _CoerceDictionary(value, dictionary);
        if (target is TupleType tuple) return _CoerceTuple(value, tuple);
        // Une lambda pure entre dans le même type sans pure : la même valeur.
        if (target is FunctionType function) return value.Type is FunctionType source && function.Accepts(source) ? new IrRetype(value.Span, function, value) : null;

        // Vers T? : null, ou une valeur de T (convertie au besoin).
        if (target is NullableType nullable)
        {
            if (value.Type is NullType) return new IrConversion(value.Span, nullable, value);
            // Une variable dont on sait qu'elle n'est pas null, rendue à son propre type : la variable elle-même.
            if (value is IrNonNull { Value: var original } && original.Type == nullable) return original;
            if (value.Type is NullableType) return null;
            return _Coerce(value, nullable.Inner) is { } inner ? new IrConversion(value.Span, nullable, inner) : null;
        }

        // Un type borné vaut sa base partout où elle est attendue.
        value = _Unbounded(value);
        if (value.Type == target) return value;
        if (value.Type is not PrimitiveType { IsNumeric: true } from || target is not PrimitiveType { IsNumeric: true } to) return null;

        if (ConstantFolder.Fold(value) is { } constant && ConstantFolder.Fits(constant, to))
            return new IrConversion(value.Span, to, value);

        if (!_IsLossless(from.Kind, to.Kind)) return null;

        if (!_probing)
            _diagnostics.Info(value.Span, DiagnosticCodes.ImplicitConversion, $"conversion implicite de « {from} » en « {to} »", $"converti en « {to} »",
            ["la conversion est sans perte : la valeur ne change pas"]);
        return new IrConversion(value.Span, to, value);
    }

    /// <summary>Vrai pendant qu'on essaie une conversion sans la garder : elle ne doit rien signaler.</summary>
    private bool _probing;

    /// <summary>Indique si <paramref name="value"/> se convertit en <paramref name="target"/>, sans rien signaler.</summary>
    private bool _CanCoerce(IrExpression value, IrType target)
    {
        // Une vraie erreur trouvée en chemin (une valeur par défaut fautive…) reste signalée : elle le serait de toute façon.
        var probing = _probing;
        _probing = true;
        var result = _Coerce(value, target) is not null;
        _probing = probing;
        return result;
    }

    /// <summary>Aide d'une erreur de type : pourquoi la conversion n'est pas implicite.</summary>
    private string _WhyNotConverted(IrType from, IrType to) =>
        from is NullableType nullable && (nullable.Inner == to || to is not NullableType)
            ? $"la valeur peut être null : vérifie-la avant (if (… not eq null) {{ … }}), ou donne une valeur de secours avec ?? (« {nullable} » vers « {to} »)"
            : from is NullType
            ? $"null n'est permis que dans un type nullable : déclare « {to}? »"
            :
        from is FunctionType fromFunction && to is FunctionType toFunction && fromFunction.Impure == toFunction.Impure
            ? "une lambda pure est attendue : celle-ci appelle un gear ou une lambda qui n'est pas pure, ou fait une entrée ou une sortie"
            : from is FunctionType && to is FunctionType
            ? "une lambda garde sa signature : mêmes types de paramètres, dans le même ordre, et même type de retour"
            : from is ArrayType fromArray && to is ArrayType toArray && fromArray.Element != toArray.Element
            ? $"les éléments d'un tableau gardent leur type : des « {fromArray.Element} » ne deviennent pas des « {toArray.Element} »"
            : from is TupleType && to is TupleType
            ? "un tuple garde le type de chacun de ses éléments"
            : from is IngotType fromIngot
            ? $"un « {fromIngot} » ne se mélange pas avec un autre type : retire sa marque avec °, par exemple : valeur°"
            : to is IngotType toIngot
            ? $"seule une constante entre seule dans un ingot : marque la valeur, par exemple : {toIngot}(valeur)"
            : from is PrimitiveType { IsNumeric: true } && to is PrimitiveType { IsNumeric: true }
            ? $"« {from} » vers « {to} » peut perdre de l'information : convertis avec as (contrôlé), as? (null si elle ne tient pas) ou as! (dans un guard)"
            : $"« {from} » ne se convertit pas en « {to} »";

    #endregion

    #region Type commun

    /// <summary>
    /// Les deux opérandes d'une opération ramenés à un même type : une constante prend le type de
    /// l'autre opérande si elle y tient, sinon les deux vont au plus petit type qui les contient
    /// sans perte (<c>int + long</c> donne <c>long</c>, <c>uint + int</c> donne <c>long</c>).
    /// </summary>
    /// <returns>Les opérandes convertis, ou <see langword="null"/> s'il n'existe pas de type commun (rien n'est signalé).</returns>
    private (IrExpression Left, IrExpression Right)? _Unify(IrExpression left, IrExpression right)
    {
        if (left.Type == right.Type) return (left, right);

        // Deux lambdas de même signature : le type commun est sans pure si l'une ne l'est pas.
        if (left.Type is FunctionType leftFunction && right.Type is FunctionType rightFunction)
            return leftFunction.Impure == rightFunction.Impure ? (_Coerce(left, leftFunction.Impure)!, _Coerce(right, leftFunction.Impure)!) : null;

        // null et une valeur : les deux deviennent nullables ; un T? et un T : le T devient nullable.
        if (left.Type is NullType || right.Type is NullType)
        {
            var other = left.Type is NullType ? right.Type : left.Type;
            if (other == PrimitiveType.Void) return null;
            var common = other as NullableType ?? new NullableType(other);
            return _Coerce(left, common) is { } l2 && _Coerce(right, common) is { } r2 ? (l2, r2) : null;
        }
        if (left.Type is NullableType leftNullable && right.Type is not NullableType)
            return _Coerce(right, leftNullable) is { } asLeftNullable ? (left, asLeftNullable) : null;
        if (right.Type is NullableType rightNullable && left.Type is not NullableType)
            return _Coerce(left, rightNullable) is { } asRightNullable ? (asRightNullable, right) : null;

        // Une union ou un résultat : l'autre valeur y entre (un membre, une union plus étroite, ok(…)).
        if (left.Type is UnionType or ResultType || right.Type is UnionType or ResultType)
        {
            if (_Coerce(right, left.Type) is { } intoLeft) return (left, intoLeft);
            if (_Coerce(left, right.Type) is { } intoRight) return (intoRight, right);
            return null;
        }

        // Deux tableaux de mêmes éléments : le type commun est [T] (aucun contrôle ; un [T] et un
        // [T;3] se comparent). Un tableau ou un tuple écrit peut aussi prendre le type de l'autre.
        if (left.Type is ArrayType leftArray && right.Type is ArrayType rightArray && leftArray.Element == rightArray.Element)
        {
            var dynamic = leftArray.Dynamic;
            return (_Coerce(left, dynamic)!, _Coerce(right, dynamic)!);
        }
        if (left.Type is ArrayType or DictionaryType or TupleType || right.Type is ArrayType or DictionaryType or TupleType)
        {
            if (_Coerce(right, left.Type) is { } intoLeft) return (left, intoLeft);
            if (_Coerce(left, right.Type) is { } intoRight) return (intoRight, right);
            return null;
        }

        // Un ingot : l'autre valeur doit être du même ingot, ou une constante qui y entre.
        if (left.Type is IngotType || right.Type is IngotType)
        {
            if (_Coerce(right, left.Type) is { } intoLeft) return (left, intoLeft);
            if (_Coerce(left, right.Type) is { } intoRight) return (intoRight, right);
            return null;
        }

        // Deux molds : l'un prend le type de l'autre par duck typing, mais sans perdre de champ en silence.
        if (left.Type is MoldType leftMold && right.Type is MoldType rightMold)
        {
            if (!_DropsFields(rightMold, leftMold) && _CoerceMold(right, leftMold) is { } asLeft) return (left, asLeft);
            if (!_DropsFields(leftMold, rightMold) && _CoerceMold(left, rightMold) is { } asRight) return (asRight, right);
            return null;
        }

        if (left.Type is not PrimitiveType { IsNumeric: true } l || right.Type is not PrimitiveType { IsNumeric: true } r) return null;

        if (ConstantFolder.Fold(right) is { } rightValue && ConstantFolder.Fits(rightValue, l))
            return (left, new IrConversion(right.Span, l, right));
        if (ConstantFolder.Fold(left) is { } leftValue && ConstantFolder.Fits(leftValue, r))
            return (new IrConversion(left.Span, r, left), right);

        foreach (var kind in NumericKinds)
        {
            if (!_IsLossless(l.Kind, kind) || !_IsLossless(r.Kind, kind)) continue;

            var common = new PrimitiveType(kind);
            return (_Coerce(left, common)!, _Coerce(right, common)!);
        }

        return null;
    }

    #endregion

    #region Nombres

    /// <summary>Types numériques du plus étroit au plus large : l'ordre dans lequel on cherche un type commun.</summary>
    private static readonly PrimitiveKind[] NumericKinds =
    [
        PrimitiveKind.Byte, PrimitiveKind.UByte, PrimitiveKind.Short, PrimitiveKind.UShort,
        PrimitiveKind.Int, PrimitiveKind.UInt, PrimitiveKind.Long, PrimitiveKind.ULong,
        PrimitiveKind.Float, PrimitiveKind.Double,
    ];

    /// <summary>
    /// Indique si toute valeur de <paramref name="from"/> s'écrit exactement en <paramref name="to"/>.
    /// Un entier ne devient pas non signé ; un non signé ne devient signé que dans un type plus
    /// large ; un flottant garde exactement les entiers de 16 bits (<c>float</c>) ou de 32 bits
    /// (<c>double</c>).
    /// </summary>
    private static bool _IsLossless(PrimitiveKind from, PrimitiveKind to)
    {
        if (from == to) return true;

        var (fromBits, fromSigned, fromFloat) = _Shape(from);
        var (toBits, toSigned, toFloat) = _Shape(to);

        if (fromFloat) return toFloat && toBits >= fromBits;
        if (toFloat) return fromBits <= (to == PrimitiveKind.Float ? 16 : 32);
        if (fromSigned) return toSigned && toBits >= fromBits;
        return toSigned ? toBits > fromBits : toBits >= fromBits;
    }

    /// <summary>Largeur en bits, signe et nature flottante d'un type numérique.</summary>
    private static (int Bits, bool Signed, bool Float) _Shape(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Byte => (8, true, false),
        PrimitiveKind.UByte => (8, false, false),
        PrimitiveKind.Short => (16, true, false),
        PrimitiveKind.UShort => (16, false, false),
        PrimitiveKind.Int => (32, true, false),
        PrimitiveKind.UInt => (32, false, false),
        PrimitiveKind.Long => (64, true, false),
        PrimitiveKind.ULong => (64, false, false),
        PrimitiveKind.Float => (32, true, true),
        PrimitiveKind.Double => (64, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Type non numérique."),
    };

    #endregion

    #region Types bornés

    /// <summary>
    /// Une valeur qui entre dans un type borné : convertie dans sa base, puis vérifiée — connue à la
    /// compilation, elle doit être dans les bornes ; sinon, elle est contrôlée à l'exécution, sauf
    /// si ses propres bornes y sont déjà.
    /// </summary>
    private IrExpression? _CoerceBounded(IrExpression value, PrimitiveType target)
    {
        if (_Coerce(value, target.Unbounded) is not { } inBase) return null;

        if (ConstantFolder.Fold(inBase) is { } known)
            return target.Bounds!.Contains(known) ? new IrRetype(value.Span, target, inBase) : null;
        if (value.Type is PrimitiveType { Bounds: { } own } && target.Bounds!.Contains(own))
            return new IrRetype(value.Span, target, inBase);
        return new IrBoundCheck(value.Span, target, inBase);
    }

    /// <summary>La valeur d'un type borné vue comme sa base ; une autre valeur telle quelle.</summary>
    private static IrExpression _Unbounded(IrExpression value) =>
        value.Type is PrimitiveType { Bounds: not null } bounded ? new IrRetype(value.Span, bounded.Unbounded, value) : value;

    /// <summary>
    /// La valeur connue qui sort des bornes du type attendu (lui-même, ou sous un nullable ou un
    /// ingot), écrite comme en RGL ; <see langword="null"/> si ce n'est pas le cas.
    /// </summary>
    private static string? _OutOfBounds(IrExpression value, IrType target)
    {
        while (target is NullableType or IngotType)
            target = target is NullableType nullable ? nullable.Inner : ((IngotType)target).Base;
        if (target is not PrimitiveType { Bounds: { } bounds }) return null;

        return ConstantFolder.Fold(value) is { } known and (long or double) && !bounds.Contains(known) ? _ValueText(known) : null;
    }

    #endregion

    #region Molds

    /// <summary>
    /// Une valeur de mold dans le type mold <paramref name="target"/>, par duck typing : chaque champ
    /// de la cible vient du champ de même nom de la valeur (converti si besoin), ou de sa valeur par
    /// défaut ; les champs en plus de la valeur sont laissés, sauf ceux écrits dans une valeur <c>{…}</c>.
    /// </summary>
    /// <returns>La valeur construite, ou <see langword="null"/> si elle ne convient pas (rien n'est signalé).</returns>
    private IrExpression? _CoerceMold(IrExpression value, MoldType target)
    {
        if (value.Type is not MoldType source) return null;
        if (source == target) return value;
        // Une valeur à cycle de vie ne devient pas un autre mold : ses champs seraient copiés. Seule la valeur écrite {…} prend le type.
        if (LifecycleRules.HasMold(target.Symbol) && value is not IrMoldValue { ExplicitFields: not null }) return null;

        IReadOnlyList<IrExpression> parts;
        Func<MoldFieldSymbol, IrExpression?> available;
        if (value is IrMoldValue { ExplicitFields: { } written } literal)
        {
            if (written.Any(name => target.Symbol[name] is null)) return null;
            parts = literal.Parts;
            available = field => source.Symbol[field.Name] is { } own ? literal.Fields[_FieldIndex(source.Symbol, own)] : null;
        }
        else
        {
            // Une valeur déjà calculée : lue une fois, sauf un simple nom.
            var held = value is IrVariableReference or IrParameterReference ? value : new IrPart(value.Span, value);
            parts = held is IrPart ? [value] : [];
            available = field => source.Symbol[field.Name] is { } own ? new IrMemberAccess(value.Span, held, own) : null;
        }

        var fields = new IrExpression[target.Symbol.Fields.Count];
        for (var i = 0; i < fields.Length; ++i)
        {
            var field = target.Symbol.Fields[i];
            if (available(field) is { } given)
            {
                if (_Coerce(given, field.Type) is not { } converted) return null;
                fields[i] = converted;
            }
            else if (field.HasDefault && _DefaultOf(field) is { } fallback) fields[i] = fallback;
            else return null;
        }

        return new IrMoldValue(value.Span, target, parts, fields, null);
    }

    /// <summary>Indique si une valeur de <paramref name="from"/> perdrait des champs en devenant un <paramref name="to"/> (duck typing).</summary>
    private static bool _DropsFields(IrType from, IrType to) =>
        from is MoldType { Symbol: var source } && to is MoldType { Symbol: var target } && source.Fields.Any(f => target[f.Name] is null);

    /// <summary>
    /// Pourquoi une valeur de mold ne va pas dans <paramref name="target"/> : le premier champ en cause,
    /// jugé comme le fait <see cref="_CoerceMold"/> (une constante qui tient dans le type du champ y va).
    /// </summary>
    private string _WhyNotMold(IrExpression value, MoldType target)
    {
        if (value.Type is not MoldType source) return $"« {value.Type} » n'est pas un mold";

        var literal = value as IrMoldValue;
        if (literal is { ExplicitFields: { } written } && written.FirstOrDefault(name => target.Symbol[name] is null) is { } extra)
            return $"« {target} » n'a pas de champ « {extra} » (faute de frappe ?)";

        foreach (var field in target.Symbol.Fields)
        {
            if (source.Symbol[field.Name] is not { } own)
            {
                if (!field.HasDefault) return $"il manque le champ « {field.Name} », qui n'a pas de valeur par défaut dans « {target} »";
                continue;
            }

            // La valeur écrite elle-même quand on l'a, sinon une valeur quelconque du type du champ.
            var given = literal is { ExplicitFields: not null } ? literal.Fields[_FieldIndex(source.Symbol, own)] : new IrMemberAccess(value.Span, value, own);
            if (_CanCoerce(given, field.Type)) continue;

            return own.Type is PrimitiveType { IsNumeric: true } && field.Type is PrimitiveType { IsNumeric: true }
                ? $"le champ « {field.Name} » : {_WhyNotConverted(own.Type, field.Type)}"
                : $"le champ « {field.Name} » est un « {own.Type} », et « {target} » attend un « {field.Type} »";
        }
        return $"« {value.Type} » ne se convertit pas en « {target} »";
    }

    #endregion

    #region Unions et résultats

    /// <summary>
    /// Une valeur dans une union : une union plus étroite s'y élargit ; sinon, le membre de même type,
    /// ou le seul membre qui l'accepte (plusieurs : ambigu, refusé).
    /// </summary>
    private IrExpression? _CoerceUnion(IrExpression value, UnionType target)
    {
        if (value.Type is UnionType source)
            return source.Members.All(m => target.IndexOf(m) >= 0) ? new IrUnionWiden(value.Span, target, value) : _CoerceMembers(value, target);

        var exact = target.IndexOf(value.Type);
        if (exact >= 0) return new IrToUnion(value.Span, target, exact, value);

        var candidates = _UnionCandidates(value, target);
        if (candidates is not [var only]) return null;
        return _Coerce(value, target.Members[only]) is { } converted ? new IrToUnion(value.Span, target, only, converted) : null;
    }

    /// <summary>
    /// Une union d'erreurs reçue par un type plus étroit (un handler <c>onError(Exception e)</c>) : chaque
    /// membre doit être accepté par le type attendu, par duck typing, et perd alors les champs en trop.
    /// Seules les unions d'erreurs sont concernées.
    /// </summary>
    private IrExpression? _CoerceMembers(IrExpression value, IrType target)
    {
        if (value.Type is not UnionType { Members: var members } || !members.All(_IsError)) return null;

        var branches = new List<IrExpression>();
        foreach (var member in members)
        {
            if (_Coerce(new IrUnionMemberValue(value.Span, member), target) is not { } branch) return null;
            branches.Add(branch);
        }
        return new IrUnionConvert(value.Span, target, value, branches);
    }

    /// <summary>
    /// Les membres de l'union qui acceptent la valeur ; s'il y en a plusieurs, seuls ceux qui la
    /// prennent sans lui retirer de champ (un mold qui a tous ses champs l'emporte sur un mold plus petit).
    /// </summary>
    private int[] _UnionCandidates(IrExpression value, UnionType target)
    {
        var candidates = Enumerable.Range(0, target.Members.Count).Where(i => _CanCoerce(value, target.Members[i])).ToArray();
        if (candidates.Length < 2) return candidates;
        var whole = candidates.Where(i => !_DropsFields(value.Type, target.Members[i])).ToArray();
        return whole.Length > 0 ? whole : candidates;
    }

    /// <summary>
    /// Une valeur dans un résultat : <c>ok(v)</c> ou <c>err(e)</c> prennent ses types, et une valeur seule
    /// devient un succès ou une erreur si un seul des deux côtés l'accepte.
    /// </summary>
    private IrExpression? _CoerceResult(IrExpression value, ResultType target)
    {
        if (value is IrResultValue { Result.IsPending: true } partial)
        {
            var side = partial.IsOk ? target.Value : target.Error;
            return _Coerce(partial.Value, side) is { } inner ? new IrResultValue(value.Span, target, partial.IsOk, inner) : null;
        }
        if (value.Type is ResultType) return null;

        var asValue = _CanCoerce(value, target.Value);
        var asError = _CanCoerce(value, target.Error);
        if (asValue == asError) return null;   // aucun côté, ou les deux : il faut ok(…) ou err(…)

        var converted = _Coerce(value, asValue ? target.Value : target.Error);
        return converted is null ? null : new IrResultValue(value.Span, target, asValue, converted);
    }

    /// <summary>Pourquoi une valeur ne va pas dans une union ou un résultat.</summary>
    private string _WhyNotUnion(IrExpression value, IrType target)
    {
        switch (target)
        {
            case UnionType union when value.Type is UnionType source:
                return $"« {string.Join(", ", source.Members.Where(m => union.IndexOf(m) < 0))} » n'est pas dans « {union} »";

            case UnionType union:
                var candidates = _UnionCandidates(value, union).Select(i => union.Members[i]).ToArray();
                return candidates.Length > 1
                    ? $"ambigu : la valeur peut être un {string.Join(" ou un ", candidates)} ; donne-lui un type précis, par exemple : new {candidates[0]} {{…}}"
                    : $"« {value.Type} » n'est aucun des types de « {union} »";

            case ResultType result when value is IrResultValue partial:
                return $"{(partial.IsOk ? "le succès" : "l'erreur")} de « {result} » est un « {(partial.IsOk ? result.Value : result.Error)} »";

            case ResultType result:
                return _CanCoerce(value, result.Value) && _CanCoerce(value, result.Error)
                    ? $"ambigu : la valeur peut être le succès ou l'erreur de « {result} » : écris ok(…) ou err(…)"
                    : $"« {value.Type} » n'est ni le succès ni l'erreur de « {result} »";

            default:
                return $"« {value.Type} » ne se convertit pas en « {target} »";
        }
    }

    #endregion

    #region Collections

    /// <summary>
    /// Un tableau dans une sorte de tableau, de mêmes éléments. Vers <c>[T]</c>, ou quand la taille de
    /// la source est déjà permise, rien n'est contrôlé ; sinon, la taille l'est à l'exécution (arrêt,
    /// ou échec du guard). Un tableau écrit prend lui-même le type attendu, élément par élément, et
    /// son nombre d'éléments, s'il est connu, doit être permis.
    /// </summary>
    private IrExpression? _CoerceArray(IrExpression value, ArrayType target)
    {
        if (value.Type is not ArrayType source) return null;

        if (value is IrArrayValue literal)
        {
            var parts = new List<IrArrayPart>();
            foreach (var part in literal.Parts)
            {
                if (part.IsSpread)
                {
                    if (((ArrayType)part.Value.Type).Element != target.Element) return null;
                    parts.Add(part);
                }
                else if (_Coerce(part.Value, target.Element) is { } converted) parts.Add(part with { Value = converted });
                else return null;
            }

            var dynamic = new IrArrayValue(literal.Span, new ArrayType(target.Element, Packed: target.Packed), parts);
            if (target.Kind == ArrayKind.Dynamic) return dynamic;
            if (!dynamic.HasKnownCount) return new IrArrayConvert(value.Span, target, dynamic, target, ArrayConversion.Checked);
            return target.Fits((ulong)parts.Count) ? dynamic with { Array = target } : null;
        }

        if (source.Element != target.Element) return null;
        if (target.Packed && !source.Packed && source.Kind == ArrayKind.Dynamic && source.Element is MoldType) return null;
        var mode = target.Kind == ArrayKind.Dynamic || target.Contains(source) ? ArrayConversion.Safe : ArrayConversion.Checked;
        return new IrArrayConvert(value.Span, target, value, target, mode);
    }

    /// <summary>Un dictionnaire écrit prend le type attendu, clé par clé et valeur par valeur ; un autre doit déjà l'avoir.</summary>
    private IrExpression? _CoerceDictionary(IrExpression value, DictionaryType target)
    {
        if (value is not IrDictionaryValue literal) return null;

        var entries = new List<(IrExpression Key, IrExpression Value)>();
        foreach (var (key, entry) in literal.Entries)
        {
            if (_Coerce(key, target.Key) is not { } convertedKey || _Coerce(entry, target.Value) is not { } convertedValue) return null;
            entries.Add((convertedKey, convertedValue));
        }
        return new IrDictionaryValue(literal.Span, target, entries);
    }

    /// <summary>
    /// Un tuple écrit prend le type attendu (et ses noms), élément par élément ; un autre doit avoir les
    /// mêmes types d'éléments, ses noms important peu.
    /// </summary>
    private IrExpression? _CoerceTuple(IrExpression value, TupleType target)
    {
        if (value is not IrTupleValue literal || literal.Elements.Count != target.Elements.Count) return null;

        var elements = new List<IrExpression>();
        for (var i = 0; i < literal.Elements.Count; ++i)
        {
            if (_Coerce(literal.Elements[i], target.Elements[i]) is not { } converted) return null;
            elements.Add(converted);
        }
        return new IrTupleValue(literal.Span, target, elements);
    }

    #endregion

    #region Ingots

    /// <summary>
    /// Une valeur qui entre dans un ingot sans être marquée : une valeur du même ingot, ou une
    /// constante de la base (<c>UserId uid: 42;</c>). Une autre valeur de la base doit être
    /// marquée (<c>UserId(v)</c>).
    /// </summary>
    private IrExpression? _CoerceIngot(IrExpression value, IngotType ingot)
    {
        if (value.Type is IngotType || ConstantFolder.Fold(value) is null) return null;
        return _Coerce(value, ingot.Base) is { } inBase ? new IrRetype(value.Span, ingot, inBase) : null;
    }

    #endregion
}
