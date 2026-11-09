using System.Globalization;
using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Les expressions : la traduction de chaque nœud, l'évaluation de gauche à droite quand un
/// opérande a un effet, et les littéraux (entiers, flottants, chaînes, templates, symboles).
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// Une expression C++. Chaque opération est entièrement parenthésée : la précédence du C++ ne
    /// peut ainsi jamais changer le sens fixé par le parseur RGL. RGL évalue toujours de gauche à
    /// droite ; le C++ ne le garantit pas pour les opérandes de <c>+</c> ou les arguments d'un
    /// appel : quand l'un d'eux modifie une variable (<c>increment</c>…), ils sont évalués un à un
    /// dans une lambda appelée sur place (<see cref="_Sequenced"/>).
    /// </summary>
    private string _Expression(IrExpression expression) =>
        _NeedsSequencing(expression) ? _Sequenced(expression) : _Render(expression, _Expression);

    /// <summary>
    /// Le C++ d'une expression, ses sous-expressions traduites par <paramref name="operand"/>.
    /// </summary>
    private string _Render(IrExpression expression, Func<IrExpression, string> operand) => expression switch
    {
        IrRawMemory memory => _RawMemory(memory, operand),
        IrIntegerConstant constant => _Integer(constant.Value, constant.Type),
        // Une marque d'ingot ou des bornes n'existent qu'en RGL : la valeur C++ est la même.
        IrRetype retype => operand(retype.Value),
        IrNumericCast cast => _NumericCast(cast, operand(cast.Value)),
        IrDurationMilliseconds milliseconds => $"static_cast<std::int64_t>({operand(milliseconds.Value)}.milliseconds())",
        IrNow now => $"{_Type(now.Type)}::now()",
        IrDateTimeOf date => $"{_Type(date.Type)}({operand(date.Value)})",
        IrDurationOf duration => $"{_Type(duration.Type)}({operand(duration.Value)})",
        IrBoundCheck check => _BoundCheck(check, operand(check.Value)),
        IrTryCast cast => _TryCast(cast, operand(cast.Value)),
        // Une constante adaptée au type attendu s'écrit directement dans ce type : 40u plutôt que static_cast<std::uint32_t>(40).
        IrConversion { Operand: IrIntegerConstant constant, Type: PrimitiveType { IsInteger: true } } conversion => _Integer(constant.Value, conversion.Type),
        IrConversion { Operand: IrIntegerConstant constant, Type: PrimitiveType { Kind: PrimitiveKind.Double } } => _Float(constant.Value),
        IrFloatConstant constant => _Float(constant.Value),
        IrBoolConstant constant => constant.Value ? "true" : "false",
        IrNullConstant => "nullptr",
        IrStringConstant constant => _String(constant),
        IrSymbolConstant constant => _Symbol(constant),
        IrRegexConstant constant => _Regex(constant),
        IrRegexOp operation => _RegexOperation(operation, operand),
        IrDurationConstant constant => $"{_Type(constant.Type)}({constant.Milliseconds.ToString(CultureInfo.InvariantCulture)}LL)",
        IrParameterReference reference => _Name(reference.Parameter),
        IrVariableReference reference => _variableNames[reference.Variable],
        IrMove move => $"std::move({_variableNames[move.Source.Variable]})",
        IrTemplate template => _Template(template, operand),
        IrSwitchValue value => $"rgl_sw{value.SwitchId}",
        IrPipelineValue => _pipelineValue ?? throw new InvalidOperationException("Valeur de pipeline sans étape qui la produise."),
        IrStepInput => _stepInput ?? throw new InvalidOperationException("Valeur d'entrée d'étape hors d'une étape qui la range."),
        IrAccumulatorValue => _accumulatorValue ?? throw new InvalidOperationException("Accumulateur hors d'une accumulation."),
        IrTargetValue => _targetValue ?? throw new InvalidOperationException("Valeur d'avant hors d'une affectation composée en place."),
        IrRange range => _RangeValue(range),
        IrConversion { Type: NullableType, Operand: IrNullConstant } conversion => $"{_Type(conversion.Type)}()",
        IrConversion { Type: NullableType } conversion => $"{_Type(conversion.Type)}({operand(conversion.Operand)})",
        IrNonNull nonNull => $"(*{operand(nonNull.Value)})",
        IrCoalesce { Left.Type: ResultType } coalesce => _WithTemporary(coalesce.Type, coalesce.Left, value =>
            $"{value}.is_ok() ? {value}.value() : {_Expression(coalesce.Right)}"),
        IrCoalesce coalesce => _WithTemporary(coalesce.Type, coalesce.Left, value =>
            $"{value}.has_value() ? {(coalesce.Type is NullableType ? value : "*" + value)} : {_Expression(coalesce.Right)}"),
        IrEmptyCoalesce coalesce => _EmptyCoalesce(coalesce),
        IrSafeAccess access => _WithTemporary(access.Type, access.Target, value =>
            $"{value}.has_value() ? {_Type(access.Type)}({_Field($"(*{value})", ((NullableType)access.Target.Type).Inner, access.Field)}) : {_Type(access.Type)}()"),
        IrTryParseNumber parse => _TryParseNumber(parse, operand(parse.Text)),
        IrToUnion union => $"{_Type(union.Union)}(rgl::standard::member<{union.Index}>, {operand(union.Value)})",
        IrUnionMemberValue => _unionMember ?? throw new InvalidOperationException("Membre d'union hors d'une conversion d'union."),
        IrUnionConvert convert => _UnionConvert(convert),
        IrWith with => _With(with),
        IrUnionWiden widen => $"rgl::standard::widen<{_Type(widen.Union)}>({operand(widen.Value)})",
        IrResultValue result => $"{_Type(result.Result)}::{(result.IsOk ? "ok" : "err")}({operand(result.Value)})",
        IrMatch match => _MatchValue(match),
        IrRead => _Read(),
        IrReadFile read => _ReadFile(read, operand),
        IrReadJson read => _ReadJson(read, operand),
        IrReadXml read => _ReadXml(read, operand),
        IrParseJson parse => _ParseJson(parse, operand),
        IrToJson write => _ToJson(write, operand),
        IrOpenFile open => _OpenFile(open, operand),
        IrSpawn spawn => _Spawn(spawn, operand),
        IrNativeCall native => _NativeCall(native, operand),
        // await suspend la coroutine ; sleep est une promesse que la boucle d'événements termine à l'échéance.
        IrAwait await => $"(co_await {operand(await.Operand)})",
        IrCancel cancel => $"rgl::standard::cancel({operand(cancel.Operand)})",
        IrPromiseState state => $"static_cast<{_Type(state.Enum)}>(({operand(state.Operand)}).state())",
        IrAsyncFile file => _AsyncFile(file, operand),
        IrAsyncSpawn spawn => _AsyncSpawn(spawn, operand),
        IrWhenAny any => _WhenAny(any, operand),
        IrTaskCall task => _TaskCall(task, operand),
        IrBlockOn block => _BlockOn(block, operand),
        IrSleep sleep => _Sleep(sleep, operand),
        IrTapValue => "rgl_tap_value",
        IrFileOp operation => _FileOperation(operation, operand),
        IrStringToBytes bytes => _StringToBytes(bytes, operand),
        IrBytesToString text => _BytesToString(text, operand),
        IrParseNumber parse => _ParseNumber(parse, operand(parse.Text)),
        IrMoldValue mold => _MoldValue(mold),
        IrEnumValue value => $"{_Type(value.Enum)}::rgl_c_{value.Enum.Symbol.Variants[value.Index].Name}",
        IrPart part => _partNames[part.Value],
        IrMemberAccess access => _MemberAccess(access, operand),
        IrUnionField access => _UnionField(access),
        IrArrayValue array => _ArrayValue(array, operand),
        IrDictionaryValue dictionary => _DictionaryValue(dictionary, operand),
        IrTupleValue tuple => _TupleValue(tuple, operand),
        IrIndex index => _Index(index, operand),
        IrTupleItem item => $"rgl::standard::item<{item.Position}>({operand(item.Target)})",
        IrLength { Target.Type: PrimitiveType { Kind: PrimitiveKind.String } } length => $"static_cast<std::uint32_t>(({operand(length.Target)}).length())",
        IrLength length => $"static_cast<std::uint32_t>(({operand(length.Target)}).size())",
        IrRowCount rows => _RowCount(rows, operand),
        IrPoolValue pool => _PoolValue(pool, operand),
        IrContains contains => $"({operand(contains.Target)}).contains({operand(contains.Key)})",
        IrDatasetFind find => _DatasetFind(find, operand),
        IrDictionaryParts parts => $"({operand(parts.Target)}).{(parts.Values ? "values" : "keys")}<{_Type(parts.Array)}>()",
        IrDictionaryPut put => $"({operand(put.Target)}).put({operand(put.Key)}, {operand(put.Value)})",
        IrDictionaryWithout without => $"({operand(without.Target)}).without({operand(without.Key)})",
        IrPush push => _Push(push, operand),
        IrAdd add => _Add(add, operand),
        IrArrayPack pack => _ArrayPack(pack, operand),
        IrArrayConvert convert => _ArrayConvert(convert, operand),
        IrLambda lambda => _Lambda(lambda),
        IrGearReference reference => $"{_Type(reference.Function)}(&rgl_g_{reference.Gear.CppName})",
        // Une lambda écrite là où elle est appelée (l'étape d'un parcours) : une lambda C++ appelée sur
        // place, que Clang inline, sans passer par rgl::standard::function.
        // L'accumulateur d'une accumulation est consommé par l'étape : la lambda le reçoit par valeur, déplacé, et le renvoie par déplacement
        // (un « return état; » de paramètre) au lieu de recopier le tableau ou le texte qu'il contient à chaque élément.
        IrInvoke { Target: IrLambda lambda, Arguments: [IrAccumulatorValue, ..] } invoke when _ConsumesAccumulator(lambda) && _NoteTrue(lambda.Span, "l'accumulateur est reçu par valeur et déplacé : il n'est plus recopié à chaque élément") =>
            $"{_LambdaCode(lambda, _atFileLevel ? "[]" : "[&]", consumedFirst: true)}(std::move({operand(invoke.Arguments[0])}){string.Concat(invoke.Arguments.Skip(1).Select(argument => ", " + operand(argument)))})",
        IrInvoke { Target: IrLambda lambda } invoke => $"{_LambdaCode(lambda, _atFileLevel ? "[]" : "[&]")}({string.Join(", ", invoke.Arguments.Select(operand))})",
        IrInvoke invoke => $"({operand(invoke.Target)})({string.Join(", ", invoke.Arguments.Select(operand))})",
        IrElementValue => _elementValue ?? throw new InvalidOperationException("Élément de parcours hors d'un parcours."),
        IrIndexValue => $"static_cast<std::int32_t>({_indexValue ?? throw new InvalidOperationException("Rang de parcours hors d'un parcours.")})",
        IrEach each => _EachExpression(each),
        // x eq null : un test, qui ne demande pas l'égalité des valeurs (une lambda n'en a pas).
        IrBinary { Operator: IrBinaryOperator.Equal } equal when _IsNullValue(equal.Right) && !_IsNullValue(equal.Left) => $"(!{operand(equal.Left)}.has_value())",
        IrBinary { Operator: IrBinaryOperator.Equal } equal when _IsNullValue(equal.Left) && !_IsNullValue(equal.Right) => $"(!{operand(equal.Right)}.has_value())",
        IrCall { AsResult: true } call => _TrainResult(call, operand),
        IrCall { Gear.Kind: GearKind.Guard } call when _InGuard => _PropagatingCall(call, operand),
        IrCall call => _CallText(call, operand),
        IrIncrement increment => _Increment(increment),
        IrConditional conditional => $"({operand(conditional.Condition)} ? {operand(conditional.WhenTrue)} : {operand(conditional.WhenFalse)})",
        IrConversion conversion => $"static_cast<{_Type(conversion.Type)}>({operand(conversion.Operand)})",
        IrUnary { Operator: IrUnaryOperator.Negate } unary when _MayOverflow(unary) => _Arithmetic("negate", unary, operand(unary.Operand)),
        IrUnary { Operator: IrUnaryOperator.BitwiseNot } unary => $"static_cast<{_Type(unary.Type)}>(~{operand(unary.Operand)})",
        IrBinary { Operator: IrBinaryOperator.Add } binary when _MayOverflow(binary) => _Arithmetic("add", binary, operand(binary.Left), operand(binary.Right)),
        IrBinary { Operator: IrBinaryOperator.Subtract } binary when _MayOverflow(binary) => _Arithmetic("sub", binary, operand(binary.Left), operand(binary.Right)),
        IrBinary { Operator: IrBinaryOperator.Multiply } binary when _MayOverflow(binary) => _Arithmetic("mul", binary, operand(binary.Left), operand(binary.Right)),
        // Un diviseur connu, ni nul ni -1, ne peut ni arrêter ni déborder : l'opération C++ seule.
        IrBinary { Operator: IrBinaryOperator.Divide or IrBinaryOperator.Remainder } binary when _MayOverflow(binary) && _IsSafeDivisor(binary.Right) => _Bits(binary.Type) < 32
            ? $"static_cast<{_Type(binary.Type)}>({operand(binary.Left)} {_Binary(binary.Operator)} {operand(binary.Right)})"
            : $"({operand(binary.Left)} {_Binary(binary.Operator)} {operand(binary.Right)})",
        IrBinary { Operator: IrBinaryOperator.Divide or IrBinaryOperator.Remainder } binary when _MayOverflow(binary) && _InGuard && ConstantFolder.Fold(binary.Right) is null =>
            _GuardedDivision(binary, operand(binary.Left), operand(binary.Right)),
        IrBinary { Operator: IrBinaryOperator.Divide } binary when _MayOverflow(binary) => _Arithmetic("div", binary, operand(binary.Left), operand(binary.Right)),
        IrBinary { Operator: IrBinaryOperator.Remainder } binary when _MayOverflow(binary) => _Arithmetic("rem", binary, operand(binary.Left), operand(binary.Right)),
        IrBinary { Operator: IrBinaryOperator.ShiftLeft or IrBinaryOperator.ShiftRight } binary when _MayOverflow(binary) && _IsValidShift(binary) => _Bits(binary.Type) < 32
            ? $"static_cast<{_Type(binary.Type)}>({operand(binary.Left)} {_Binary(binary.Operator)} {operand(binary.Right)})"
            : $"({operand(binary.Left)} {_Binary(binary.Operator)} {operand(binary.Right)})",
        IrBinary { Operator: IrBinaryOperator.ShiftLeft } binary when _MayOverflow(binary) => _Arithmetic("shl", binary, operand(binary.Left), operand(binary.Right)),
        IrBinary { Operator: IrBinaryOperator.ShiftRight } binary when _MayOverflow(binary) => _Arithmetic("shr", binary, operand(binary.Left), operand(binary.Right)),
        IrUnary unary => $"({_Unary(unary.Operator)}{operand(unary.Operand)})",
        IrBinary binary => $"({operand(binary.Left)} {_Binary(binary.Operator)} {operand(binary.Right)})",
        _ => throw new InvalidOperationException($"Expression sans traduction : {expression.GetType().Name}. Ajouter son cas dans CppEmitter."),
    };

    /// <summary>
    /// Vrai si les sous-expressions doivent être évaluées une à une : l'une modifie une variable,
    /// et une autre peut la lire (elle n'est pas connue à la compilation), sous une opération dont
    /// le C++ n'ordonne pas les opérandes. <c>and</c>, <c>or</c> et le ternaire évaluent déjà de
    /// gauche à droite, et sans calculer la partie inutile.
    /// </summary>
    private static bool _NeedsSequencing(IrExpression expression) =>
        expression is IrBinary { Operator: not (IrBinaryOperator.And or IrBinaryOperator.Or) } or IrRawMemory { Arguments.Count: > 1 } or IrTemplate or IrCall or IrInvoke or IrArrayValue { HasKnownCount: false }
        && IrEffects.Children(expression).Any(IrEffects.HasEffects)
        && IrEffects.Children(expression).Count(child => ConstantFolder.Fold(child) is null) > 1;

    /// <summary>
    /// L'expression dans une lambda appelée sur place, qui range chaque sous-expression dans une
    /// constante, dans l'ordre : <c>[&amp;]() -&gt; T { const auto rgl_t1 = …; const auto rgl_t2 = …; return rgl_t1 + rgl_t2; }()</c>.
    /// Clang l'intègre entièrement ; seul l'ordre d'évaluation change. Dans un guard, c'est une
    /// expression-instruction <c>({ … })</c> : un échec d'une sous-expression doit quitter le guard,
    /// pas seulement la lambda.
    /// </summary>
    private string _Sequenced(IrExpression expression)
    {
        if (_UseStatementExpressions)
        {
            _usesStatementExpressions = true;
            var block = new StringBuilder("({ ");
            var named = new Dictionary<IrExpression, string>(ReferenceEqualityComparer.Instance);
            var children = IrEffects.Children(expression).ToArray();
            for (var i = 0; i < children.Length; ++i)
            {
                var name = $"rgl_t{++_temporaryCount}";
                block.Append(_IsMutableArgument(expression, i) ? "auto&" : _Store(children[i], children[(i + 1)..])).Append(' ').Append(name).Append(" = ").Append(_Expression(children[i])).Append("; ");
                named[children[i]] = name;
            }
            return block.Append(_Render(expression, child => named[child])).Append("; })").ToString();
        }

        var body = new StringBuilder($"[&]() -> {_Type(expression.Type)} {{ ");
        var temporaries = new Dictionary<IrExpression, string>(ReferenceEqualityComparer.Instance);
        var all = IrEffects.Children(expression).ToArray();
        for (var i = 0; i < all.Length; ++i)
        {
            var name = $"rgl_t{++_temporaryCount}";
            body.Append(_IsMutableArgument(expression, i) ? "auto&" : _Store(all[i], all[(i + 1)..])).Append(' ').Append(name).Append(" = ").Append(_Expression(all[i])).Append("; ");
            temporaries[all[i]] = name;
        }

        return body.Append("return ").Append(_Render(expression, child => temporaries[child])).Append("; }()").ToString();
    }

    /// <summary>
    /// Comment ranger une valeur évaluée d'abord : une référence (<c>const auto&amp;</c>), sans copie,
    /// sauf si elle lit une variable qu'une valeur évaluée après elle modifie (<c>increment</c>, <c>push</c>,
    /// argument <c>mut</c>) : il faut alors garder la valeur d'avant (<c>const auto</c>).
    /// </summary>
    private static string _Store(IrExpression value, IEnumerable<IrExpression> later)
    {
        var modified = later.SelectMany(IrMutation.Modified).ToHashSet();
        var reads = _Descendants(value).Select(IrMutation.Symbol).Any(s => s is not null && modified.Contains(s));
        return reads ? "const auto" : "const auto&";
    }

    /// <summary>Vrai si la sous-expression <paramref name="index"/> de <paramref name="expression"/> est passée à un paramètre <c>mut</c> : une référence modifiable.</summary>
    private static bool _IsMutableArgument(IrExpression expression, int index) =>
        expression is IrCall call && call.Gear.Parameters[index].IsMutable;

    /// <summary>Une expression et toutes ses sous-expressions.</summary>
    private static IEnumerable<IrExpression> _Descendants(IrExpression expression) =>
        IrEffects.Children(expression).SelectMany(_Descendants).Prepend(expression);

    /// <summary>Vrai pour <c>null</c> écrit dans le source, mis dans un nullable.</summary>
    private static bool _IsNullValue(IrExpression expression) => expression is IrNullConstant or IrConversion { Operand: IrNullConstant };

    /// <summary>
    /// Une lambda : <c>rgl::standard::function&lt;R(A…)&gt;([=](A… p) -&gt; R { … })</c>. <c>[=]</c> copie à sa
    /// création les variables qu'elle lit ; au niveau du fichier, il n'y a rien à capturer (<c>[]</c>).
    /// Son corps s'écrit comme celui d'un gear, hors de tout guard ; s'il renvoie une valeur, il ne peut
    /// pas atteindre sa fin (le binder l'a vérifié).
    /// </summary>
    private string _Lambda(IrLambda lambda) => $"{_Type(lambda.Function)}({_LambdaCode(lambda, _atFileLevel ? "[]" : "[=]")})";

    /// <summary>La lambda C++ elle-même, <c>capture(paramètres) -&gt; R { corps }</c>, sans <c>rgl::standard::function</c> autour.</summary>
    private string _LambdaCode(IrLambda lambda, string capture, bool consumedFirst = false)
    {
        var (gear, atFileLevel, indent, element) = (_gear, _atFileLevel, _lambdaIndent, _elementValue);
        (_gear, _atFileLevel, _elementValue) = (null, false, null);
        _lambdaIndent += Indent;

        var body = new StringBuilder();
        foreach (var statement in LoopFusion.Fuse(lambda.Body.Statements, _AddNote)) _Statement(body, statement, _lambdaIndent);
        if (lambda.Function.Return != PrimitiveType.Void && lambda.Body.Statements is not [.., IrReturn])
            body.Append(_lambdaIndent).Append("__builtin_unreachable();\n");

        (_gear, _atFileLevel, _lambdaIndent, _elementValue) = (gear, atFileLevel, indent, element);
        var parameters = string.Join(", ", lambda.Parameters.Select((p, index) => $"[[maybe_unused]] {(consumedFirst && index == 0 ? _Type(p.Type) : _ParameterType(p))} {_Name(p)}"));
        var result = lambda.Function.Return == PrimitiveType.Void ? "void" : _Type(lambda.Function.Return);
        // Les captures restent des copies côté RGL. `mutable` permet toutefois aux parcours
        // explicitement `mut` de modifier le contenu de la copie possédée par la closure.
        return $"{capture}({parameters}) mutable -> {result} {{\n{body}{indent}{Indent}}}";
    }

    /// <summary>
    /// Vrai si l'étape d'une accumulation peut recevoir l'accumulateur par valeur, déplacé : il possède de la mémoire (un tableau, un texte, un tuple
    /// qui en contient) et la lambda ne le modifie pas en place. L'accumulateur est réaffecté du résultat juste après : rien ne le relit entre-temps.
    /// </summary>
    private bool _NoteTrue(Rgl.Text.SourceSpan span, string message)
    {
        _Note(span, message);
        return true;
    }

    private bool _ConsumesAccumulator(IrLambda lambda) =>
        _accumulatorValue is not null && LastUses.ConsumesAccumulator(lambda);

    /// <summary>L'indentation du corps de la lambda en cours d'écriture : un niveau de plus par lambda imbriquée.</summary>
    private string _lambdaIndent = Indent;

    /// <summary><c>&lt;- console</c> : une ligne lue sur la console.</summary>
    private string _Read()
    {
        _Use(RuntimeComponent.Console, "rgl/standard/console.hpp");
        return "rgl::standard::console::read_line()";
    }

    /// <summary>
    /// Un entier écrit avec le suffixe de son type (<c>40u</c>, <c>4000000000ll</c>) ; les types plus
    /// petits qu'un <c>int</c>, qui n'ont pas de suffixe, passent par un <c>static_cast</c>.
    /// </summary>
    private string _Integer(long value, IrType type)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        // Le plus petit entier signé n'a pas de littéral : « -2147483648 » est un moins appliqué à un long.
        if (value == int.MinValue && type is PrimitiveType { Kind: PrimitiveKind.Int }) return "(-2147483647 - 1)";
        if (value == long.MinValue && type is PrimitiveType { Kind: PrimitiveKind.Long }) return "(-9223372036854775807ll - 1)";
        return type switch
        {
            PrimitiveType { Kind: PrimitiveKind.Int } => text,
            PrimitiveType { Kind: PrimitiveKind.UInt } => text + "u",
            PrimitiveType { Kind: PrimitiveKind.Long } => text + "ll",
            PrimitiveType { Kind: PrimitiveKind.ULong } => text + "ull",
            _ => $"static_cast<{_Type(type)}>({text})",
        };
    }

    /// <summary>Un double C++ qui relit exactement la même valeur, toujours avec un point ou un exposant.</summary>
    private static string _Float(double value)
    {
        if (double.IsPositiveInfinity(value)) return "__builtin_huge_val()";

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>
    /// Une chaîne, construite avec sa longueur en octets UTF-8 (elle peut contenir des <c>\0</c>).
    /// Tout octet hors de l'ASCII imprimable est écrit en octal (<c>\351</c>) : contrairement à
    /// <c>\x</c>, un échappement octal ne dépasse jamais trois chiffres, et ne peut donc pas avaler
    /// le caractère suivant.
    /// </summary>
    private string _String(IrStringConstant constant)
    {
        var bytes = Encoding.UTF8.GetBytes(constant.Value);
        var literal = new StringBuilder("\"");
        foreach (var b in bytes)
        {
            if (b == '"' || b == '\\') literal.Append('\\').Append((char)b);
            else if (b is >= 0x20 and < 0x7F and not (byte)'?') literal.Append((char)b);  // « ? » : évite les trigraphes
            else literal.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        }
        literal.Append('"');

        var value = $"{_Type(constant.Type)}({literal}, {bytes.Length})";
        if (constant.Type != PrimitiveType.String || bytes.Length <= SmallStringCapacity) return value;

        // Une chaîne longue construite à l'exécution serait copiée sur le tas à chaque fois ; en
        // constante, elle n'est qu'une vue sur le texte du littéral.
        if (!_longLiterals.TryGetValue(value, out var name))
        {
            name = $"rgl_l_{_longLiterals.Count + 1}";
            _longLiterals[value] = name;
        }
        return name;
    }

    /// <summary>Taille au-delà de laquelle une chaîne n'est plus stockée dans l'objet (<c>string::small_capacity</c>).</summary>
    private const int SmallStringCapacity = 22;

    /// <summary>Un template : <c>rgl::standard::interpolate(…)</c> écrit ses morceaux à la suite dans une chaîne.</summary>
    private string _Template(IrTemplate template, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
        _includes.Add("rgl/standard/format.hpp");
        foreach (var part in template.Parts) _MarkWritten(part.Type);
        return $"rgl::standard::interpolate({string.Join(", ", template.Parts.Select(operand))})";
    }

    /// <summary>Un symbole littéral : le symbole de son entrée statique, écrite en tête du fichier.</summary>
    private string _Symbol(IrSymbolConstant constant)
    {
        _symbols.Add(constant.Name);
        return $"{_Type(constant.Type)}(rgl_s_{constant.Name})";
    }

    private static string _Unary(IrUnaryOperator op) => op switch
    {
        IrUnaryOperator.Negate => "-",
        IrUnaryOperator.Not => "!",
        IrUnaryOperator.BitwiseNot => "~",
        _ => throw new InvalidOperationException($"Opérateur sans traduction : {op}."),
    };

    private static string _Binary(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add or IrBinaryOperator.Concatenate => "+",
        IrBinaryOperator.Subtract => "-",
        IrBinaryOperator.Multiply => "*",
        IrBinaryOperator.Divide => "/",
        IrBinaryOperator.Remainder => "%",
        IrBinaryOperator.Less => "<",
        IrBinaryOperator.Greater => ">",
        IrBinaryOperator.LessOrEqual => "<=",
        IrBinaryOperator.GreaterOrEqual => ">=",
        IrBinaryOperator.Equal => "==",
        IrBinaryOperator.And => "&&",
        IrBinaryOperator.Or => "||",
        IrBinaryOperator.Xor => "!=",  // ou exclusif logique sur deux bool
        IrBinaryOperator.BitAnd => "&",
        IrBinaryOperator.BitOr => "|",
        IrBinaryOperator.BitXor => "^",
        IrBinaryOperator.ShiftLeft => "<<",
        IrBinaryOperator.ShiftRight => ">>",
        _ => throw new InvalidOperationException($"Opérateur sans traduction : {op}."),
    };
}
