using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Les fichiers entiers (<c>&lt;- raw chemin</c>, <c>valeur -&gt; raw chemin;</c>) : le composant <c>File</c> du
/// runtime rend un <c>status</c>, que le guard qui lit ou écrit transforme en <c>IoError</c> — le message du statut, la
/// position dans le source, le code 5 et le chemin — quand il n'est pas <c>ok</c>.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Le code d'une <c>IoError</c> : <c>errorCode.IO</c>.</summary>
    private const string IoErrorCode = "5u";

    /// <summary>
    /// <c>&lt;- raw chemin</c>, dans un guard : le texte ou les octets du fichier, ou l'échec du guard
    /// (<c>rgl::standard::file::read_text</c>, <c>read_bytes</c>).
    /// </summary>
    private string _ReadFile(IrReadFile read, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.File, "rgl/standard/file.hpp");
        _usesStatementExpressions = true;
        var (path, content, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var function = read.IsText ? "read_text" : "read_bytes";
        return $"({{ const auto& {path} = {operand(read.Path)}; {_Type(read.Type)} {content}; const auto {status} = rgl::standard::file::{function}({path}, {content}); "
            + $"if ({status} != rgl::standard::file::status::ok) return {_IoFailure(status, path, read.Span)}; std::move({content}); }})";
    }

    /// <summary>
    /// <c>valeur -&gt; raw chemin;</c>, dans un guard : un bloc qui écrit le fichier (<c>write_text</c>,
    /// <c>write_bytes</c>) et fait échouer le guard si le système refuse. La valeur est évaluée avant le chemin.
    /// </summary>
    private string _WriteFile(IrWriteFile write)
    {
        _Use(RuntimeComponent.File, "rgl/standard/file.hpp");
        var (value, path, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var function = write.Value.Type is PrimitiveType ? "write_text" : "write_bytes";
        return $"{{ const auto& {value} = {_Expression(write.Value)}; const auto& {path} = {_Expression(write.Path)}; "
            + $"const auto {status} = rgl::standard::file::{function}({path}, {value}); "
            + $"if ({status} != rgl::standard::file::status::ok) return {_IoFailure(status, path, write.Span)}; }}";
    }

    /// <summary><c>texte as bytes</c> : <c>rgl::standard::bytes::from_string</c>.</summary>
    private string _StringToBytes(IrStringToBytes bytes, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
        _includes.Add("rgl/standard/bytes.hpp");
        return $"rgl::standard::bytes::from_string({operand(bytes.Value)})";
    }

    /// <summary>
    /// <c>octets as? string</c> : <c>rgl::standard::bytes::try_to_string</c> ; <c>octets as! string</c>, dans un guard : la chaîne, ou
    /// l'échec du guard avec une <c>CastError</c>.
    /// </summary>
    private string _BytesToString(IrBytesToString text, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
        _includes.Add("rgl/standard/bytes.hpp");
        if (!text.IsForced) return $"rgl::standard::bytes::try_to_string({operand(text.Value)})";

        _usesStatementExpressions = true;
        var octets = $"rgl_t{++_temporaryCount}";
        var message = _String(new IrStringConstant(text.Span, PrimitiveType.String, "les octets ne sont pas de l'UTF-8 valide"));
        var target = _String(new IrStringConstant(text.Span, PrimitiveType.String, "string"));
        return $"({{ const auto& {octets} = {operand(text.Value)}; if (!rgl::standard::bytes::is_text({octets})) return {_StandardFailure(_castErrorMold, message, text.Span, "{}", target)}; "
            + $"rgl::standard::bytes::to_string({octets}); }})";
    }

    /// <summary>L'échec du guard courant avec une <c>IoError</c> : le message du <c>status</c> et le chemin du fichier.</summary>
    private string _IoFailure(string status, string path, SourceSpan span) =>
        _StandardFailure(_ioErrorMold, $"rgl::standard::file::message({status})", span, IoErrorCode, path);
}
