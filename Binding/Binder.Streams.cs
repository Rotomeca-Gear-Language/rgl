using Rgl.AST;
using Rgl.AST.Expressions;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Types;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Le format <c>file</c> (slice 5.1e) : un flux ouvert. <c>var f &lt;- file "a.txt";</c> l'ouvre en lecture, <c>file&lt;write&gt;</c> le crée ou le remplace,
/// <c>file&lt;append&gt;</c> écrit à sa fin ; le type est <c>FileStream</c>. Décisions : le flux se ferme tout seul quand la dernière copie de la variable disparaît
/// (fin du bloc, <c>undefine</c>) et tout de suite par <c>f.close()</c>, pour toutes les copies ; <c>f.readLine()</c> (la ligne suivante, <c>null</c> en
/// fin de fichier), <c>f.readBytes(n)</c> (jusqu'à <c>n</c> octets), <c>f.write(texte)</c> et <c>f.write(octets)</c> ; tout sauf <c>close</c> dans un guard, où
/// un échec (fichier absent, flux fermé ou de l'autre sens, texte qui n'est pas de l'UTF-8) est une <c>IoError</c> ; <c>const</c> d'un flux avertit (W016).
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Vrai si <paramref name="target"/> est une variable ou un paramètre de type <c>FileStream</c> : seul un nom est reconnu, sans lier ce qu'il désigne deux fois.</summary>
    private bool _IsFileStream(IExpression target)
    {
        if (target is not NameExpression name) return false;
        var key = name.Name.Name;
        var entry = _scope[key];
        if (entry.HasValue) return entry.Value.Symbol?.Type is PrimitiveType { Kind: PrimitiveKind.FileStream };
        return _parameters.TryGetValue(key, out var parameter) && parameter.Type is PrimitiveType { Kind: PrimitiveKind.FileStream };
    }

    /// <summary><c>&lt;- file chemin</c>, <c>&lt;- file&lt;write&gt; chemin</c>, <c>&lt;- file&lt;append&gt; chemin</c>.</summary>
    private IrExpression? _BindFileOpen(ReadExpression read)
    {
        const string example = "var f <- file \"a.txt\";";
        var path = _BindFilePath(read.Path, read.Source.Span, "file", example);
        if (!_AllowInputOutput(read.Span, "ouvrir un fichier") || !_RequireGuard(read.Span, "l'ouverture d'un fichier") || path is null) return null;

        var mode = FileOpenMode.Read;
        if (read.Custom is { } written)
        {
            var name = written.Type is AstTypes.NamedType { TypeArguments: null } named ? named.Name.Name : null;
            switch (name)
            {
                case "read": break;
                case "write": mode = FileOpenMode.Write; break;
                case "append": mode = FileOpenMode.Append; break;
                default:
                    _Error(written.Span, DiagnosticCodes.ExpectedToken, "« file » s'ouvre en read, write ou append", "mode inconnu",
                        "par exemple : var f <- file<write> \"a.txt\"; (sans chevrons, en lecture)");
                    return null;
            }
        }
        return new IrOpenFile(read.Span, path, mode);
    }

    /// <summary><c>f.readLine()</c>, <c>f.readBytes(n)</c>, <c>f.write(x)</c>, <c>f.close()</c> sur un <c>FileStream</c>.</summary>
    private IrExpression? _BindFileOperation(CallExpression call, MemberAccessExpression member)
    {
        var name = member.Member.Name;
        var target = _BindExpression(member.Target);
        if (target is null) return null;

        var expected = name is "readBytes" or "write" ? 1 : 0;
        if (call.Arguments.Count != expected)
        {
            _Error(call.Span, DiagnosticCodes.ArgumentCount, $"« {name} » attend {expected} argument(s), pas {call.Arguments.Count}", $"{call.Arguments.Count} argument(s)",
                name switch
                {
                    "readBytes" => "par exemple : f.readBytes(1024)",
                    "write" => "par exemple : f.write(\"texte\")",
                    _ => $"par exemple : f.{name}()",
                });
            return null;
        }

        if (!_AllowInputOutput(call.Span, name == "close" ? "fermer un fichier" : "lire ou écrire un fichier")) return null;
        if (name != "close" && !_RequireGuard(call.Span, $"{name} sur un flux")) return null;

        switch (name)
        {
            case "readLine":
                return new IrFileOp(call.Span, target, FileOperation.ReadLine, null, new NullableType(PrimitiveType.String));

            case "readBytes":
                var count = _BindExpected(call.Arguments[0], PrimitiveType.UInt);
                var counted = count is null ? null : _ConvertTo(count, PrimitiveType.UInt, "readBytes lit un nombre d'octets");
                return counted is null ? null : new IrFileOp(call.Span, target, FileOperation.ReadBytes, counted, _bytes);

            case "write":
                var value = _BindExpression(call.Arguments[0]);
                if (value is null) return null;
                if (value.Type is PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString })
                    value = _Coerce(value, PrimitiveType.String);
                else if (value.Type is not ArrayType { Element: PrimitiveType { Kind: PrimitiveKind.UByte } })
                {
                    _Error(value.Span, DiagnosticCodes.TypeMismatch, $"« write » écrit une string ou des bytes, pas un « {value.Type} »", $"de type « {value.Type} »",
                        "pour écrire un nombre ou un mold, mets-le d'abord en texte : f.write(`{valeur}`)");
                    return null;
                }
                return value is null ? null : new IrFileOp(call.Span, target, FileOperation.Write, value, PrimitiveType.Void);

            default:
                return new IrFileOp(call.Span, target, FileOperation.Close, null, PrimitiveType.Void);
        }
    }
}
