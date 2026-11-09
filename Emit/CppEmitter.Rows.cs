using Rgl.IR.Expressions;
using Rgl.Text;

namespace Rgl.Emit;

internal sealed partial class CppEmitter
{
    /// <summary>Le drapeau de débordement de la boucle à vérification groupée en cours d'écriture ; <see langword="null"/> hors d'elle.</summary>
    private string? _groupedOverflow;

    /// <summary>Vrai quand une opération de la boucle à vérification groupée en cours a utilisé son drapeau.</summary>
    private bool _groupedUsed;

    /// <summary>Les colonnes que le parcours de lignes en cours lit par un pointeur <c>__restrict</c>, et le nom de ce pointeur.</summary>
    private readonly Dictionary<IrExpression, string> _restrictColumns = new(ReferenceEqualityComparer.Instance);

    /// <summary>Les noms des fichiers dont une position compacte a été écrite, dans l'ordre de leur numéro (6 bits : au plus 63).</summary>
    private readonly List<string> _positionFiles = [];

    /// <summary>
    /// L'endroit d'un élément dans le source RGL, en constante de 32 bits (<c>rgl::standard::position</c>) : le fichier (6 bits), la ligne (18) et la
    /// colonne (8), chacun ramené au maximum de son champ. C'est un immédiat dans le code, là où le texte <c>"main.rgl:44:19"</c> coûte une adresse et
    /// ses octets à chaque opération ; le nom du fichier se retrouve par <c>position_files</c>, que le programme définit.
    /// </summary>
    private string _Position(SourceSpan span)
    {
        _includes.Add("rgl/standard/position.hpp");
        var position = span.StartPosition;
        var name = Path.GetFileName(span.Source.Path);
        var file = _positionFiles.IndexOf(name);
        if (file < 0)
        {
            file = _positionFiles.Count;
            _positionFiles.Add(name);
        }
        var packed = ((uint)Math.Min(file, 63) << 26) | (Math.Min(span.Source.OriginalLine(position.Line).Value, 0x3ffffu) << 8) | Math.Min(position.Column.Value, 255u);
        return $"{packed}u";
    }

    /// <summary><c>|S|&gt;</c> et <c>||S|&gt;</c> : le nombre de lignes, tiré de la taille de chaque colonne.</summary>
    private string _RowCount(IrRowCount rows, Func<IrExpression, string> operand)
    {
        _includes.Add("rgl/standard/rows.hpp");
        var sizes = string.Join(", ", rows.Columns.Select(c => $"({operand(c)}).size()"));
        return $"rgl::standard::row_count({(rows.Strict ? "true" : "false")}, {_Where(rows.Span)}, {sizes})";
    }
}
