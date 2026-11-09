using Rgl.AST.Expressions;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// Le format <c>json</c> (slice 5.1c). <c>Config c &lt;- json "a.json";</c> ou <c>var c &lt;- json&lt;Config&gt; "a.json";</c> lit un
/// fichier JSON en une valeur du type attendu — un type écrit, ou celui de la déclaration ; sans l'un ni l'autre le type du JSON est
/// inconnu à la compilation (E001). <c>json&lt;{int x, string nom}&gt;</c> donne un mold anonyme. Décisions : un champ du JSON qui n'est
/// pas dans le mold est ignoré, un champ absent prend la valeur par défaut du mold (<c>null</c> pour un nullable), sinon c'est une erreur ;
/// un entier JSON se lit dans un type entier (sinon erreur), un nombre dans un flottant ; une borne violée, un type qui ne convient pas ou
/// un JSON invalide sont une <c>ParseError</c> qui porte le chemin de la valeur fautive (<c>$.joueurs[2].niveau</c>) ; un fichier
/// introuvable une <c>IoError</c>. Comme tout fichier, dans un guard seulement. Les types sans forme en JSON — lambda, symbole — sont
/// refusés ; union, résultat et clé de dictionnaire qui n'est pas une <c>string</c> ne sont pas encore pris en charge.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>
    /// <c>&lt;- json chemin</c> : le type lu est celui qui est écrit entre chevrons, sinon celui que le contexte attend (E001 sans
    /// l'un ni l'autre).
    /// </summary>
    private IrExpression? _BindJsonRead(ReadExpression read, IrType? expected)
    {
        const string example = "Config c <- json \"a.json\";";
        var path = _BindFilePath(read.Path, read.Source.Span, "json", example);
        if (!_AllowInputOutput(read.Span, "lire un fichier") || !_RequireGuard(read.Span, "la lecture d'un fichier") || path is null) return null;

        var type = read.Custom is { } written ? _BindType(written.Type) : expected;
        if (type is null)
        {
            if (read.Custom is null)
                _Error(read.Span, DiagnosticCodes.CannotInferType, "le type du JSON lu est inconnu à la compilation", "de type inconnu",
                    "écris le type lu : json<Config> \"a.json\", un mold anonyme json<{int x, string nom}> \"a.json\", ou celui de la variable : Config c <- json \"a.json\";");
            return null;
        }
        return _CheckJsonType(type, read.Span, "lire") ? new IrReadJson(read.Span, path, type) : null;
    }

    /// <summary>
    /// <c>valeur -&gt; json chemin;</c> (ou <c>json&lt;T&gt;</c> : la valeur doit alors se convertir en <c>T</c>) : écrit la valeur en JSON,
    /// dans un guard.
    /// </summary>
    private IrStatement? _BindJsonWrite(WriteStatement write, IrExpression? value)
    {
        var path = _BindFilePath(write.Path, write.Target.Span, "json", "config -> json \"a.json\";");
        if (!_AllowInputOutput(write.Span, "écrire un fichier") || !_RequireGuard(write.Span, "l'écriture d'un fichier") || path is null || value is null) return null;

        if (write.Custom is { } written)
        {
            var type = _BindType(written.Type);
            value = type is null ? null : _ConvertTo(value, type, $"json<{type}> écrit un « {type} »");
            if (value is null) return null;
        }
        return _CheckJsonType(value.Type, write.Span, "écrire") ? new IrWriteJson(write.Span, value, path) : null;
    }

    /// <summary>Vrai si <paramref name="type"/> a une forme en JSON ; sinon signale pourquoi.</summary>
    private bool _CheckJsonType(IrType type, SourceSpan span, string what)
    {
        var problem = _JsonProblem(type, []);
        if (problem is null) return true;

        if (problem.Value.Never)
            _Error(span, DiagnosticCodes.TypeMismatch, $"on ne peut pas {what} en JSON un « {type} » : {problem.Value.Why}", "sans forme en JSON",
                "le JSON a des booléens, des nombres, des chaînes, des tableaux et des objets (molds, dictionnaires) ; une lambda ou un symbole n'en ont pas");
        else
            _Unsupported(span, $"{what} en JSON : {problem.Value.Why}");
        return false;
    }

    /// <summary>
    /// Ce qui, dans <paramref name="type"/>, n'a pas de forme en JSON : <c>Never</c> si le type n'en aura jamais (lambda, symbole),
    /// sinon ce qui n'est pas encore pris en charge ; <see langword="null"/> si tout se lit et s'écrit.
    /// </summary>
    private static (string Why, bool Never)? _JsonProblem(IrType type, HashSet<MoldSymbol> seen)
    {
        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.Symbol }:
                return ("un symbole n'est pas une donnée : il n'existe que dans le programme", true);
            case PrimitiveType { Kind: PrimitiveKind.Regex }:
                return ("une regex n'est pas une donnée : elle n'existe que dans le programme", true);
            case PrimitiveType { Kind: PrimitiveKind.Duration }:
                return ("une durée", false);
            case PrimitiveType { Kind: PrimitiveKind.DateTime }:
                return ("une date", false);
            case PrimitiveType { Kind: PrimitiveKind.Void }:
                return ("void n'a pas de valeur", true);
            case PrimitiveType:
            case EnumType:
                return null;
            case NullableType nullable:
                return _JsonProblem(nullable.Inner, seen);
            case IngotType ingot:
                return _JsonProblem(ingot.Underlying, seen);
            case ArrayType array:
                return _JsonProblem(array.Element, seen);
            case TupleType tuple:
                return tuple.Elements.Select(e => _JsonProblem(e, seen)).FirstOrDefault(p => p is not null);
            case DictionaryType dictionary:
                if (_Representation(dictionary.Key) is not PrimitiveType { Kind: PrimitiveKind.String })
                    return ($"un dictionnaire a pour clés des « {dictionary.Key} » : seules les clés « string » ont une forme en JSON", false);
                return _JsonProblem(dictionary.Value, seen);
            case MoldType mold:
                if (!seen.Add(mold.Symbol)) return null;
                return mold.Symbol.Fields.Select(f => _JsonProblem(f.Type, seen)).FirstOrDefault(p => p is not null);
            case FunctionType:
                return ("une lambda n'est pas une donnée", true);
            case UnionType:
            case ResultType:
                return ($"un « {type} »", false);
            default:
                return ($"un « {type} »", false);
        }
    }

    /// <summary>Le type qui représente une valeur : la base d'un ingot, sous toutes ses marques.</summary>
    private static IrType _Representation(IrType type) => type is IngotType ingot ? ingot.Underlying : type;

    /// <summary>
    /// <c>parseJson&lt;T&gt;(texte)</c> et <c>toJson(valeur)</c> : le JSON d'un texte en mémoire, avec les mêmes règles que le format <c>json</c>
    /// d'un fichier. Pas de fichier, donc pas de guard : le résultat est une valeur <c>T ! ParseError</c>, lue avec <c>match</c> ou <c>??</c>.
    /// </summary>
    private IrExpression? _BindJsonText(CallExpression call, bool parse)
    {
        if (_parseError is null)
        {
            _Unsupported(call.Span, "le JSON d'un texte sans la bibliothèque standard, qui définit ParseError");
            return null;
        }
        var error = new MoldType(_parseError);
        if (call.Arguments.Count != 1)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"{(parse ? "parseJson" : "toJson")} attend un seul argument, pas {call.Arguments.Count}", "un argument",
                parse ? "par exemple : parseJson<Config>(texte)" : "par exemple : toJson(config)");
            return null;
        }

        if (!parse)
        {
            var value = _BindExpression(call.Arguments[0]);
            if (value is null || !_CheckJsonType(value.Type, call.Span, "écrire")) return null;
            return new IrToJson(call.Span, value, new ResultType(PrimitiveType.String, error));
        }

        if (call.TypeArguments is not { Count: 1 })
        {
            _Error(call.Span, DiagnosticCodes.CannotInferType, "le type du JSON lu est inconnu à la compilation", "de type inconnu",
                "écris le type lu entre chevrons : parseJson<Config>(texte), ou un mold anonyme parseJson<{int x, string nom}>(texte)");
            return null;
        }
        var type = _BindType(call.TypeArguments[0]);
        var text = _BindExpected(call.Arguments[0], PrimitiveType.String);
        if (type is null || text is null || !_CheckJsonType(type, call.Span, "lire")) return null;
        if (_ConvertTo(text, PrimitiveType.String, "parseJson lit une string") is not { } converted) return null;
        return new IrParseJson(call.Span, converted, type, new ResultType(type, error));
    }
}
