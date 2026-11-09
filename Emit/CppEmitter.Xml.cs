using System.Text;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Le XML (<c>&lt;- xml chemin</c>, <c>valeur -&gt; xml chemin;</c>). Le runtime lit un document en un arbre neutre
/// (<c>rgl::standard::xml::element</c>) et l'écrit depuis lui ; le compilateur écrit les deux fonctions qui passent de cet arbre au struct du mold
/// <c>XmlNode</c> de la std (<c>rgl_xml_to_node</c>) et inversement (<c>rgl_xml_from_node</c>), seulement si le programme lit ou écrit du XML.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Le mold <c>XmlNode</c> de la std ; <see langword="null"/> sans la std.</summary>
    private MoldSymbol? _xmlNodeMold;

    private bool _xmlRead;

    private bool _xmlWrite;

    /// <summary>Le C++ des fonctions qui convertissent un <c>XmlNode</c> et l'arbre du runtime, écrites seulement si un programme en a besoin.</summary>
    private string _XmlFunctions()
    {
        if (!_xmlRead && !_xmlWrite) return "";

        var mold = _xmlNodeMold!;
        var node = _Type(new MoldType(mold));
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append('\n');

        if (_xmlRead)
        {
            // Les champs du struct dans l'ordre du mold : le nom, les attributs, les fils et le texte viennent de l'élément.
            var initializers = string.Join(", ", mold.Fields.Select(f => f.Name switch
            {
                "name" => "e.name",
                "attributes" => "std::move(attributes)",
                "children" => "std::move(children)",
                "text" => "e.text",
                _ => "{}",
            }));
            Line($"static {node} rgl_xml_to_node(const rgl::standard::xml::element& e) {{");
            Line($"{Indent}{_Type(mold["children"]!.Type)} children;");
            Line($"{Indent}children.reserve(e.children.size());");
            Line($"{Indent}for (const auto& child : e.children) children.push(rgl_xml_to_node(child));");
            Line($"{Indent}{_Type(mold["attributes"]!.Type)} attributes;");
            Line($"{Indent}for (const auto& attribute : e.attributes) attributes.add(attribute.first, attribute.second, \"xml\");");
            Line($"{Indent}return {node}{{{initializers}}};");
            Line("}");
            Line("");
        }

        if (_xmlWrite)
        {
            Line($"static rgl::standard::xml::element rgl_xml_from_node(const {node}& node) {{");
            Line($"{Indent}rgl::standard::xml::element e;");
            Line($"{Indent}e.name = node.rgl_f_name;");
            Line($"{Indent}for (const auto& [key, value] : node.rgl_f_attributes) e.attributes.emplace_back(key, value);");
            Line($"{Indent}e.text = node.rgl_f_text;");
            Line($"{Indent}for (std::size_t i = 0; i < node.rgl_f_children.size(); ++i) e.children.push_back(rgl_xml_from_node(node.rgl_f_children[i]));");
            Line($"{Indent}return e;");
            Line("}");
            Line("");
        }
        return text.ToString();
    }

    /// <summary>
    /// <c>&lt;- xml chemin</c>, dans un guard : l'élément racine, ou l'échec du guard — une <c>IoError</c> si le fichier manque, une
    /// <c>ParseError</c> si le XML ne convient pas.
    /// </summary>
    private string _ReadXml(IrReadXml read, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.Xml, "rgl/standard/xml.hpp");
        _usesStatementExpressions = true;
        _xmlRead = true;
        _Type(read.Type);
        var (path, root, error, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        return $"({{ const auto& {path} = {operand(read.Path)}; rgl::standard::xml::element {root}; rgl::standard::xml::failure {error}; "
            + $"const auto {status} = rgl::standard::xml::read({path}, {root}, {error}); "
            + $"if (!{status}.ok) {{ if ({status}.io) return {_IoFailure($"{status}.io_status", path, read.Span)}; return {_XmlFailure(error, path, read.Span)}; }} "
            + $"rgl_xml_to_node({root}); }})";
    }

    /// <summary>
    /// <c>noeud -&gt; xml chemin;</c>, dans un guard : un bloc qui écrit le nœud, puis le fichier, et fait échouer le guard si l'un des deux ne va pas.
    /// </summary>
    private string _WriteXml(IrWriteXml write)
    {
        _Use(RuntimeComponent.Xml, "rgl/standard/xml.hpp");
        _xmlWrite = true;
        _Type(write.Value.Type);
        var (value, path, root, text, error, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        return $"{{ const auto& {value} = {_Expression(write.Value)}; const auto& {path} = {_Expression(write.Path)}; const auto {root} = rgl_xml_from_node({value}); "
            + $"rgl::standard::mstring {text}; rgl::standard::xml::failure {error}; "
            + $"if (!rgl::standard::xml::serialize({root}, {text}, {error})) return {_XmlFailure(error, path, write.Span)}; "
            + $"const auto {status} = rgl::standard::xml::write_file({path}, {text}); "
            + $"if (!{status}.ok) return {_IoFailure($"{status}.io_status", path, write.Span)}; }}";
    }

    /// <summary>L'échec du guard courant avec une <c>ParseError</c> : le message de l'analyse, et le fichier concerné.</summary>
    private string _XmlFailure(string failure, string path, SourceSpan span) =>
        _StandardFailure(_parseErrorMold, $"{failure}.message", span, "400u", path);
}
