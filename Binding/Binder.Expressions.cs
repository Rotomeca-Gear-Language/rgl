using System.Numerics;
using Rgl.AST.Collections;
using Rgl.AST.Expressions;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;
using Rotomeca.Utils.Types;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    /// <summary>
    /// Code de sortie d'un <c>$ valeur</c> : un <c>uint</c>, éventuellement par une conversion
    /// implicite (une constante positive ou nulle, un <c>ubyte</c>…).
    /// </summary>
    private IrExpression? _BindExitCode(IExpression expression)
    {
        var code = _BindExpression(expression);
        if (code is null) return null;
        // Un ingot d'entier non signé (programValue de la std) donne son code sans passer par °.
        if (code.Type is IngotType { Underlying: PrimitiveType { Kind: PrimitiveKind.UInt } } && _Coerce(_Unbounded(_Unbranded(code)), PrimitiveType.UInt) is { } unbranded) return unbranded;
        if (_Coerce(code, PrimitiveType.UInt) is { } converted) return converted;

        _Error(code.Span, DiagnosticCodes.TypeMismatch, $"le code de sortie doit être un uint, pas un « {code.Type} »", $"de type « {code.Type} »",
            _WhyNotConverted(code.Type, PrimitiveType.UInt), "le code de sortie est un entier positif, par exemple : $ 2");
        return null;
    }

    /// <summary>
    /// Type et sens d'une expression. Renvoie <see langword="null"/> après avoir signalé une erreur ;
    /// l'expression qui la contient ne signale alors rien de plus, pour éviter les erreurs en cascade.
    /// </summary>
    private IrExpression? _BindExpression(IExpression expression)
    {
        // Le type attendu ne vaut que pour cette expression, pas pour ses sous-expressions.
        var expected = _expected;
        _expected = null;
        var spawnAllowed = _spawnAllowed;
        _spawnAllowed = false;
        if (_pathOverrides.Count > 0 && _pathOverrides.TryGetValue(expression, out var overridden)) return overridden;
        return _AutoBlock(expression switch
        {
        LiteralExpression literal when _dataLiterals.Contains(literal) => _BindDataLiteral(literal, expected),
        LiteralExpression literal => _BindLiteral(literal),
        NameExpression name => _BindName(name, expected),
        ParenthesizedExpression parenthesized => _BindExpected(parenthesized.Inner, expected),
        UnaryExpression unary => _BindUnary(unary),
        BinaryExpression binary => _BindBinary(binary),
        CallExpression call => _BindCall(call),
        TypeTestExpression typeTest => _BindTypeTest(typeTest),
        TemplateExpression template => _BindTemplate(template),
        IncrementExpression increment => _BindIncrement(increment),
        ConditionalExpression conditional => _BindConditional(conditional, expected),
        DollarExpression dollar => _BindDollar(dollar),
        PipeExpression pipe => _BindPipe(pipe),
        LambdaPipeExpression pipe => _BindLambdaPipe(pipe),
        ReadExpression read => _BindRead(read, expected),
        SpawnExpression spawn => _BindSpawn(spawn, spawnAllowed),
        AwaitExpression await => _BindAwait(await),
        TaskCallExpression task => _BindTaskCall(task),
        CppExpression cpp => _BindCpp(cpp),
        NativeCallExpression native => _BindNativeCall(native),
        CastExpression cast => _BindCast(cast),
        MoldExpression mold => _BindMoldExpression(mold, expected),
        MemberAccessExpression access => _BindMemberAccess(access),
        MatchExpression match => _BindMatch(match, expected),
        UnbrandExpression unbrand => _BindUnbrand(unbrand),
        ArrayExpression array => _BindArrayLiteral(array, expected),
        DictionaryExpression dictionary => _BindDictionaryLiteral(dictionary, expected),
        TupleExpression tuple => _BindTupleLiteral(tuple, expected),
        IndexExpression index => _BindIndex(index),
        SpreadElement spread => _SpreadOutside(spread),
        LambdaExpression lambda => _BindLambda(lambda, expected),
        EachExpression each => _BindEach(each),
        SoaEachExpression rows => _BindSoaEach(rows),
        FoldExpression fold => _BindFold(fold),
        WithExpression with => _BindWith(with),
        RangeExpression range => _BindRange(range, expected),
        _ => throw new InvalidOperationException($"Expression inattendue dans un arbre sans erreur de syntaxe : {expression.GetType().Name}."),
        });
    }

    /// <summary>
    /// Le type que le contexte attend de l'expression liée ensuite (une déclaration typée, un
    /// <c>return</c>, une affectation) : les branches d'un match ou d'un ternaire y vont directement.
    /// </summary>
    private IrType? _expected;

    /// <summary><c>...a</c> hors d'un tableau écrit (E040).</summary>
    private IrExpression? _SpreadOutside(SpreadElement spread)
    {
        _Error(spread.Span, DiagnosticCodes.TypeMismatch, "« ... » étale un tableau dans un autre tableau écrit", "hors d'un tableau",
            "par exemple : [...autres, 4]");
        return null;
    }

    /// <summary><paramref name="expression"/>, liée en sachant qu'elle doit donner un <paramref name="expected"/>.</summary>
    private IrExpression? _BindExpected(IExpression expression, IrType? expected)
    {
        _expected = expected == PrimitiveType.Void ? null : expected;
        return _BindExpression(expression);
    }

    #region Littéraux et noms

    private IrExpression? _BindLiteral(LiteralExpression literal)
    {
        var span = literal.Span;
        switch (literal.Kind)
        {
            case TokenKind.IntegerLiteral:
                // Un littéral entier est un int ; plus grand, c'est une constante long, qui s'adapte au type attendu.
                var value = LiteralDecoder.Integer(literal.Text);
                if (value > long.MaxValue)
                {
                    _Error(span, DiagnosticCodes.TypeMismatch, $"entier trop grand : {literal.Text}", "hors limites",
                        $"un littéral entier vaut au plus {long.MaxValue}");
                    return null;
                }
                return new IrIntegerConstant(span, value > int.MaxValue ? new PrimitiveType(PrimitiveKind.Long) : PrimitiveType.Int, (long)value);

            case TokenKind.FloatLiteral:
                return new IrFloatConstant(span, LiteralDecoder.Float(literal.Text));

            case TokenKind.TrueKeyword or TokenKind.FalseKeyword:
                return new IrBoolConstant(span, literal.Kind == TokenKind.TrueKeyword);

            case TokenKind.NullKeyword:
                return new IrNullConstant(span);

            case TokenKind.RawStringLiteral:
                return new IrStringConstant(span, PrimitiveType.String, LiteralDecoder.Raw(literal.Text));

            case TokenKind.StringLiteral or TokenKind.MutableStringLiteral:
                return _BindQuotedString(literal);

            case TokenKind.SymbolLiteral:
                // Le texte commence par « § », qui ne fait pas partie du nom.
                return new IrSymbolConstant(span, literal.Text[1..]);

            case TokenKind.DurationLiteral:
                return _BindDuration(literal);

            case TokenKind.RegexLiteral:
                return _BindRegex(literal);

            default:
                throw new InvalidOperationException($"Littéral inattendu : {literal.Kind}.");
        }
    }

    /// <summary>Chaîne classique ou <c>m"…"</c>, échappements décodés ; un échappement inconnu donne E042.</summary>
    private IrExpression? _BindQuotedString(LiteralExpression literal)
    {
        var failed = false;
        var text = LiteralDecoder.Quoted(literal.Text, (offset, length) =>
        {
            failed = true;
            var escape = new SourceSpan(literal.Span.Source, literal.Span.Start + RUint.CreateChecked(offset), RUint.CreateChecked(length));
            _Error(escape, DiagnosticCodes.UnknownEscape, $"séquence d'échappement inconnue « {escape.Text} »", "inconnue",
                "échappements possibles : \\n, \\t, \\r, \\0, \\a, \\b, \\f, \\v, \\\\, \\\", \\', \\xHH, \\u{XXXX}",
                "pour un antislash sans échappement, utilise une chaîne brute : 'C:\\chemin'");
        });

        if (failed) return null;
        var type = literal.Kind == TokenKind.MutableStringLiteral ? PrimitiveType.MString : PrimitiveType.String;
        return new IrStringConstant(literal.Span, type, text);
    }

    /// <summary>
    /// Un nom : une variable déclarée plus haut (ou une constante du fichier), sinon un paramètre
    /// du gear courant ou du train (E006 si la variable a été libérée par <c>undefine</c>, E041 si
    /// le nom n'existe pas).
    /// </summary>
    private IrExpression? _BindName(NameExpression name, IrType? expected = null)
    {
        var entry = _scope[name.Name.Name];
        if (entry.HasValue)
        {
            if (entry.Value.Symbol is not { } variable) return null;
            if (_CheckLifecycleCapture(name.Span, name.Name.Name, variable.Type, _lambda is not null && !_lambda.Locals.Contains(variable) && !variable.IsConstant)) return null;
            return _Narrowed(new IrVariableReference(name.Span, variable), variable);
        }

        if (_parameters.TryGetValue(name.Name.Name, out var parameter))
        {
            if (_CheckLifecycleCapture(name.Span, name.Name.Name, parameter.Type, _lambda is not null && !_lambda.Parameters.Contains(parameter))) return null;
            return _Narrowed(new IrParameterReference(name.Span, parameter), parameter);
        }

        // Un paramètre de valeur d'un gear générique : la constante de cette instance.
        if (_generic is not null && _generic.Values.TryGetValue(name.Name.Name, out var value)) return value with { Span = name.Span };

        // Dans un case d'un switch sur un enum, le nom seul d'une variante.
        if (_expectedEnum?[name.Name.Name] is { } variant)
            return new IrEnumValue(name.Span, new EnumType(_expectedEnum), variant.Index);

        // Un gear passé comme valeur : garder(nums, estPair).
        if (_TryFind(_gearOverloads, name.Name.Name, out var overloads))
        {
            if (overloads.Count == 1) return _BindGearReference(name.Name, overloads[0]);
            var candidates = expected is FunctionType function
                ? overloads.Where(g => g.Kind == GearKind.Gear && (!function.IsPure || g.IsPure)
                    && g.Parameters.Select(p => p.Type).SequenceEqual(function.Parameters) && g.ReturnType == function.Return).ToArray()
                : overloads.ToArray();
            if (candidates.Length == 1) return _BindGearReference(name.Name, candidates[0]);
            _Error(name.Span, DiagnosticCodes.AmbiguousGear, $"la référence « {name.Name.Name} » désigne plusieurs surcharges", "signature attendue insuffisante",
                $"candidates : {string.Join(" ; ", overloads.Select(g => g.ToString()))}",
                "donne-lui un type de fonction précis ou enveloppe l'appel dans une lambda");
            return null;
        }
        if (_TryFind(_gears, name.Name.Name, out var gear)) return _BindGearReference(name.Name, gear);

        var help = _parameters.Count == 0
            ? "aucune variable de ce nom n'est déclarée plus haut, et il n'y a pas de paramètre ici"
            : $"aucune variable de ce nom n'est déclarée plus haut ; paramètres : {string.Join(", ", _parameters.Keys)}";
        _ReportMissingVariable(name.Name, DiagnosticCodes.UnknownName, $"nom « {name.Name.Name} » inconnu", help);
        return null;
    }

    /// <summary>
    /// Un template : son texte est décodé (E042 pour un échappement inconnu), et chaque expression
    /// interpolée doit pouvoir s'écrire en texte : chaîne, nombre, bool, symbole ou Exception (E040 sinon).
    /// </summary>
    private IrExpression? _BindTemplate(TemplateExpression template)
    {
        RArray<IrExpression> parts = new();
        var failed = false;

        foreach (var part in template.Parts)
        {
            switch (part)
            {
                case TemplateText text:
                    var decoded = LiteralDecoder.Template(text.Span.Text, (offset, length) =>
                    {
                        failed = true;
                        var escape = new SourceSpan(text.Span.Source, text.Span.Start + RUint.CreateChecked(offset), RUint.CreateChecked(length));
                        _Error(escape, DiagnosticCodes.UnknownEscape, $"séquence d'échappement inconnue « {escape.Text} »", "inconnue",
                            "échappements possibles : \\n, \\t, \\r, \\0, \\\\, \\`, \\{, \\}, \\xHH, \\u{XXXX}…");
                    });
                    parts.Push(new IrStringConstant(text.Span, PrimitiveType.String, decoded));
                    break;

                case TemplateInterpolation interpolation:
                    var value = _BindExpression(interpolation.Expression);
                    if (value is null) failed = true;
                    else if (!_IsWritable(value.Type))
                    {
                        failed = true;
                        _Error(value.Span, DiagnosticCodes.TypeMismatch, $"une valeur « {value.Type} » ne s'écrit pas dans un template", $"de type « {value.Type} »",
                            "un template écrit des chaînes, des nombres, des bool, des symboles et des Exception");
                    }
                    // Un template dans un template : ses morceaux s'écrivent directement dans celui-ci.
                    else if (value is IrTemplate inner) foreach (var nested in inner.Parts) parts.Push(nested);
                    else parts.Push(value);
                    break;
            }
        }

        return failed ? null : new IrTemplate(template.Span, [.. parts]);
    }


    #endregion

    #region Opérations

    private IrExpression? _BindUnary(UnaryExpression unary)
    {
        var operand = _BindExpression(unary.Operand);
        if (operand is null) return null;
        if (unary.Operator is TokenKind.Star or TokenKind.Ampersand or TokenKind.DeleteKeyword) return _BindRawUnary(unary, operand);

        return _BindUnaryOperation(unary, operand);
    }

    /// <summary>Un opérateur préfixe sur un opérande déjà lié.</summary>
    private IrExpression? _BindUnaryOperation(UnaryExpression unary, IrExpression operand)
    {
        if (unary.Operator == TokenKind.Minus && operand.Type is MoldType)
        {
            var overloaded = _TryOverload(unary.Span, "-", [operand], out var handled);
            if (handled) return overloaded;
        }

        // Sur un ingot : l'opération sur sa base, puis la marque remise (-uid reste un UserId).
        if (operand.Type is IngotType ingot)
            return _BindUnaryOperation(unary, _Unbrand(operand)) is { } inner ? _Rebrand(inner, ingot) : null;
        operand = _Unbounded(operand);

        switch (unary.Operator)
        {
            case TokenKind.Minus when operand.Type is PrimitiveType { IsNumeric: true } or PrimitiveType { Kind: PrimitiveKind.Duration }:
                return new IrUnary(unary.Span, operand.Type, IrUnaryOperator.Negate, operand);

            case TokenKind.NotKeyword when operand.Type == PrimitiveType.Bool:
                return new IrUnary(unary.Span, PrimitiveType.Bool, IrUnaryOperator.Not, operand);

            // invert : négation logique sur un bool, inversion des bits sur un entier.
            case TokenKind.InvertKeyword when operand.Type == PrimitiveType.Bool:
                return new IrUnary(unary.Span, PrimitiveType.Bool, IrUnaryOperator.Not, operand);

            case TokenKind.InvertKeyword when operand.Type is PrimitiveType { IsInteger: true }:
                return new IrUnary(unary.Span, operand.Type, IrUnaryOperator.BitwiseNot, operand);

            case TokenKind.Minus or TokenKind.NotKeyword or TokenKind.InvertKeyword:
                var expected = unary.Operator switch
                {
                    TokenKind.Minus => "un nombre",
                    TokenKind.NotKeyword => "un bool",
                    _ => "un bool ou un entier",
                };
                _Error(unary.Span, DiagnosticCodes.TypeMismatch, $"« {_Text(unary.Operator)} » attend {expected}, pas un « {operand.Type} »", $"de type « {operand.Type} »");
                return null;

            default:
                _Unsupported(unary.Span, $"opérateur « {_Text(unary.Operator)} »");
                return null;
        }
    }

    private IrExpression? _BindBinary(BinaryExpression binary)
    {
        if (binary.Operator == TokenKind.QuestionQuestion) return _BindCoalesce(binary);
        if (binary.Operator == TokenKind.QuestionDegree) return _BindEmptyCoalesce(binary);

        var left = _BindExpression(binary.Left);

        // À droite d'un « and », la gauche est vraie ; à droite d'un « or », elle est fausse.
        var facts = _NullFacts(left);
        var right = binary.Operator switch
        {
            TokenKind.AndKeyword => _WithNarrowings(facts.WhenTrue, () => _BindExpression(binary.Right)),
            TokenKind.OrKeyword => _WithNarrowings(facts.WhenFalse, () => _BindExpression(binary.Right)),
            _ => _BindExpression(binary.Right),
        };
        return left is null || right is null ? null : _BindOperation(binary.Span, binary.OperatorSpan, binary.Operator, left, right);
    }

    /// <summary>
    /// Une opération à deux opérandes déjà liés : une expression binaire, ou le calcul d'une
    /// affectation composée (<c>a +&lt;- b</c> calcule <c>a + b</c>).
    /// </summary>
    private IrExpression? _BindOperation(SourceSpan span, SourceSpan operatorSpan, TokenKind op, IrExpression boundLeft, IrExpression boundRight)
    {
        if (boundLeft.Type is PointerType || boundRight.Type is PointerType)
            return _BindRawBinary(span, op, boundLeft, boundRight);

        var binary = (Span: span, Operator: op);

        if (boundLeft.Type is MoldType || boundRight.Type is MoldType)
        {
            var overloaded = _TryBinaryOverload(span, operatorSpan, op, boundLeft, boundRight, out var handled);
            if (handled) return overloaded;
        }

        // Un ingot ne se mélange qu'avec lui-même ou une constante ; le calcul se fait sur sa base.
        if (boundLeft.Type is IngotType || boundRight.Type is IngotType) return _BindIngotOperation(span, operatorSpan, op, boundLeft, boundRight);
        (boundLeft, boundRight) = (_Unbounded(boundLeft), _Unbounded(boundRight));

        // Une valeur qui n'est jamais null, comparée à null : toujours différente.
        if (op == TokenKind.EqKeyword && (boundLeft.Type is NullType) != (boundRight.Type is NullType)
            && (boundLeft.Type is NullType ? boundRight : boundLeft) is { Type: not (NullableType or NullType) } never)
            _diagnostics.Warning(span, DiagnosticCodes.UselessCoalescence, $"un « {never.Type} » n'est jamais null : la comparaison avec null est toujours fausse", "jamais null");

        // Une durée ne se mélange qu'avec une durée ou un entier, selon l'opération.
        if ((boundLeft.Type == PrimitiveType.DateTime || boundRight.Type == PrimitiveType.DateTime) && boundLeft.Type is not (NullType or NullableType) && boundRight.Type is not (NullType or NullableType))
            return _BindDateTimeOperation(span, operatorSpan, op, boundLeft, boundRight);
        if ((boundLeft.Type == PrimitiveType.Duration || boundRight.Type == PrimitiveType.Duration) && boundLeft.Type is not (NullType or NullableType) && boundRight.Type is not (NullType or NullableType))
            return _BindDurationOperation(span, operatorSpan, op, boundLeft, boundRight);

        // Les deux opérandes prennent un même type, sauf pour un décalage, dont le nombre de bits a son propre type.
        var (left, right) = (boundLeft, boundRight);
        var isShift = binary.Operator is TokenKind.ShlKeyword or TokenKind.ShrKeyword;
        if (!isShift && left.Type != right.Type)
        {
            if (_Unify(left, right) is not { } unified)
            {
                _Error(binary.Span, DiagnosticCodes.TypeMismatch,
                    $"« {_Text(binary.Operator)} » ne s'applique pas à « {left.Type} » et « {right.Type} »", $"« {left.Type} » {_Text(binary.Operator)} « {right.Type} »",
                    left.Type is PrimitiveType { IsNumeric: true } && right.Type is PrimitiveType { IsNumeric: true }
                        ? $"aucun type ne contient à la fois « {left.Type} » et « {right.Type} » sans perte : convertis l'un des deux avec as"
                        : "les deux opérandes doivent être du même type");
                return null;
            }
            (left, right) = unified;
        }

        var sameType = left.Type == right.Type;
        var numeric = sameType && left.Type is PrimitiveType { IsNumeric: true };
        var integer = sameType && left.Type is PrimitiveType { IsInteger: true };
        var boolean = sameType && left.Type == PrimitiveType.Bool;
        var text = sameType && left.Type == PrimitiveType.String;

        (IrBinaryOperator Operator, IrType Type)? bound = binary.Operator switch
        {
            TokenKind.Plus when numeric => (IrBinaryOperator.Add, left.Type),
            TokenKind.Plus when text => (IrBinaryOperator.Concatenate, PrimitiveType.String),
            TokenKind.Minus when numeric => (IrBinaryOperator.Subtract, left.Type),
            TokenKind.Star when numeric => (IrBinaryOperator.Multiply, left.Type),
            TokenKind.Slash when numeric => (IrBinaryOperator.Divide, left.Type),
            TokenKind.Percent when integer => (IrBinaryOperator.Remainder, left.Type),
            TokenKind.Less when numeric => (IrBinaryOperator.Less, PrimitiveType.Bool),
            TokenKind.Greater when numeric => (IrBinaryOperator.Greater, PrimitiveType.Bool),
            TokenKind.LessEqual when numeric => (IrBinaryOperator.LessOrEqual, PrimitiveType.Bool),
            TokenKind.GreaterEqual when numeric => (IrBinaryOperator.GreaterOrEqual, PrimitiveType.Bool),
            TokenKind.EqKeyword when sameType && left.Type != PrimitiveType.Regex && !_IsPending(left.Type) && left.Type is PrimitiveType or MoldType or NullableType or NullType or UnionType or ResultType or EnumType or ArrayType or DictionaryType or TupleType
                && ((!FunctionType.IsInside(left.Type) && !LifecycleRules.Has(left.Type)) || _IsNull(left) || _IsNull(right))
                => (IrBinaryOperator.Equal, PrimitiveType.Bool),
            TokenKind.AndKeyword when boolean => (IrBinaryOperator.And, PrimitiveType.Bool),
            TokenKind.OrKeyword when boolean => (IrBinaryOperator.Or, PrimitiveType.Bool),
            TokenKind.XorKeyword when boolean => (IrBinaryOperator.Xor, PrimitiveType.Bool),
            TokenKind.BitAndKeyword when integer => (IrBinaryOperator.BitAnd, left.Type),
            TokenKind.BitOrKeyword when integer => (IrBinaryOperator.BitOr, left.Type),
            TokenKind.BitXorKeyword when integer => (IrBinaryOperator.BitXor, left.Type),
            TokenKind.ShlKeyword when left.Type is PrimitiveType { IsInteger: true } && right.Type is PrimitiveType { IsInteger: true } => (IrBinaryOperator.ShiftLeft, left.Type),
            TokenKind.ShrKeyword when left.Type is PrimitiveType { IsInteger: true } && right.Type is PrimitiveType { IsInteger: true } => (IrBinaryOperator.ShiftRight, left.Type),
            _ => null,
        };

        if (bound is { } result)
        {
            // Un diviseur entier nul connu à la compilation arrêterait le programme à coup sûr.
            if (result.Operator is IrBinaryOperator.Divide or IrBinaryOperator.Remainder && integer && ConstantFolder.Fold(right) is 0L)
            {
                _Error(right.Span, DiagnosticCodes.DivisionByZero, "division entière par zéro", "vaut 0",
                    "le programme s'arrêterait ici : une division entière par zéro n'a pas de résultat");
                return null;
            }

            return new IrBinary(binary.Span, result.Type, result.Operator, left, right, operatorSpan);
        }

        var nullableOperand = boundLeft.Type is NullableType ? boundLeft : boundRight.Type is NullableType ? boundRight : null;
        _Error(binary.Span, DiagnosticCodes.TypeMismatch,
            $"« {_Text(binary.Operator)} » ne s'applique pas à « {left.Type} » et « {right.Type} »", $"« {left.Type} » {_Text(binary.Operator)} « {right.Type} »",
            nullableOperand is not null
                ? $"« {nullableOperand.Span.Text} » peut être null : vérifie-le avant (if ({nullableOperand.Span.Text} not eq null) {{ … }}), ou donne une valeur de secours ({nullableOperand.Span.Text} ?? …)"
                : sameType
                ? $"« {_Text(binary.Operator)} » n'est pas défini pour le type « {left.Type} »"
                : "les deux opérandes doivent être du même type");
        return null;
    }

    /// <summary>
    /// <c>increment v</c> et ses variantes : v est une variable numérique (E002 ou E006 si elle
    /// n'existe pas, E005 si c'est une constante ou un paramètre, E040 si elle n'est pas
    /// numérique), et le pas un entier au moins égal à 1 qui tient dans son type (E046).
    /// </summary>
    private IrExpression? _BindIncrement(IncrementExpression increment)
    {
        var what = _Text(increment.Operator);
        if (increment.Operand is not NameExpression { Name: var name })
        {
            _Error(increment.Operand.Span, DiagnosticCodes.TypeMismatch, $"« {what} » modifie une variable : il s'applique à un nom", "pas une variable",
                $"par exemple : {what} compteur");
            return null;
        }

        var entry = _scope[name.Name];
        if (!entry.HasValue && _parameters.ContainsKey(name.Name))
        {
            _Error(name.Span, DiagnosticCodes.AssignToConstant, $"« {name.Name} » est un paramètre : il ne peut pas être modifié", "paramètre",
                "un paramètre est passé par référence constante : copie-le dans une variable pour le modifier");
            return null;
        }

        if (!entry.HasValue)
        {
            _ReportMissingVariable(name, DiagnosticCodes.UnknownVariable, $"la variable « {name.Name} » n'existe pas", $"déclare-la d'abord, par exemple : var {name.Name}: 0;");
            return null;
        }
        if (entry.Value.Symbol is not { } variable) return null;

        if (_ReportCaptured(name, variable)) return null;
        if (variable.IsConstant)
        {
            _Error(name.Span, DiagnosticCodes.AssignToConstant, $"« {name.Name} » est une constante : « {what} » ne peut pas la modifier", "constante",
                $"déclarée constante ligne {variable.Declaration.StartPosition.Line} : pour la modifier, déclare-la avec declare ou var");
            return null;
        }

        if ((variable.Type is IngotType ingot ? ingot.Underlying : variable.Type) is not PrimitiveType { IsNumeric: true } type)
        {
            _Error(name.Span, DiagnosticCodes.TypeMismatch, $"« {what} » s'applique à un nombre, pas à un « {variable.Type} »", $"de type « {variable.Type} »");
            return null;
        }

        long step = 1;
        if (increment.Step is { } literal)
        {
            var value = LiteralDecoder.Integer(literal.Text);
            if (value < 1 || value > long.MaxValue || !ConstantFolder.Fits((long)value, type.Unbounded))
            {
                _Error(literal.Span, DiagnosticCodes.InvalidIncrementStep, $"pas invalide : {literal.Text}", "pas",
                    value < 1 ? "le pas vaut au moins 1 ; pour l'autre sens, utilise decrement" : $"le pas doit tenir dans le type « {type} » de « {name.Name} »");
                return null;
            }
            step = (long)value;
        }

        var isDecrement = increment.Operator is TokenKind.DecrementKeyword or TokenKind.PostDecrementKeyword;
        var isPost = increment.Operator is TokenKind.PostIncrementKeyword or TokenKind.PostDecrementKeyword;
        return new IrIncrement(increment.Span, variable, isDecrement, isPost, step);
    }

    /// <summary>
    /// Le ternaire : la condition est un bool (E040), et les deux valeurs prennent un type
    /// commun, comme les opérandes d'une opération (E040 s'il n'y en a pas).
    /// </summary>
    /// <param name="expected">Le type attendu par le contexte : chaque valeur y est convertie, au lieu de chercher leur type commun.</param>
    private IrExpression? _BindConditional(ConditionalExpression conditional, IrType? expected = null)
    {
        var condition = _BindExpression(conditional.Condition);
        var facts = _NullFacts(condition);
        var whenTrue = _WithNarrowings(facts.WhenTrue, () => _BindExpression(conditional.WhenTrue));
        var whenFalse = _WithNarrowings(facts.WhenFalse, () => _BindExpression(conditional.WhenFalse));
        if (condition is null || whenTrue is null || whenFalse is null) return null;

        if (condition.Type != PrimitiveType.Bool)
        {
            _Error(condition.Span, DiagnosticCodes.TypeMismatch, $"la condition d'un ternaire est un bool, pas un « {condition.Type} »", $"de type « {condition.Type} »",
                "par exemple : n < 0 ? -1 : 1");
            return null;
        }

        if (expected is not null)
        {
            var why = $"ce ternaire doit donner un « {expected} »";
            var (first, second) = (_ConvertTo(whenTrue, expected, why), _ConvertTo(whenFalse, expected, why));
            return first is null || second is null ? null : new IrConditional(conditional.Span, expected, condition, first, second);
        }

        if (_Unify(whenTrue, whenFalse) is not { } unified)
        {
            _Error(conditional.Span, DiagnosticCodes.TypeMismatch, $"les deux valeurs du ternaire n'ont pas de type commun : « {whenTrue.Type} » et « {whenFalse.Type} »",
                $"« {whenTrue.Type} » ou « {whenFalse.Type} »", "les deux valeurs doivent avoir le même type, ou un type commun sans perte (deux molds différents ne perdent jamais de champ en silence)",
                "donne-leur un type attendu (déclaration typée, return) ou un type précis (new Nom {…})");
            return null;
        }

        return new IrConditional(conditional.Span, unified.Left.Type, condition, unified.Left, unified.Right);
    }

    private static string _Text(TokenKind kind) => SyntaxFacts.GetText(kind).ContinueIfNull(kind.ToString);

    #endregion
}
