using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.Ingots;
using Rgl.AST.Variables;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Les ingots : un nom posé sur n'importe quel type, qui donne une autre identité à la même
/// valeur. Un ingot ne se mélange ni avec sa base ni avec un autre ingot : <c>°</c> lui retire sa
/// marque, <c>UserId(v)</c> et <c>°&lt;-</c> marquent une valeur de la base, et seule une constante y
/// entre sans rien écrire. Un calcul garde la marque (<c>uid + 1</c> est un <c>UserId</c>) ; les champs
/// d'un ingot de mold se lisent directement.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Les ingots déclarés, par nom.</summary>
    private readonly Dictionary<string, IngotSymbol> _ingots = new(StringComparer.Ordinal);

    /// <summary>La déclaration de chaque ingot.</summary>
    private readonly Dictionary<IngotSymbol, IngotDeclaration> _ingotDeclarations = [];

    /// <summary>Les ingots dont le nom est déjà comparé à ceux des gears.</summary>
    private readonly HashSet<IngotSymbol> _ingotNamesChecked = [];

    /// <summary>Les ingots dont la base est en cours de résolution (pour trouver les cycles) ou résolue.</summary>
    private readonly HashSet<IngotSymbol> _resolvingIngots = [];
    private readonly HashSet<IngotSymbol> _resolvedIngots = [];

    #region Déclarations

    /// <summary>
    /// Les noms des ingots, avant les molds et les gears qui les utilisent : nom unique parmi les
    /// types (E075). Leurs bases sont liées une fois les noms des molds connus.
    /// </summary>
    private void _DeclareIngots(IEnumerable<IngotDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            var name = declaration.Name.Name;
            if (!_ClaimTypeName(declaration.Name, "un ingot")) continue;

            var symbol = new IngotSymbol(name, declaration.Name.Span);
            _ingots[_DeclKey(name)] = symbol;
            _ingotDeclarations[symbol] = declaration;
        }
    }

    /// <summary>Les bases de tous les ingots.</summary>
    private void _ResolveIngots()
    {
        foreach (var symbol in _ingotDeclarations.Keys) _ResolveIngot(symbol);
    }

    /// <summary>
    /// La base d'un ingot. Un ingot vaut sa base en C++ : il ne peut pas la contenir, même dans un
    /// tableau ou par un autre ingot (E089).
    /// </summary>
    private void _ResolveIngot(IngotSymbol symbol)
    {
        if (_resolvedIngots.Contains(symbol) || !_resolvingIngots.Add(symbol)) return;

        var declaration = _ingotDeclarations[symbol];
        if (_BindType(declaration.Base) is { } type)
        {
            if (_MentionsIngot(type, symbol))
                _Error(declaration.Name.Span, DiagnosticCodes.InvalidIngot, $"l'ingot « {symbol.Name} » repose sur lui-même", "se contient",
                    "un ingot est sa base sous un autre nom : sa base ne peut pas le contenir, même dans un tableau ; pour une structure récursive, utilise un mold");
            else symbol.Base = type;
        }

        _resolvingIngots.Remove(symbol);
        _resolvedIngots.Add(symbol);
    }

    /// <summary>Indique si <paramref name="type"/> contient l'ingot <paramref name="target"/>, directement ou par d'autres ingots.</summary>
    private bool _MentionsIngot(IrType type, IngotSymbol target) => type switch
    {
        IngotType { Symbol: var symbol } when symbol == target || _resolvingIngots.Contains(symbol) => true,
        IngotType { Symbol: var symbol } => _Resolved(symbol) && _MentionsIngot(symbol.Base, target),
        NullableType nullable => _MentionsIngot(nullable.Inner, target),
        ArrayType array => _MentionsIngot(array.Element, target),
        DictionaryType dictionary => _MentionsIngot(dictionary.Key, target) || _MentionsIngot(dictionary.Value, target),
        TupleType tuple => tuple.Elements.Any(e => _MentionsIngot(e, target)),
        UnionType union => union.Members.Any(m => _MentionsIngot(m, target)),
        ResultType result => _MentionsIngot(result.Value, target) || _MentionsIngot(result.Error, target),
        _ => false,
    };

    /// <summary>Résout un ingot au besoin ; vrai s'il a une base.</summary>
    private bool _Resolved(IngotSymbol symbol)
    {
        _ResolveIngot(symbol);
        return symbol.Base != PrimitiveType.Void;
    }

    /// <summary><c>UserId(42)</c> marque une valeur : un gear du même nom rendrait l'appel ambigu (E089).</summary>
    private void _CheckIngotNames()
    {
        foreach (var (key, symbol) in _ingots.ToArray())
        {
            if (!_ingotNamesChecked.Add(symbol)) continue;
            if (_gears.ContainsKey(key))
                _Error(symbol.Declaration, DiagnosticCodes.InvalidIngot, $"un gear s'appelle déjà « {symbol.Name} »", "nom déjà pris",
                    $"{symbol.Name}(valeur) marque une valeur de l'ingot : un gear ne peut pas porter le même nom");
        }
    }

    #endregion

    #region Marques

    /// <summary>La valeur d'un ingot dans le type de sa base (une marque de moins).</summary>
    private static IrExpression _Unbrand(IrExpression value) =>
        new IrRetype(value.Span, ((IngotType)value.Type).Base, value);

    /// <summary>La valeur sans aucune marque : un ingot posé sur un ingot perd les deux.</summary>
    private static IrExpression _Unbranded(IrExpression value)
    {
        while (value.Type is IngotType) value = _Unbrand(value);
        return value;
    }

    /// <summary>
    /// Le résultat d'un calcul sur la base d'un ingot, marqué à nouveau ; une valeur entrant dans
    /// une base bornée y est contrôlée. Un résultat d'un autre type (une comparaison) reste tel quel.
    /// </summary>
    private IrExpression _Rebrand(IrExpression result, IngotType ingot)
    {
        var plain = ingot.Base is PrimitiveType primitive ? primitive.Unbounded : ingot.Base;
        if (result.Type != plain && result.Type != ingot.Base) return result;
        return _Coerce(result, ingot.Base) is { } inBase ? new IrRetype(result.Span, ingot, inBase) : result;
    }

    /// <summary><c>uid°</c> : la valeur d'un ingot sans sa marque (E088 si ce n'est pas un ingot).</summary>
    private IrExpression? _BindUnbrand(UnbrandExpression unbrand)
    {
        var value = _BindExpression(unbrand.Value);
        if (value is null) return null;
        if (value.Type is IngotType) return _Unbrand(value) with { Span = unbrand.Span };

        _Error(unbrand.Span, DiagnosticCodes.NotAnIngot, $"« ° » retire la marque d'un ingot, et un « {value.Type} » n'en a pas", "pas un ingot",
            value.Type is NullableType { Inner: IngotType }
                ? "la valeur peut être null : vérifie-la avant (if (… not eq null) { … }), ou donne une valeur de secours avec ??"
                : "° s'écrit après une valeur d'ingot, par exemple : uid°");
        return null;
    }

    /// <summary><c>UserId(42)</c> : une valeur de la base de l'ingot, marquée (E058 sans exactement une valeur).</summary>
    private IrExpression? _BindIngotConstruction(CallExpression call, IngotSymbol symbol)
    {
        var ingot = new IngotType(symbol);
        var arguments = call.Arguments.Select(_BindExpression).ToArray();
        if (arguments.Length != 1)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"« {symbol.Name}(…) » marque une valeur, et en reçoit {arguments.Length}", "une seule valeur attendue",
                $"par exemple : {symbol.Name}(valeur)");
            return null;
        }

        if (arguments[0] is not { } value) return null;
        if (value.Type == ingot)
            _diagnostics.Warning(call.Span, DiagnosticCodes.UselessCast, $"la valeur est déjà un « {ingot} »", "déjà marquée", ["écris la valeur seule"]);
        var inBase = value.Type == ingot ? _Unbrand(value) : _ConvertTo(value, ingot.Base, $"« {ingot} » repose sur « {ingot.Base} »");
        return inBase is null ? null : new IrRetype(call.Span, ingot, inBase);
    }

    /// <summary>
    /// <c>uid °&lt;- 43</c> : une valeur de la base affectée à une variable d'ingot, sans la marquer
    /// d'abord (E088 si la variable n'est pas un ingot).
    /// </summary>
    private IrStatement? _BindBrandAssignment(AssignmentStatement assignment, VariableSymbol variable, IrExpression value)
    {
        if (variable.Type is not IngotType ingot)
        {
            _Error(assignment.Target.Span, DiagnosticCodes.NotAnIngot, $"« °<- » affecte une valeur à un ingot, et « {assignment.Target.Name} » est un « {variable.Type} »", "pas un ingot",
                $"pour une variable ordinaire, écris : {assignment.Target.Name} <- valeur;");
            return null;
        }

        var inBase = _ConvertTo(value, ingot.Base, $"« {assignment.Target.Name} » est un « {ingot} », qui repose sur « {ingot.Base} »");
        if (inBase is null) return null;
        var marked = new IrRetype(value.Span, ingot, inBase);
        _AfterAssignment(variable, marked);
        return new IrAssignment(assignment.Span, variable, marked);
    }

    /// <summary>
    /// Une opération dont un opérande est un ingot : l'autre est du même ingot ou une constante
    /// (E040 sinon) ; le calcul se fait sur la base, et son résultat reprend la marque, sauf pour
    /// une comparaison. Le nombre de bits d'un décalage garde son propre type.
    /// </summary>
    private IrExpression? _BindIngotOperation(SourceSpan span, SourceSpan operatorSpan, TokenKind op, IrExpression left, IrExpression right)
    {
        var ingot = (left.Type as IngotType ?? right.Type as IngotType)!;
        var isShift = op is TokenKind.ShlKeyword or TokenKind.ShrKeyword;
        bool Accepts(IrExpression value) => value.Type == ingot || (value.Type is not IngotType && ConstantFolder.Fold(value) is not null);

        if (!(Accepts(left) && Accepts(right)) && !(isShift && left.Type == ingot && right.Type is not IngotType))
        {
            var other = left.Type == ingot ? right : left;
            _Error(span, DiagnosticCodes.TypeMismatch, $"« {_Text(op)} » ne mélange pas « {left.Type} » et « {right.Type} »", $"« {left.Type} » {_Text(op)} « {right.Type} »",
                other.Type is IngotType
                    ? "deux ingots différents ne se mélangent pas, même sur la même base : retire leurs marques avec ° pour calculer sur la base"
                    : $"retire la marque avec ° pour calculer sur la base, ou marque l'autre valeur : {ingot}(…)");
            return null;
        }

        var result = _BindOperation(span, operatorSpan, op,
            left.Type == ingot ? _Unbrand(left) : left, right.Type == ingot ? _Unbrand(right) : right);
        if (result is null) return null;

        var comparison = op is TokenKind.EqKeyword or TokenKind.Less or TokenKind.Greater or TokenKind.LessEqual or TokenKind.GreaterEqual;
        return comparison ? result : _Rebrand(result, ingot);
    }

    #endregion
}
