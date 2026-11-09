using System.Globalization;
using System.Numerics;
using Rgl.AST.Expressions;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Les durées : littéraux <c>100ms</c>, <c>1.5s</c>, <c>2min</c>, <c>1h</c> et opérations. Décisions :
/// le type <c>duration</c> compte des millisecondes sur 64 bits signés ; une durée ne se mélange pas
/// avec un entier sauf pour additionner ou soustraire (<c>d + 5</c> : cinq millisecondes), multiplier
/// (<c>3 * 1s</c>) ou diviser (<c>d / 2</c>) ; <c>as</c> passe d'un nombre à une durée en millisecondes ; deux durées
/// s'additionnent, se soustraient, se comparent, et leur quotient est un entier (<c>d / 1s</c> : les
/// secondes entières) ; un débordement arrête le programme.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Une durée littérale, ramenée à ses millisecondes (E126 si elle ne tient pas ou n'est pas entière).</summary>
    private IrExpression? _BindDuration(LiteralExpression literal)
    {
        var text = literal.Text.Replace("_", "");
        var split = text.Length;
        while (split > 0 && char.IsLetter(text[split - 1])) --split;
        var (number, suffix) = (text[..split], text[split..]);
        var unit = suffix switch { "ms" => 1L, "s" => 1000L, "min" => 60_000L, _ => 3_600_000L };

        BigInteger milliseconds;
        if (number.Contains('.') || number.Contains('e') || number.Contains('E'))
        {
            decimal? value = decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            if (value is not { } exact || exact * unit != decimal.Truncate(exact * unit))
            {
                _Error(literal.Span, DiagnosticCodes.InvalidDuration, $"durée invalide : {literal.Text}", "pas un nombre entier de millisecondes",
                    "la plus petite durée est la milliseconde : écris par exemple 1500ms ou 1.5s");
                return null;
            }
            milliseconds = new BigInteger(exact * unit);
        }
        else milliseconds = LiteralDecoder.Integer(number) * unit;

        if (milliseconds > long.MaxValue)
        {
            _Error(literal.Span, DiagnosticCodes.InvalidDuration, $"durée trop grande : {literal.Text}", "hors limites",
                $"une durée vaut au plus {long.MaxValue} millisecondes");
            return null;
        }
        return new IrDurationConstant(literal.Span, (long)milliseconds);
    }

    /// <summary>
    /// Une opération dont un opérande au moins est une date : <c>date ± durée</c>, <c>durée + date</c>, <c>date - date</c> (une durée), les
    /// comparaisons et <c>eq</c> entre deux dates (E040 sinon : une date ne se mélange pas avec un entier).
    /// </summary>
    private IrExpression? _BindDateTimeOperation(SourceSpan span, SourceSpan operatorSpan, TokenKind op, IrExpression left, IrExpression right)
    {
        static bool IsDate(IrExpression e) => e.Type == PrimitiveType.DateTime;
        static bool IsSpan(IrExpression e) => e.Type == PrimitiveType.Duration;

        (IrBinaryOperator Operator, IrType Type)? bound = null;
        if (IsDate(left) && IsDate(right))
        {
            bound = op switch
            {
                TokenKind.Minus => (IrBinaryOperator.Subtract, PrimitiveType.Duration),
                TokenKind.Less => (IrBinaryOperator.Less, PrimitiveType.Bool),
                TokenKind.Greater => (IrBinaryOperator.Greater, PrimitiveType.Bool),
                TokenKind.LessEqual => (IrBinaryOperator.LessOrEqual, PrimitiveType.Bool),
                TokenKind.GreaterEqual => (IrBinaryOperator.GreaterOrEqual, PrimitiveType.Bool),
                TokenKind.EqKeyword => (IrBinaryOperator.Equal, PrimitiveType.Bool),
                _ => null,
            };
        }
        else if (IsDate(left) && IsSpan(right) && op is TokenKind.Plus or TokenKind.Minus)
            bound = (op == TokenKind.Plus ? IrBinaryOperator.Add : IrBinaryOperator.Subtract, PrimitiveType.DateTime);
        else if (IsSpan(left) && IsDate(right) && op == TokenKind.Plus) bound = (IrBinaryOperator.Add, PrimitiveType.DateTime);

        if (bound is not { } result)
        {
            _Error(span, DiagnosticCodes.TypeMismatch, $"« {_Text(op)} » ne s'applique pas à « {left.Type} » et « {right.Type} »", $"« {left.Type} » {_Text(op)} « {right.Type} »",
                "une date s'ajoute ou se retranche avec une durée (d + 2h), deux dates se soustraient (b - a : une durée) et se comparent ; pour un nombre de millisecondes, passe par as");
            return null;
        }
        return new IrBinary(span, result.Type, result.Operator, left, right, operatorSpan);
    }

    /// <summary>Une opération dont un opérande au moins est une durée (E040 si elle n'existe pas).</summary>
    private IrExpression? _BindDurationOperation(SourceSpan span, SourceSpan operatorSpan, TokenKind op, IrExpression left, IrExpression right)
    {
        static bool IsDuration(IrExpression e) => e.Type == PrimitiveType.Duration;
        static bool IsCount(IrExpression e) => e.Type is PrimitiveType { IsInteger: true, Kind: not PrimitiveKind.ULong };

        (IrBinaryOperator Operator, IrType Type)? bound = null;
        if (IsDuration(left) && IsDuration(right))
        {
            bound = op switch
            {
                TokenKind.Plus => (IrBinaryOperator.Add, PrimitiveType.Duration),
                TokenKind.Minus => (IrBinaryOperator.Subtract, PrimitiveType.Duration),
                TokenKind.Slash => (IrBinaryOperator.Divide, new PrimitiveType(PrimitiveKind.Long)),
                TokenKind.Less => (IrBinaryOperator.Less, PrimitiveType.Bool),
                TokenKind.Greater => (IrBinaryOperator.Greater, PrimitiveType.Bool),
                TokenKind.LessEqual => (IrBinaryOperator.LessOrEqual, PrimitiveType.Bool),
                TokenKind.GreaterEqual => (IrBinaryOperator.GreaterOrEqual, PrimitiveType.Bool),
                TokenKind.EqKeyword => (IrBinaryOperator.Equal, PrimitiveType.Bool),
                _ => null,
            };
        }
        else if (op is TokenKind.Plus or TokenKind.Minus && (IsDuration(left) && IsCount(right) || IsCount(left) && IsDuration(right)))
            bound = (op == TokenKind.Plus ? IrBinaryOperator.Add : IrBinaryOperator.Subtract, PrimitiveType.Duration);
        else if (op == TokenKind.Star && (IsDuration(left) && IsCount(right) || IsCount(left) && IsDuration(right))) bound = (IrBinaryOperator.Multiply, PrimitiveType.Duration);
        else if (op == TokenKind.Slash && IsDuration(left) && IsCount(right)) bound = (IrBinaryOperator.Divide, PrimitiveType.Duration);

        if (bound is not { } result)
        {
            _Error(span, DiagnosticCodes.TypeMismatch, $"« {_Text(op)} » ne s'applique pas à « {left.Type} » et « {right.Type} »", $"« {left.Type} » {_Text(op)} « {right.Type} »",
                "une durée s'additionne et se soustrait avec une durée ou un entier (des millisecondes), se multiplie ou se divise par un entier (3 * 1s, d / 2), et d / 1s donne un nombre entier de secondes");
            return null;
        }

        var zero = ConstantFolder.Fold(right) is 0L or ConstantFolder.DurationValue { Milliseconds: 0 };
        if (result.Operator == IrBinaryOperator.Divide && zero)
        {
            _Error(right.Span, DiagnosticCodes.DivisionByZero, "division entière par zéro", "vaut 0",
                "le programme s'arrêterait ici : une division entière par zéro n'a pas de résultat");
            return null;
        }
        return new IrBinary(span, result.Type, result.Operator, left, right, operatorSpan);
    }
}
