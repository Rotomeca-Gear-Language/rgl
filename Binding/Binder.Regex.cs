using Rgl.AST.Collections;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Utils.Types;

namespace Rgl.Binding;

/// <summary>
/// Les regex (slice 5.4) : le littéral <c>/motif/flags</c> et le type <c>regex</c>. Décisions : le moteur est CTRE (le motif
/// est compilé en code C++ par Clang, aucune analyse à l'exécution), donc une regex ne s'écrit qu'en littéral — jamais
/// construite d'un texte connu à l'exécution — ; <c>RegexSyntax</c> refuse ce que le moteur ne compile pas (E127, jamais
/// une erreur de Clang) ; flags <c>g</c> (toutes les occurrences pour <c>find</c> et <c>replace</c>), <c>i</c> (casse, ASCII
/// seulement) et <c>m</c> (<c>^</c> et <c>$</c> sur chaque ligne) ; le texte est lu en UTF-8, <c>.</c> voit un caractère Unicode ;
/// quatre méthodes : <c>r.test(s)</c> (<c>bool</c>), <c>r.match(s)</c> (<c>[string]?</c> : le texte trouvé puis les groupes),
/// <c>r.find(s)</c> (<c>[string]</c>), <c>r.replace(s, par)</c> (<c>string</c>, <c>$0</c> à <c>$9</c> et <c>$$</c> dans <c>par</c>) ;
/// une regex ne se compare pas et ne s'écrit pas en texte.
/// </summary>
internal sealed partial class Binder
{
    private static readonly string[] _regexMethods = ["test", "match", "find", "replace"];

    /// <summary>Une regex littérale : ses flags (g, i, m, sans doublon) et son motif sont vérifiés (E127).</summary>
    private IrExpression? _BindRegex(LiteralExpression literal)
    {
        var text = literal.Text;
        var close = text.LastIndexOf('/');
        var pattern = text[1..close];
        var written = text[(close + 1)..];

        var failed = false;
        var flags = new SortedSet<char>();
        foreach (var flag in written)
        {
            if (flag is not ('g' or 'i' or 'm'))
            {
                _Error(literal.Span, DiagnosticCodes.InvalidRegex, $"flag de regex inconnu « {flag} »", "flag inconnu",
                    "flags possibles : g (toutes les occurrences), i (sans tenir compte de la casse), m (^ et $ sur chaque ligne)");
                failed = true;
            }
            else if (!flags.Add(flag))
            {
                _Error(literal.Span, DiagnosticCodes.InvalidRegex, $"flag de regex en double « {flag} »", "déjà écrit", "retire le doublon");
                failed = true;
            }
        }

        if (RegexSyntax.Check(pattern) is { } problem)
        {
            var at = new SourceSpan(literal.Span.Source, literal.Span.Start + RUint.CreateChecked(1 + problem.Offset), RUint.CreateChecked(1));
            _Error(at, DiagnosticCodes.InvalidRegex, $"regex invalide : {problem.Message}", "ici",
                "le moteur de regex compile le motif en code : il accepte l'essentiel de la syntaxe usuelle (classes, groupes, répétitions, ancres, \\d \\w \\s), pas tout");
            failed = true;
        }

        return failed ? null : new IrRegexConstant(literal.Span, pattern, flags.Contains('g'), string.Concat(flags));
    }

