using System.Globalization;
using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Binding;

/// <summary>
/// <c>spawn</c> (slice 5.2) : lance un processus enfant, attend sa fin, et donne un <c>ProcessResult ! SpawnError</c> (<c>std/Process.forge</c> : le code de
/// sortie et ce qui a été écrit, ou la raison de l'échec du lancement). Décisions : chaque élément est une chaîne — un littéral, un template ou une
/// variable <c>string</c>, jamais un nombre ni un drapeau nu ; <c>spawn</c> n'est qu'une instruction, ou la valeur d'une déclaration, d'une affectation
/// ou d'un <c>return</c>, jamais un argument ni une étape de pipeline (E124) ; il ne se fait pas dans un gear <c>pure</c> (E066) ni au niveau du fichier
/// (E053) ; ignorer le résultat d'un <c>spawn</c> écrit comme instruction avertit (W060) ; un exécutable <c>rgl</c> écrit en littéral est refusé (E124), et un
/// autre refusé à l'exécution (<c>PermissionDenied</c>) : un programme RGL ne relance pas le compilateur ; le résultat se lit comme tout résultat
/// (<c>match</c>, <c>??</c>) — le rail <c>!> SpawnError |> f</c> de la spec n'existe plus dans le langage ; l'attente est synchrone (l'asynchronisme inféré
/// est une autre slice) ; options entre parenthèses, une chaîne connue à la compilation : <c>timeout</c> (ms), <c>cwd</c>, <c>env</c> (<c>"A=1,B=2"</c>),
/// <c>stdin</c> (<c>null</c>, <c>none</c>), <c>stdout</c> et <c>stderr</c> (<c>pipe</c>, <c>inherit</c>, <c>null</c>) — une valeur peut s'écrire entre
/// guillemets pour contenir une virgule.
/// </summary>
internal sealed partial class Binder
{
    /// <summary><paramref name="expression"/>, liée en sachant qu'elle donne <paramref name="expected"/> et qu'un <c>spawn</c> y est permis.</summary>
    private IrExpression? _BindStatementValue(IExpression expression, IrType? expected)
    {
        _spawnAllowed = true;
        return _BindExpected(expression, expected);
    }

    /// <summary>Une expression écrite comme instruction ; un <c>spawn</c> dont on ignore le résultat avertit (W060).</summary>
    private IrStatement? _BindExpressionStatement(ExpressionStatement statement)
    {
        _spawnAllowed = statement.Expression is SpawnExpression;
        var bound = _BindExpression(statement.Expression);
        if (bound is null) return null;
        if (!_CheckDiscarded(statement.Span, bound)) return null;
        if (bound is IrDictionaryPut or IrDictionaryWithout)
        {
            var method = bound is IrDictionaryPut ? "put" : "without";
            _Error(statement.Span, DiagnosticCodes.TypeMismatch, $"« {method} » rend un nouveau dictionnaire : le résultat est perdu et le dictionnaire reste tel quel", "résultat perdu",
                $"écris : d <- d.{method}(…); (sur une variable mut) pour garder le résultat");
            return null;
        }
        if (bound is IrSpawn && _safe)
        {
            _Error(statement.Span, DiagnosticCodes.IgnoredResult, "le résultat de spawn est ignoré : il peut être une erreur, et le mode sûr n'en cache aucune", "résultat ignoré",
                "garde-le : var r: spawn \"make\"; puis lis-le avec match ou ??");
            return null;
        }
        if (bound is IrSpawn)
            _diagnostics.Warning(statement.Span, DiagnosticCodes.SpawnResultIgnored, "le résultat de spawn est ignoré : il peut être une erreur", "résultat ignoré",
                ["garde-le : var r: spawn \"make\"; puis lis-le avec match ou ??"]);
        return new IrExpressionStatement(statement.Span, bound);
    }

    private IrExpression? _BindSpawn(SpawnExpression spawn, bool allowed)
    {
        if (!allowed)
        {
            _Error(spawn.Span, DiagnosticCodes.InvalidSpawn, "spawn est une instruction : ce n'est ni un argument ni une étape de pipeline", "spawn ici",
                "écris-le seul, ou comme valeur d'une déclaration : var r: spawn \"make\";");
            return null;
        }
        if (!_AllowInputOutput(spawn.Span, "lancer un processus")) return null;
        if (_processResult is null || _spawnError is null)
        {
            _Unsupported(spawn.Span, "spawn sans la bibliothèque standard, qui définit ProcessResult et SpawnError");
            return null;
        }

        var options = SpawnOptions.Default;
        if (spawn.Options is { } written)
        {
            if (_BindSpawnOptions(written) is not { } parsed) return null;
            options = parsed;
        }

        List<IrExpression> parts = [];
        var failed = false;
        foreach (var part in spawn.Parts)
        {
            var bound = _BindExpected(part, PrimitiveType.String);
            var text = bound is null ? null : _ConvertTo(bound, PrimitiveType.String, "chaque élément de spawn est une chaîne");
            if (text is null) failed = true;
            else parts.Add(text);
        }
        if (failed) return null;

        if (parts[0] is IrStringConstant { Value: var program } && _IsCompiler(program))
        {
            _Error(spawn.Parts[0].Span, DiagnosticCodes.InvalidSpawn, "un programme RGL ne relance pas rgl", "le compilateur",
                "la décision d'exécuter le compilateur reste à l'humain");
            return null;
        }

        var type = new ResultType(new MoldType(_processResult), new MoldType(_spawnError));
        return new IrSpawn(spawn.Span, parts[0], parts.Skip(1).ToArray(), options, type);
    }

