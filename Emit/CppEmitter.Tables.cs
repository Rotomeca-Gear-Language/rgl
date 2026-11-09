using Rgl.IR;
using System.Text;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les grosses constantes de fichier : un tableau ou un dictionnaire de plusieurs dizaines d'éléments, tous connus à la compilation, s'écrit comme une table
/// <c>constexpr</c> de données (dans l'exécutable, rien à construire) que le programme recopie une fois au démarrage dans son tableau ou son
/// dictionnaire. Écrire les éléments un à un dans un <c>initializer_list</c> faisait générer à Clang une construction par élément : plusieurs
/// dizaines de secondes de compilation et un gros exécutable pour quelques milliers d'entrées.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Le nombre d'éléments à partir duquel une constante s'écrit comme une table.</summary>
    private const int TableThreshold = 64;

    /// <summary>
    /// Écrit la constante comme une table si elle s'y prête : retourne vrai quand c'est fait. Seul le premier niveau du fichier est concerné
    /// (<paramref name="indent"/> vide), pour une constante dont la valeur n'est pas déjà <c>constexpr</c>.
    /// </summary>
    private bool _TryTable(StringBuilder output, IrVariableDeclaration declaration, string indent)
    {
        var variable = declaration.Variable;
        if (indent.Length != 0 || !variable.IsConstant || variable.IsCompileTime) return false;

        switch (declaration.Value)
        {
            case IrArrayValue { Array.Kind: ArrayKind.Dynamic } array
                when array.Parts.Count >= TableThreshold && array.Parts.All(p => !p.IsSpread && ConstantFolder.Fold(p.Value) is not null):
            {
                var element = _Type(array.Array.Element);
                var table = $"rgl_d{++_temporaryCount}";
                _Line(output, declaration.Span);
                output.Append("[[maybe_unused]] static constexpr rgl::standard::fixed_array<").Append(element).Append(", ").Append(array.Parts.Count).Append("> ").Append(table)
                    .Append(" = rgl::standard::fixed_array<").Append(element).Append(", ").Append(array.Parts.Count).Append(">{")
                    .AppendJoin(", ", array.Parts.Select(p => _Expression(p.Value))).Append("};\n");
                output.Append("[[maybe_unused]] static const ").Append(_Type(variable.Type)).Append(' ').Append(_Declare(variable)).Append(" = ")
                    .Append(_Type(variable.Type)).Append("::from(").Append(table).Append(", ").Append(_Where(declaration.Span)).Append(");\n");
                return true;
            }

            case IrDictionaryValue dictionary
                when dictionary.Entries.Count >= TableThreshold && dictionary.Dictionary.Key is PrimitiveType { IsInteger: true } or PrimitiveType { Kind: PrimitiveKind.String }:
                return _TryDictionaryTable(output, declaration, dictionary);

            default:
                return false;
        }
    }

    /// <summary>
    /// Un dictionnaire dont les clés et les valeurs sont connues : la table est rangée par clé par le compilateur (comme le dictionnaire du runtime : entiers par
    /// valeur, textes par octets UTF-8), le programme n'a plus qu'à la recopier. Les clés en double sont déjà refusées par le binder.
    /// </summary>
    private bool _TryDictionaryTable(StringBuilder output, IrVariableDeclaration declaration, IrDictionaryValue dictionary)
    {
        var entries = new List<(object Key, IrExpression KeyExpression, IrExpression Value)>();
        foreach (var (key, value) in dictionary.Entries)
        {
            if (ConstantFolder.Fold(key) is not { } folded || ConstantFolder.Fold(value) is null) return false;
            entries.Add((folded, key, value));
        }

        var comparable = dictionary.Dictionary.Key is PrimitiveType { Kind: PrimitiveKind.String }
            ? entries.All(e => e.Key is string)
            : entries.All(e => e.Key is long or ulong);
        if (!comparable) return false;

        entries.Sort((left, right) => _CompareKeys(left.Key, right.Key));
        for (var i = 1; i < entries.Count; i++)
        {
            if (_CompareKeys(entries[i - 1].Key, entries[i].Key) == 0) return false;
        }

        var (keyType, valueType) = (_Type(dictionary.Dictionary.Key), _Type(dictionary.Dictionary.Value));
        var table = $"rgl_d{++_temporaryCount}";
        _Line(output, declaration.Span);
        output.Append("[[maybe_unused]] static constexpr std::pair<").Append(keyType).Append(", ").Append(valueType).Append("> ").Append(table).Append('[').Append(entries.Count).Append("] = {")
            .AppendJoin(", ", entries.Select(e => $"{{{_Expression(e.KeyExpression)}, {_Expression(e.Value)}}}")).Append("};\n");
        output.Append("[[maybe_unused]] static const ").Append(_Type(declaration.Variable.Type)).Append(' ').Append(_Declare(declaration.Variable)).Append(" = ")
            .Append(_Type(declaration.Variable.Type)).Append("::from_sorted(").Append(table).Append(", ").Append(entries.Count).Append(");\n");
        return true;
    }

    /// <summary>L'ordre des clés du dictionnaire du runtime : les entiers par valeur, les textes octet par octet en UTF-8.</summary>
    private static int _CompareKeys(object left, object right) => (left, right) switch
    {
        (string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)),
        (long a, long b) => a.CompareTo(b),
        (ulong a, ulong b) => a.CompareTo(b),
        (long a, ulong b) => a < 0 ? -1 : ((ulong)a).CompareTo(b),
        (ulong a, long b) => b < 0 ? 1 : a.CompareTo((ulong)b),
        _ => 0,
    };
}