    /// <summary>
    /// Le type de <paramref name="target"/> lu sans le lier : un littéral regex, un nom (variable ou paramètre), un champ de mold, un
    /// élément de tableau ou des parenthèses ; <see langword="null"/> pour toute autre forme.
    /// </summary>
    private IrType? _SyntacticType(IExpression target)
    {
        switch (target)
        {
            case LiteralExpression { Kind: TokenKind.RegexLiteral }:
                return PrimitiveType.Regex;

            case ParenthesizedExpression parenthesized:
                return _SyntacticType(parenthesized.Inner);

            case NameExpression name:
                var key = name.Name.Name;
                var entry = _scope[key];
                if (entry.HasValue) return entry.Value.Symbol?.Type;
                return _parameters.TryGetValue(key, out var parameter) ? parameter.Type : null;

            case MemberAccessExpression { IsSafe: false } member:
                // Moves.attaquer : l'identifiant d'une entrée de dataset.
                if (_DatasetNamed(member.Target) is { IdIngot: { } idIngot } dataset && dataset.Entries.Contains(member.Member.Name)) return new IngotType(idIngot);
                return _SyntacticType(member.Target) is MoldType mold ? mold.Symbol[member.Member.Name]?.Type : null;

            case IndexExpression index:
                return _SyntacticType(index.Target) is ArrayType array ? array.Element : null;

            // a ?? b : le type de a sans son null (ou celui de b quand a est null écrit) ; c ? a : b : celui de a.
            case BinaryExpression { Operator: TokenKind.QuestionQuestion } coalesce:
                return _SyntacticType(coalesce.Left) is NullableType nullable ? nullable.Inner : _SyntacticType(coalesce.Left);

            case ConditionalExpression conditional:
                return _SyntacticType(conditional.WhenTrue);

            // id.get() : l'entrée du dataset ; id.name() : son nom ; Moves.find(nom) : son identifiant, nullable.
            case CallExpression { Callee: MemberAccessExpression { IsSafe: false } dataCall } when dataCall.Member.Name is "get" or "name" && _SyntacticType(dataCall.Target) is IngotType { Symbol.Dataset: { Mold: { } entryMold } }:
                return dataCall.Member.Name == "get" ? new MoldType(entryMold) : PrimitiveType.String;

            case CallExpression { Callee: MemberAccessExpression { IsSafe: false, Member.Name: "find" } findCall } when _DatasetNamed(findCall.Target) is { IdIngot: { } findIngot }:
                return new NullableType(new IngotType(findIngot));

            // f(x) : ce que renvoie le gear de ce nom.
            case CallExpression { Callee: NameExpression callee } when _TryFind(_gearOverloads, callee.Name.Name, out var called) && called.Count == 1:
                return called[0].ReturnType;

            default:
                return null;
        }
    }

    /// <summary><c>r.test(s)</c>, <c>r.match(s)</c>, <c>r.find(s)</c>, <c>r.replace(s, par)</c>.</summary>
    private IrExpression? _BindRegexOperation(CallExpression call, MemberAccessExpression member)
    {
        var name = member.Member.Name;
        var target = _BindExpression(member.Target);

        var expected = name == "replace" ? 2 : 1;
        if (call.Arguments.Count != expected)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"« {name} » attend {expected} argument(s), pas {call.Arguments.Count}", $"{call.Arguments.Count} argument(s)",
                name == "replace" ? "par exemple : /a+/g.replace(texte, \"b\")" : $"par exemple : r.{name}(texte)");
            return null;
        }

        var arguments = call.Arguments.Select(argument => _BindExpected(argument, PrimitiveType.String)).ToArray();
        if (target is null || arguments.Any(a => a is null)) return null;

        var converted = new IrExpression?[arguments.Length];
        for (var i = 0; i < arguments.Length; ++i)
        {
            converted[i] = _ConvertTo(arguments[i]!, PrimitiveType.String, i == 0 ? $"« {name} » cherche dans une string" : "le remplacement est une string");
            if (converted[i] is null) return null;
        }

        return name switch
        {
            "test" => new IrRegexOp(call.Span, target, RegexOperation.Test, converted[0]!, null, PrimitiveType.Bool),
            "match" => new IrRegexOp(call.Span, target, RegexOperation.Match, converted[0]!, null, new NullableType(_StringArray)),
            "find" => new IrRegexOp(call.Span, target, RegexOperation.Find, converted[0]!, null, _StringArray),
            _ => new IrRegexOp(call.Span, target, RegexOperation.Replace, converted[0]!, converted[1], PrimitiveType.String),
        };
    }

    private static ArrayType _StringArray => new(PrimitiveType.String);
}
