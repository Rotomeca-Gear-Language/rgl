using Rgl.AST.Gears;
using Rgl.AST.Molds;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;

namespace Rgl.Binding;

/// <summary>
/// Les opérateurs et les conversions déclarés dans un mold (décisions : ils s'appellent comme des gears,
/// l'ordre des opérandes compte et rien n'est inversé tout seul, <c>not eq</c> se déduit de <c>eq</c>,
/// <c>&lt;=</c> de <c>&lt;</c> et <c>eq</c>, <c>&gt;=</c> de <c>&gt;</c> et <c>eq</c>, <c>&lt;</c> et <c>&gt;</c> vont
/// ensemble, un opérateur est <c>pure</c> s'il l'écrit, une conversion déclarée l'emporte sur le duck
/// typing). Chacun est un gear nommé « Mold.opérateur » ; les signatures sont liées avec celles des molds,
/// les corps avec ceux des gears.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Les opérateurs déclarés dont le corps reste à lier.</summary>
    private readonly List<(GearDeclaration Declaration, GearSymbol Symbol)> _pendingOverloads = [];

    /// <summary>Le nombre d'opérateurs déclarés : il numérote leur nom C++.</summary>
    private uint _overloadCount;

    /// <summary>Les opérateurs binaires que les molds peuvent surcharger, par leur jeton.</summary>
    private static readonly HashSet<TokenKind> _overloadableOperators =
    [
        TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent, TokenKind.Less, TokenKind.Greater, TokenKind.EqKeyword,
        TokenKind.BitAndKeyword, TokenKind.BitOrKeyword, TokenKind.BitXorKeyword, TokenKind.ShlKeyword, TokenKind.ShrKeyword,
    ];

    #region Déclarations

    /// <summary>
    /// Les opérateurs et les conversions des molds de <paramref name="declarations"/> : leurs signatures sont liées
    /// et rangées sur le mold, leurs corps attendent <see cref="_BindOverloadBodies"/>.
    /// </summary>
    private void _DeclareOverloads(IEnumerable<MoldDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            var operators = declaration.Members.OfType<MoldOperator>().ToArray();
            if (operators.Length == 0) continue;
            if (declaration.TypeParameters is not null)
            {
                _Unsupported(operators[0].OperatorSpan, "un opérateur ou une conversion dans un mold générique");
                continue;
            }
            if (_molds.GetValueOrDefault(_DeclKey(declaration.Name.Name)) is not { } mold || mold.Declaration != declaration.Name.Span) continue;

            foreach (var overload in operators) _DeclareOverload(mold, overload);
            _CheckComparisonPairs(mold);
        }
    }

    private void _DeclareOverload(MoldSymbol mold, MoldOperator overload)
    {
        if (overload.Operator == "[]")
        {
            _DeclareIndexer(mold, overload);
            return;
        }

        var text = overload.Operator;
        var isConversion = text is "implicit" or "explicit";
        var expected = isConversion ? new[] { 1 } : text == "-" ? [1, 2] : [2];
        var count = overload.Parameters.Count;
        if (!expected.Contains(count))
        {
            _Error(overload.OperatorSpan, DiagnosticCodes.InvalidOverload,
                $"« {text} » attend {string.Join(" ou ", expected)} paramètre(s), pas {count}", $"{count} paramètre(s)",
                isConversion ? $"une conversion reçoit la valeur à convertir : {mold.Name} {text}({mold.Name} v) {{ … }}"
                : $"un opérateur reçoit ses opérandes : {mold.Name} {text}({mold.Name} a, {mold.Name} b) {{ … }}");
            return;
        }
        if (overload.Parameters.FirstOrDefault(p => p.IsMutable) is { } mutable)
        {
            _Error(mutable.Span, DiagnosticCodes.InvalidOverload, $"le paramètre « {mutable.Name.Name} » de « {text} » ne peut pas être mut", "mut",
                "un opérateur ou une conversion calcule une valeur sans modifier ses opérandes");
            return;
        }

        if (overload.Parameters.FirstOrDefault(p => p.IsParams) is { } withParams)
        {
            _RejectParams(withParams, $"« {text} »");
            return;
        }
        if (overload.Parameters.FirstOrDefault(p => p.Default is not null) is { } withDefault)
        {
            _RejectDefault(withDefault, $"« {text} »");
            return;
        }

        var name = $"{mold.Name}.{text}";
        var gear = new GearDeclaration(overload.Span, TokenKind.GearKeyword, overload.IsPure, new AST.Identifier(overload.OperatorSpan), overload.Parameters, overload.ReturnType, overload.Body, null);
        if (_BindGearSignature(gear, name, name, $"{mold.CppName}_op{_overloadCount++}") is not { } symbol) return;

        var types = symbol.Parameters.Select(p => p.Type).ToArray();
        var own = isConversion ? types[0] is MoldType { Symbol: var first } && first == mold : types.Any(t => t is MoldType { Symbol: var m } && m == mold);
        if (!own)
        {
            _Error(overload.OperatorSpan, DiagnosticCodes.InvalidOverload,
                isConversion ? $"la conversion « {text} » de « {mold.Name} » doit recevoir un « {mold.Name} »" : $"l'opérateur « {text} » de « {mold.Name} » doit avoir un « {mold.Name} » parmi ses opérandes", $"pas de {mold.Name}",
                "un opérateur ou une conversion se déclare dans le mold dont il traite les valeurs");
            return;
        }

        var problem = text switch
        {
            "eq" or "<" or ">" when symbol.ReturnType != PrimitiveType.Bool => $"« {text} » renvoie un bool, pas un « {symbol.ReturnType} »",
            _ when symbol.ReturnType == PrimitiveType.Void => $"« {text} » doit renvoyer une valeur",
            "implicit" or "explicit" when symbol.ReturnType == types[0] => $"« {text} » doit convertir en un autre type que « {mold.Name} »",
            _ => null,
        };
        if (problem is null && mold.Overloads.Any(o => o.Operator == text && (isConversion ? o.Gear.ReturnType == symbol.ReturnType : o.Gear.Parameters.Select(p => p.Type).SequenceEqual(types))))
            problem = isConversion ? $"« {mold.Name} » a déjà une conversion « {text} » vers « {symbol.ReturnType} »" : $"« {text} » est déjà défini pour ({string.Join(", ", types)})";
        if (problem is not null)
        {
            _Error(overload.OperatorSpan, DiagnosticCodes.InvalidOverload, problem, "invalide", "chaque opérateur se déclare une fois par type d'opérandes");
            return;
        }

        mold.Overloads.Add(new MoldOverload(text, symbol));
        _pendingOverloads.Add((gear, symbol));
    }

    /// <summary>
    /// L'opérateur d'index d'un mold. La lecture <c>[pure] T [P i] { … }</c> est le gear « Mold.[] » de paramètres
    /// (<c>this</c>, <c>i</c>) ; l'écriture <c>mut T [P i] { … }</c> est « Mold.[]= », de paramètres (<c>mut this</c>, <c>i</c>,
    /// <c>value</c>) et qui ne renvoie rien. <c>this</c> est le mold lui-même, <c>value</c> la valeur écrite.
    /// </summary>
    private void _DeclareIndexer(MoldSymbol mold, MoldOperator overload)
    {
        var write = overload.IsMutable;
        var text = write ? "[]=" : "[]";
        var written = overload.Parameters[0];
        if (written.IsMutable)
        {
            _Error(written.Span, DiagnosticCodes.InvalidOverload, $"le paramètre « {written.Name.Name} » d'un index ne peut pas être mut", "mut", "un index se calcule sans modifier ce qui le désigne");
            return;
        }
        if (written.IsParams)
        {
            _RejectParams(written, "l'opérateur d'index");
            return;
        }
        if (written.Default is not null)
        {
            _RejectDefault(written, "l'opérateur d'index");
            return;
        }
        if (write && overload.IsPure)
        {
            _Error(overload.OperatorSpan, DiagnosticCodes.InvalidOverload, $"l'écriture d'un index de « {mold.Name} » modifie le mold : elle ne peut pas être pure", "pure", "retire pure, ou écris la lecture : pure T [int i] { … }");
            return;
        }
        if (_BindType(written.Type) is not { } indexType || _BindType(overload.ReturnType) is not { } valueType) return;
        if (valueType == PrimitiveType.Void)
        {
            _Error(overload.ReturnType.Span, DiagnosticCodes.InvalidOverload, "l'index renvoie une valeur (ou, en écriture, reçoit une valeur) : void ne convient pas", "void", "écris le type des éléments, par exemple : pure int [int i] { … }");
            return;
        }

        var parameters = new List<ParameterSymbol> { new("this", new MoldType(mold), overload.OperatorSpan, write), new(written.Name.Name, indexType, written.Name.Span, false) };
        if (write) parameters.Add(new ParameterSymbol("value", valueType, overload.ReturnType.Span, false));
        if (mold.Overloads.Any(o => o.Operator == text && o.Gear.Parameters[1].Type == indexType))
        {
            _Error(overload.OperatorSpan, DiagnosticCodes.InvalidOverload, $"« {text} » est déjà défini pour « {mold.Name} » avec un index « {indexType} »", "déjà défini", "chaque index se déclare une fois par type d'index");
            return;
        }

        var symbol = new GearSymbol($"{mold.Name}.{text}", GearKind.Gear, [.. parameters], write ? PrimitiveType.Void : valueType, overload.IsPure, overload.OperatorSpan, null)
        {
            CppNameOverride = $"{mold.CppName}_op{_overloadCount++}",
        };
        var gear = new GearDeclaration(overload.Span, TokenKind.GearKeyword, overload.IsPure, new AST.Identifier(overload.OperatorSpan), [], overload.ReturnType, overload.Body, null);
        mold.Overloads.Add(new MoldOverload(text, symbol));
        _pendingOverloads.Add((gear, symbol));
    }

    /// <summary><c>&lt;</c> et <c>&gt;</c> vont ensemble : chacun demande l'autre, pour les mêmes opérandes (E010).</summary>
    private void _CheckComparisonPairs(MoldSymbol mold)
    {
        foreach (var overload in mold.Overloads.Where(o => o.Operator is "<" or ">"))
        {
            var other = overload.Operator == "<" ? ">" : "<";
            var types = overload.Gear.Parameters.Select(p => p.Type);
            if (mold.Overloads.Any(o => o.Operator == other && o.Gear.Parameters.Select(p => p.Type).SequenceEqual(types))) continue;
            _Error(overload.Gear.Declaration, DiagnosticCodes.ComparisonPair, $"« {overload.Operator} » est défini pour « {mold.Name} » sans « {other} »", $"il manque « {other} »",
                $"« < » et « > » vont ensemble : écris aussi bool {other}({string.Join(", ", overload.Gear.Parameters.Select(p => $"{p.Type} {p.Name}"))}) {{ … }}");
        }
    }

    /// <summary>Lie les corps des opérateurs déclarés, dans la portée du fichier comme ceux des gears.</summary>
    private void _BindOverloadBodies(RArray<IrGear> gears)
    {
        var fileScope = _scope;
        foreach (var (declaration, symbol) in _pendingOverloads.ToArray())
        {
            _scope = new Scope(fileScope);
            if (_BindGearBody(declaration, symbol) is { } bound) gears.Push(bound);
        }
        _scope = fileScope;
        _pendingOverloads.Clear();
    }

    #endregion

    #region Appels

    /// <summary>
    /// L'opérateur surchargé qui convient à <paramref name="operands"/> : parmi ceux de leurs molds, ceux dont les
    /// paramètres sont exactement leurs types, sinon ceux où chacun se convertit. Plus d'un est E009.
    /// </summary>
    /// <param name="handled">Faux si aucun opérateur surchargé ne s'applique : l'opération est celle du langage. Vrai si l'appel est construit, ou une erreur signalée.</param>
    private IrExpression? _TryOverload(SourceSpan span, string op, IrExpression[] operands, out bool handled)
    {
        handled = false;
        var candidates = operands.Select(o => o.Type).OfType<MoldType>().Select(t => t.Symbol).Distinct().SelectMany(m => m.Overloads)
            .Where(o => o.Operator == op && o.Gear.Parameters.Count == operands.Length).ToArray();
        var matching = candidates.Where(o => o.Gear.Parameters.Select(p => p.Type).SequenceEqual(operands.Select(x => x.Type))).ToArray();
        if (matching.Length == 0)
            matching = [.. candidates.Where(o => operands.Zip(o.Gear.Parameters).All(pair => _CanCoerce(pair.First, pair.Second.Type)))];
        if (matching.Length == 0) return null;

        handled = true;
        if (matching.Length > 1)
        {
            _Error(span, DiagnosticCodes.AmbiguousOperator, $"« {op} » est ambigu pour ({string.Join(", ", operands.Select(o => o.Type))})", "ambigu",
                $"plusieurs définitions conviennent : {string.Join(", ", matching.Select(m => m.Gear.ToString()))}",
                "convertis un opérande avec as, ou retire l'une des définitions");
            return null;
        }

        var gear = matching[0].Gear;
        var arguments = new IrExpression[operands.Length];
        for (var i = 0; i < arguments.Length; ++i)
        {
            if (_ConvertTo(operands[i], gear.Parameters[i].Type, $"l'opérande {i + 1} de « {gear.Name} » est un « {gear.Parameters[i].Type} »") is not { } converted) return null;
            arguments[i] = converted;
        }
        return _OverloadCall(span, gear, arguments);
    }

    /// <summary>
    /// Un opérateur binaire surchargé sur des opérandes liés. <c>&lt;=</c> et <c>&gt;=</c> se déduisent de
    /// <c>&lt;</c> (ou <c>&gt;</c>) et de <c>eq</c> ; les opérandes sont alors lus deux fois : ils n'ont pas d'effet.
    /// </summary>
    private IrExpression? _TryBinaryOverload(SourceSpan span, SourceSpan operatorSpan, TokenKind op, IrExpression left, IrExpression right, out bool handled)
    {
        if (_overloadableOperators.Contains(op)) return _TryOverload(span, _Text(op), [left, right], out handled);

        handled = false;
        if (op is not (TokenKind.LessEqual or TokenKind.GreaterEqual)) return null;
        var strict = _TryOverload(span, op == TokenKind.LessEqual ? "<" : ">", [left, right], out handled);
        if (!handled || strict is null) return null;

        if (IrEffects.HasEffects(left) || IrEffects.HasEffects(right))
        {
            _Unsupported(span, $"« {_Text(op)} » sur des opérandes qui ont un effet : il lit chaque opérande deux fois, range-les d'abord dans des variables");
            return null;
        }
        return _BindOperation(span, operatorSpan, TokenKind.EqKeyword, left, right) is { } equal
            ? new IrBinary(span, PrimitiveType.Bool, IrBinaryOperator.Or, strict, equal, operatorSpan)
            : null;
    }

    /// <summary>
    /// L'ingot <c>index</c> de la bibliothèque standard : le type que reçoit un opérateur d'index, et le seul ingot où un
    /// nombre entre sans être écrit. Il est trouvé par son nom unique dans la std (std.Collections.index), comme un élément du langage ; un ingot index du fichier d'entrée le remplace.
    /// </summary>
    private IngotSymbol? _IndexIngot => _ingots.GetValueOrDefault("std.Collections.index") ?? _ingots.GetValueOrDefault("index");

    /// <summary>
    /// L'index <paramref name="position"/> donné à <c>m[i]</c>, converti au type <paramref name="target"/> que déclare l'opérateur : un
    /// entier se convertit implicitement vers l'ingot <c>index</c> (sa base est un <c>long</c>), et vers n'importe quel entier — arrêt
    /// du programme, ou échec du guard, si la valeur ne tient pas. Tout autre type suit les conversions ordinaires.
    /// </summary>
    private IrExpression? _IndexerArgument(IrExpression position, IrType target, string why)
    {
        var integer = _Unbounded(_Unbranded(position));
        if (integer.Type is not PrimitiveType { IsInteger: true } source) return _ConvertTo(position, target, why);

        var isIndex = target is IngotType ingot && ingot.Symbol == _IndexIngot;
        var baseType = isIndex ? ((IngotType)target).Underlying : target;
        if (baseType is not PrimitiveType { IsInteger: true, Bounds: null } wanted) return _ConvertTo(position, target, why);

        IrExpression converted;
        if (source == wanted) converted = integer;
        else if (_CanCoerce(integer, wanted)) converted = _ConvertTo(integer, wanted, why)!;
        else converted = new IrNumericCast(position.Span, wanted, integer, Checked: true);
        return isIndex ? new IrRetype(position.Span, target, converted) : converted;
    }

    /// <summary>Vrai si <paramref name="position"/> peut être donné à un opérateur d'index qui attend <paramref name="target"/>.</summary>
    private bool _IndexerArgumentFits(IrExpression position, IrType target)
    {
        if (_Unbounded(_Unbranded(position)).Type is PrimitiveType { IsInteger: true })
        {
            var baseType = target is IngotType ingot && ingot.Symbol == _IndexIngot ? ingot.Underlying : target;
            if (baseType is PrimitiveType { IsInteger: true, Bounds: null }) return true;
        }
        return _CanCoerce(position, target);
    }

    /// <summary>L'opérateur d'index <paramref name="operator"/> de <paramref name="mold"/> qui convient à <paramref name="position"/> : l'index de type exact, sinon le premier qui l'accepte.</summary>
    private MoldOverload? _FindIndexer(MoldType mold, string @operator, IrExpression position)
    {
        var candidates = mold.Symbol.Overloads.Where(o => o.Operator == @operator).ToArray();
        return candidates.FirstOrDefault(o => o.Gear.Parameters[1].Type == position.Type) ?? candidates.FirstOrDefault(o => _IndexerArgumentFits(position, o.Gear.Parameters[1].Type));
    }

    /// <summary><c>m[i]</c> sur un mold qui déclare la lecture d'un index : l'appel de cet opérateur.</summary>
    private IrExpression? _BindIndexerRead(SourceSpan span, IrExpression target, IrExpression position, MoldType mold)
    {
        var readers = mold.Symbol.Overloads.Where(o => o.Operator == "[]").ToArray();
        if (readers.Length == 0)
        {
            _Error(span, DiagnosticCodes.TypeMismatch, $"« {mold} » ne déclare pas la lecture d'un index", "pas de lecture", "déclare-la dans le mold : pure T [int i] { … }");
            return null;
        }
        if (_FindIndexer(mold, "[]", position) is not { } reader)
        {
            _Error(position.Span, DiagnosticCodes.TypeMismatch, $"« {mold} » ne se lit pas par un index « {position.Type} »", $"de type « {position.Type} »",
                $"index acceptés : {string.Join(", ", readers.Select(o => o.Gear.Parameters[1].Type))}");
            return null;
        }

        var type = reader.Gear.Parameters[1].Type;
        return _IndexerArgument(position, type, $"l'index de « {mold} » est un « {type} »") is { } argument ? _OverloadCall(span, reader.Gear, [target, argument]) : null;
    }

    /// <summary>
    /// <c>m[i] &lt;- v</c> sur un mold qui déclare l'écriture d'un index : l'appel de cet opérateur, dont <c>this</c> est la variable
    /// <c>mut</c> à modifier. Une affectation composée (<c>+&lt;-</c>) n'est pas encore prise en charge.
    /// </summary>
    /// <param name="handled">Faux si la cible n'est pas l'index d'un mold qui s'écrit : l'affectation est celle d'un contenu.</param>
    private IrStatement? _TryBindIndexerWrite(AST.Variables.FieldAssignmentStatement assignment, out bool handled)
    {
        handled = false;
        if (assignment.Target is not AST.Collections.IndexExpression index) return null;

        var receiver = _BindExpression(index.Target);
        if (receiver is null)
        {
            handled = true;
            _BindExpression(assignment.Value);
            return null;
        }
        if (_Unbranded(receiver).Type is not MoldType mold || !mold.Symbol.Overloads.Any(o => o.Operator == "[]=")) return null;

        handled = true;
        var position = _BindExpression(index.Index);
        if (position is null)
        {
            _BindExpression(assignment.Value);
            return null;
        }
        if (assignment.Operator is not (TokenKind.Assign or TokenKind.MoveAssign))
        {
            _Unsupported(assignment.Span, "une affectation composée sur un index surchargé : écris « m[i] <- m[i] + v; »");
            return null;
        }
        if (_FindIndexer(mold, "[]=", position) is not { } writer)
        {
            _Error(position.Span, DiagnosticCodes.TypeMismatch, $"« {mold} » ne s'écrit pas par un index « {position.Type} »", $"de type « {position.Type} »",
                $"index acceptés : {string.Join(", ", mold.Symbol.Overloads.Where(o => o.Operator == "[]=").Select(o => o.Gear.Parameters[1].Type))}");
            _BindExpression(assignment.Value);
            return null;
        }

        var gear = writer.Gear;
        var value = _BindExpected(assignment.Value, gear.Parameters[2].Type);
        if (value is null || !_CheckMutArguments(gear, [receiver, position, value], mutableElement: false)) return null;

        var argument = _IndexerArgument(position, gear.Parameters[1].Type, $"l'index de « {mold} » est un « {gear.Parameters[1].Type} »");
        var converted = _ConvertTo(value, gear.Parameters[2].Type, $"« {index.Span.Text} » est un « {gear.Parameters[2].Type} »");
        if (argument is null || converted is null) return null;

        if (assignment.Operator == TokenKind.MoveAssign) converted = _Moved(assignment.Value, converted, null);
        return new IrExpressionStatement(assignment.Span, _OverloadCall(assignment.Span, gear, [receiver, argument, converted]));
    }

    /// <summary>La conversion <paramref name="kind"/> (<c>implicit</c> ou <c>explicit</c>) de <paramref name="from"/> vers exactement <paramref name="to"/>, si son mold en déclare une.</summary>
    private static GearSymbol? _FindConversion(IrType from, IrType to, string kind) =>
        from is MoldType { Symbol: var mold } ? mold.Overloads.FirstOrDefault(o => o.Operator == kind && o.Gear.ReturnType == to)?.Gear : null;

    /// <summary>
    /// L'appel d'un opérateur ou d'une conversion. Un opérateur qui n'est pas <c>pure</c> n'est pas appelé depuis un
    /// gear pure (E057) ; l'appel est construit quand même, l'erreur suffit.
    /// </summary>
    private IrCall _OverloadCall(SourceSpan span, GearSymbol gear, IrExpression[] arguments)
    {
        if (!_probing)
        {
            if (!gear.IsPure) _AllowEffect(span, DiagnosticCodes.ImpureCall, $"appeler « {gear.Name} », qui n'est pas pure");
            if (_currentGear is { } current) _calls[current].Add(gear);
        }
        return new IrCall(span, gear, arguments);
    }

    #endregion
}
