using Rgl.AST;
using Rgl.AST.InputOutput;
using Rgl.AST.Interfaces;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Types;
using Rgl.Text;
using AstTypes = Rgl.AST.Types;

namespace Rgl.Binding;

/// <summary>
/// Le format <c>xml</c> (slice 5.1e). <c>XmlNode n &lt;- xml "a.xml";</c> lit un document en son élément racine, un <c>XmlNode</c> de la std
/// (nom de la balise, attributs, éléments fils, texte), et <c>n -&gt; xml "b.xml";</c> l'écrit. <c>xml&lt;train&gt;</c> passe par un gear ou un guard :
/// en lecture il reçoit le <c>XmlNode</c> et renvoie ce qu'on veut lire, en écriture il reçoit la valeur et renvoie un <c>XmlNode</c> — comme
/// <c>custom</c>, un appel ordinaire du train. Comme tout fichier, dans un guard seulement ; un fichier absent est une <c>IoError</c>, un XML invalide
/// ou un nom de balise qui n'en est pas un une <c>ParseError</c>. Le sous-ensemble lu est celui de <c>runtime/include/rgl/standard/xml.hpp</c>.
/// </summary>
internal sealed partial class Binder
{
    /// <summary><c>&lt;- xml chemin</c> et <c>&lt;- xml&lt;train&gt; chemin</c>.</summary>
    private IrExpression? _BindXmlRead(ReadExpression read)
    {
        const string example = "XmlNode n <- xml \"a.xml\";";
        var path = _BindFilePath(read.Path, read.Source.Span, "xml", example);
        if (!_AllowInputOutput(read.Span, "lire un fichier") || !_RequireGuard(read.Span, "la lecture d'un fichier") || path is null) return null;
        if (_xmlNode is null)
        {
            _Unsupported(read.Source.Span, "xml sans la bibliothèque standard, qui définit XmlNode");
            return null;
        }

        var node = new IrReadXml(read.Span, path, new MoldType(_xmlNode));
        if (read.Custom is null) return node;

        return _XmlTrain(read.Custom, read.Source.Span) is { } train
            ? _BindCustomTrain(new CustomFormat(read.Custom.Span, read.Custom.Type, train), read.Source.Span, read.Span, node)
            : null;
    }

    /// <summary><c>noeud -&gt; xml chemin;</c> et <c>valeur -&gt; xml&lt;train&gt; chemin;</c>.</summary>
    private IrStatement? _BindXmlWrite(WriteStatement write, IrExpression? value)
    {
        var path = _BindFilePath(write.Path, write.Target.Span, "xml", "noeud -> xml \"a.xml\";");
        if (!_AllowInputOutput(write.Span, "écrire un fichier") || !_RequireGuard(write.Span, "l'écriture d'un fichier") || path is null || value is null) return null;
        if (_xmlNode is null)
        {
            _Unsupported(write.Target.Span, "xml sans la bibliothèque standard, qui définit XmlNode");
            return null;
        }

        if (write.Custom is { } custom)
        {
            var train = _XmlTrain(custom, write.Target.Span);
            value = train is null ? null : _BindCustomTrain(new CustomFormat(custom.Span, custom.Type, train), write.Target.Span, write.Span, value);
            if (value is null) return null;
        }

        var node = _ConvertTo(value, new MoldType(_xmlNode), "xml écrit un XmlNode");
        return node is null ? null : new IrWriteXml(write.Span, node, path);
    }

    /// <summary>Le nom du train écrit entre les chevrons de <c>xml&lt;train&gt;</c> (E030 si ce n'est pas un nom).</summary>
    private Identifier? _XmlTrain(CustomFormat format, SourceSpan span)
    {
        if (format.Train is { } given) return given;
        if (format.Type is AstTypes.NamedType { TypeArguments: null } named) return named.Name;

        _Error(span, DiagnosticCodes.ExpectedToken, "« xml » attend entre chevrons le nom d'un gear ou d'un guard : xml<train>", "train attendu",
            "par exemple : var config <- xml<lireConfig> \"a.xml\";");
        return null;
    }
}
