using Rgl.IR.Expressions;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les flux de fichier (<c>FileStream</c>) : <c>rgl::standard::file::stream</c> (file.hpp), un fichier ouvert que ses copies se partagent et qui se ferme avec
/// la dernière. Ouvrir et chaque méthode rendent un <c>status</c> que le guard transforme en <c>IoError</c> (le message du statut, le chemin du flux).
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary><c>&lt;- file chemin</c>, dans un guard : le flux ouvert, ou l'échec du guard.</summary>
    private string _OpenFile(IrOpenFile open, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.File, "rgl/standard/file.hpp");
        _usesStatementExpressions = true;
        var (path, stream, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var mode = open.Mode switch
        {
            FileOpenMode.Write => "write",
            FileOpenMode.Append => "append",
            _ => "read",
        };
        return $"({{ const auto& {path} = {operand(open.Path)}; rgl::standard::file::stream {stream}; "
            + $"const auto {status} = rgl::standard::file::open({path}, rgl::standard::file::open_mode::{mode}, {stream}); "
            + $"if ({status} != rgl::standard::file::status::ok) return {_IoFailure(status, path, open.Span)}; std::move({stream}); }})";
    }

    /// <summary>Une méthode d'un flux : <c>close</c> tout simplement, les autres dans un guard avec l'échec en <c>IoError</c>.</summary>
    private string _FileOperation(IrFileOp operation, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.File, "rgl/standard/file.hpp");
        var stream = $"rgl_t{++_temporaryCount}";
        if (operation.Operation == FileOperation.Close) return $"static_cast<void>(({operand(operation.Target)}).close())";

        _usesStatementExpressions = true;
        var (result, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var fail = $"if ({status} != rgl::standard::file::status::ok) return {_IoFailure(status, $"{stream}.path()", operation.Span)};";
        var open = $"({{ const auto& {stream} = {operand(operation.Target)}; ";
        switch (operation.Operation)
        {
            case FileOperation.ReadLine:
                return open + $"{_Type(operation.Type)} {result}; const auto {status} = {stream}.read_line({result}); {fail} std::move({result}); }})";

            case FileOperation.ReadBytes:
                return open + $"{_Type(operation.Type)} {result}; const auto {status} = {stream}.read_bytes(static_cast<std::size_t>({operand(operation.Argument!)}), {result}); {fail} std::move({result}); }})";

            default:
                var method = operation.Argument!.Type is PrimitiveType ? "write_text" : "write_bytes";
                return open + $"const auto {status} = {stream}.{method}({operand(operation.Argument!)}); {fail} }})";
        }
    }
}