    /// <summary>Vrai pour <c>rgl</c> et <c>rgl.exe</c>, quel que soit le dossier et la casse.</summary>
    private static bool _IsCompiler(string program)
    {
        var name = program[(program.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        return name is "rgl" or "rgl.exe";
    }

    private static readonly string[] _spawnKeys = ["timeout", "cwd", "env", "stdin", "stdout", "stderr"];

    /// <summary>Les options d'un <c>spawn</c> : une chaîne littérale <c>clé=valeur,clé=valeur</c>, connue à la compilation.</summary>
    private SpawnOptions? _BindSpawnOptions(IExpression expression)
    {
        if (_BindExpression(expression) is not { } bound) return null;
        if (bound is not IrStringConstant { Value: var text })
        {
            _Error(expression.Span, DiagnosticCodes.InvalidSpawn, "les options de spawn sont une chaîne écrite dans le programme", "pas un littéral",
                "par exemple : spawn(\"timeout=5000,cwd=./build\") \"make\"");
            return null;
        }

        var span = expression.Span;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        uint timeout = 0;
        string? cwd = null;
        List<(string, string)> env = [];
        var input = SpawnInput.Null;
        var output = SpawnOutput.Pipe;
        var error = SpawnOutput.Pipe;

        foreach (var item in _SplitOptions(text))
        {
            var equals = item.IndexOf('=');
            if (equals <= 0)
            {
                _Error(span, DiagnosticCodes.InvalidSpawn, $"option « {item} » mal écrite : clé=valeur attendu", "clé=valeur", $"clés : {string.Join(", ", _spawnKeys)}");
                return null;
            }

            var key = item[..equals].Trim();
            var value = item[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (!_spawnKeys.Contains(key))
            {
                _Error(span, DiagnosticCodes.InvalidSpawn, $"option « {key} » inconnue", "option inconnue", $"options : {string.Join(", ", _spawnKeys)}");
                return null;
            }
            if (!seen.Add(key))
            {
                _Error(span, DiagnosticCodes.InvalidSpawn, $"l'option « {key} » est écrite deux fois", "en double");
                return null;
            }

            switch (key)
            {
                case "timeout":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timeout))
                    {
                        _Error(span, DiagnosticCodes.InvalidSpawn, $"timeout est un nombre de millisecondes entier positif, pas « {value} »", "durée invalide", "par exemple : timeout=5000");
                        return null;
                    }
                    break;

                case "cwd":
                    if (value.Length == 0)
                    {
                        _Error(span, DiagnosticCodes.InvalidSpawn, "cwd attend un dossier", "dossier manquant");
                        return null;
                    }
                    cwd = value;
                    break;

                case "env":
                    foreach (var variable in _SplitOptions(value))
                    {
                        var separator = variable.IndexOf('=');
                        if (separator <= 0)
                        {
                            _Error(span, DiagnosticCodes.InvalidSpawn, $"variable d'environnement « {variable} » mal écrite : NOM=valeur attendu", "NOM=valeur", "plusieurs variables : env=\"A=1,B=2\"");
                            return null;
                        }
                        env.Add((variable[..separator].Trim(), variable[(separator + 1)..]));
                    }
                    break;

                case "stdin":
                    switch (value)
                    {
                        case "null": input = SpawnInput.Null; break;
                        case "none": input = SpawnInput.None; break;
                        case "pipe":
                            _Unsupported(span, "stdin=pipe : écrire dans l'entrée d'un processus");
                            return null;
                        default:
                            _Error(span, DiagnosticCodes.InvalidSpawn, $"stdin vaut null ou none, pas « {value} »", "valeur inconnue");
                            return null;
                    }
                    break;

                default:
                    var mode = value switch
                    {
                        "pipe" => (SpawnOutput?)SpawnOutput.Pipe,
                        "inherit" => SpawnOutput.Inherit,
                        "null" => SpawnOutput.Null,
                        _ => null,
                    };
                    if (mode is null)
                    {
                        _Error(span, DiagnosticCodes.InvalidSpawn, $"{key} vaut pipe, inherit ou null, pas « {value} »", "valeur inconnue");
                        return null;
                    }
                    if (key == "stdout") output = mode.Value;
                    else error = mode.Value;
                    break;
            }
        }
        return new SpawnOptions(timeout, cwd, env, input, output, error);
    }

    /// <summary>Découpe aux virgules qui ne sont pas entre guillemets ; les éléments vides sont ignorés.</summary>
    private static IEnumerable<string> _SplitOptions(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in text)
        {
            if (character == '"') quoted = !quoted;
            if (character == ',' && !quoted)
            {
                if (current.ToString().Trim() is { Length: > 0 } item) yield return item;
                current.Clear();
            }
            else current.Append(character);
        }
        if (current.ToString().Trim() is { Length: > 0 } last) yield return last;
    }
}
