using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les tableaux de molds rangés en colonnes (SoA) : <c>rgl::standard::soa_array&lt;Layout&gt;</c>, avec un
/// Layout généré par mold. Tout <c>[mold]</c> dynamique l'est, sauf un mold qui se contient lui-même
/// à travers un tableau (le C++ ne pourrait pas définir ses colonnes) : celui-là reste un tableau de
/// structs. Les sous-molds sont aplatis : un champ <c>Vec2 position</c> donne deux colonnes,
/// <c>position.x</c> et <c>position.y</c>. Les colonnes chaudes (lues dans une étape d'un parcours,
/// gears appelés compris) viennent d'abord, puis les froides ; à chaleur égale, les plus alignées d'abord.
/// Un mold dont toutes les colonnes sont chaudes reste un tableau de structs : les parcours lisent alors
/// chaque champ, chaque ligne de cache chargée sert en entier, et les colonnes ne coûteraient que la
/// reconstruction de chaque élément.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Une colonne d'un Layout : le chemin du champ dans le mold, son type et son accès dans un struct.</summary>
    private sealed record SoaColumn(string Path, IrType Type, string Access, string Name);

    /// <summary>Les molds dont un tableau est rangé en colonnes dans le code produit : leur Layout est écrit.</summary>
    private readonly HashSet<MoldSymbol> _soaMolds = [];


    /// <summary>Les molds qui se contiennent eux-mêmes à travers un tableau, déjà calculés.</summary>
    private readonly Dictionary<MoldSymbol, bool> _selfContaining = [];

    /// <summary>
    /// Indique si ce tableau est rangé en colonnes : un <c>[mold]</c> dynamique dont le mold ne se contient
    /// pas, et dont un champ au moins reste froid (aucun parcours ne le lit).
    /// </summary>
    private bool _IsSoa(ArrayType array) =>
        !array.Packed && array.Kind is ArrayKind.Dynamic or ArrayKind.Fixed && _Representation(array.Element) is MoldType { Symbol: var mold } && !_SelfContaining(mold) && !_AllHot(mold);

    /// <summary>Les molds dont toutes les colonnes sont chaudes, déjà calculés.</summary>
    private readonly Dictionary<MoldSymbol, bool> _allHot = [];

    /// <summary>Indique si toutes les colonnes de <paramref name="mold"/> sont lues par les parcours du programme.</summary>
    private bool _AllHot(MoldSymbol mold)
    {
        if (_allHot.TryGetValue(mold, out var known)) return known;
        return _allHot[mold] = _fieldHeat.Keys.Any(h => h.Mold == mold) && _ColumnPaths(mold).All(path => _HeatOf(mold, path) == ColumnHeat.Hot);
    }

    /// <summary>Le mold des éléments d'un tableau rangé en colonnes.</summary>
    private static MoldSymbol _SoaMold(ArrayType array) => ((MoldType)_Representation(array.Element)).Symbol;

    /// <summary>Le nom C++ du Layout d'un mold : <c>rgl_l_m_Entity</c>, <c>rgl_l_a_1</c>.</summary>
    private string _LayoutName(MoldSymbol mold) => "rgl_l_" + _moldNames[mold]["rgl_".Length..];

    /// <summary>Indique si <paramref name="mold"/> se retrouve dans ses propres champs, à travers d'autres molds ou des tableaux.</summary>
    private bool _SelfContaining(MoldSymbol mold)
    {
        if (_selfContaining.TryGetValue(mold, out var known)) return known;
        _selfContaining[mold] = false;

        var seen = new HashSet<MoldSymbol>();
        var pending = new Stack<MoldSymbol>(mold.Fields.SelectMany(f => _ContainedMolds(f.Type)));
        var found = false;
        while (pending.Count > 0 && !found)
        {
            var current = pending.Pop();
            if (current == mold) found = true;
            else if (seen.Add(current)) foreach (var inner in current.Fields.SelectMany(f => _ContainedMolds(f.Type))) pending.Push(inner);
        }
        return _selfContaining[mold] = found;
    }

    /// <summary>
    /// Les colonnes d'un mold, sous-molds aplatis : les chaudes d'abord, puis par alignement
    /// décroissant, puis dans l'ordre du mold.
    /// </summary>
    private List<SoaColumn> _Columns(MoldSymbol mold)
    {
        var columns = _FlatColumns(mold).Where(c => !_IsDropped(mold, c.Path, c.Type));

        return [.. columns
            .Select((column, position) => (Column: column, Position: position))
            .OrderByDescending(c => _HeatOf(mold, c.Column.Path) ?? ColumnHeat.Cold)
            .ThenByDescending(c => _Alignment(c.Column.Type))
            .ThenBy(c => c.Position)
            .Select((c, i) => new SoaColumn(c.Column.Path, c.Column.Type, c.Column.Access, $"rgl_k{i}"))];
    }

    /// <summary>Les chemins des colonnes d'un mold, sous-molds aplatis.</summary>
    private IEnumerable<string> _ColumnPaths(MoldSymbol mold) => _FlatColumns(mold).Select(c => c.Path);

    /// <summary>Les colonnes d'un mold dans l'ordre de ses champs, sous-molds aplatis : chemin, type et accès dans un struct.</summary>
    private List<(string Path, IrType Type, string Access)> _FlatColumns(MoldSymbol mold)
    {
        var columns = new List<(string Path, IrType Type, string Access)>();

        void Flatten(MoldSymbol current, string path, string access)
        {
            foreach (var field in current.Fields)
            {
                var fieldPath = path + field.Name;
                var fieldAccess = $"{access}.rgl_f_{field.Name}";
                if (_Representation(field.Type) is MoldType { Symbol: var sub } && sub != mold && !_SelfContaining(sub)) Flatten(sub, fieldPath + ".", fieldAccess);
                else columns.Add((fieldPath, field.Type, fieldAccess));
            }
        }
        Flatten(mold, "", "v");
        return columns;
    }

    /// <summary>L'alignement d'une valeur de ce type en C++, en octets, pour ranger les colonnes.</summary>
    private static int _Alignment(IrType type) => _Representation(type) switch
    {
        PrimitiveType { Kind: PrimitiveKind.Long or PrimitiveKind.ULong or PrimitiveKind.Double } => 8,
        PrimitiveType { Kind: PrimitiveKind.Int or PrimitiveKind.UInt or PrimitiveKind.Float } => 4,
        PrimitiveType { Kind: PrimitiveKind.Short or PrimitiveKind.UShort } => 2,
        PrimitiveType { Kind: PrimitiveKind.Byte or PrimitiveKind.UByte or PrimitiveKind.Bool } => 1,
        EnumType enumeration => _Alignment(enumeration.Symbol.Underlying),
        // Un mold, un tuple ou une union a l'alignement de son membre le plus aligné ; un nullable (std::optional) et un [T;N] celui de leur élément.
        MoldType mold => mold.Symbol.Fields.Count == 0 ? 1 : mold.Symbol.Fields.Max(f => _Alignment(f.Type)),
        TupleType tuple => tuple.Elements.Max(_Alignment),
        UnionType union => union.Members.Max(_Alignment),
        NullableType nullable => _Alignment(nullable.Inner),
        ArrayType { Kind: ArrayKind.Fixed } fixedArray => _Alignment(fixedArray.Element),
        _ => 8,
    };

    /// <summary>
    /// Le Layout d'un mold : ses colonnes, et comment reconstruire un élément (gather), le répartir à la
    /// fin (scatter) ou à sa place (store, pour une variable mut) et réserver la place.

    /// </summary>
    private string _Layout(MoldSymbol mold)
    {
        var columns = _Columns(mold);
        var byPath = columns.ToDictionary(c => c.Path, c => c.Name);
        var name = _moldNames[mold];

        // Une colonne supprimée (jamais lue) se reconstruit à vide ; un sous-mold aplati se rassemble champ par champ.
        var dropped = _FlatColumns(mold).Where(c => !byPath.ContainsKey(c.Path)).Select(c => c.Path).ToHashSet();

        string Gather(MoldSymbol current, string path) =>
            $"{_moldNames[current]}{{{string.Join(", ", current.Fields.Select(f =>
                byPath.TryGetValue(path + f.Name, out var column) ? $"c.{column}[i]"
                : dropped.Contains(path + f.Name) ? "{}"
                : Gather(((MoldType)_Representation(f.Type)).Symbol, path + f.Name + ".")))}}}";

        var output = new StringBuilder();
        output.Append("struct ").Append(_LayoutName(mold)).Append(" {\n");
        output.Append(Indent).Append("using value_type = ").Append(name).Append(";\n");
        output.Append(Indent).Append("struct columns {\n");
        foreach (var column in columns)
            output.Append(Indent).Append(Indent).Append("rgl::standard::array<").Append(_Type(column.Type)).Append("> ").Append(column.Name).Append(";   // ").Append(column.Path).Append('\n');
        output.Append(Indent).Append("};\n");
        // Les mêmes colonnes pour un [mold;N] : rangées dans l'objet, N éléments chacune (bloc d'AoSoA).
        output.Append(Indent).Append("template <std::size_t N> struct fixed_columns {\n");
        foreach (var column in columns)
            output.Append(Indent).Append(Indent).Append(_Type(column.Type)).Append(' ').Append(column.Name).Append("[N];   // ").Append(column.Path).Append('\n');
        output.Append(Indent).Append("};\n");
        // gather et store lisent et écrivent les colonnes d'un tableau dynamique comme celles d'un tableau fixe.
        output.Append(Indent).Append("template <class C> static constexpr value_type gather([[maybe_unused]] const C& c, [[maybe_unused]] std::size_t i) { return ")
            .Append(Gather(mold, "")).Append("; }\n");
        output.Append(Indent).Append("static void scatter([[maybe_unused]] columns& c, [[maybe_unused]] const value_type& v) {");
        foreach (var column in columns) output.Append(" c.").Append(column.Name).Append(".push(").Append(column.Access).Append(");");
        output.Append(" }\n");
        output.Append(Indent).Append("template <class C> static constexpr void store([[maybe_unused]] C& c, [[maybe_unused]] std::size_t i, [[maybe_unused]] const value_type& v) {");
        foreach (var column in columns) output.Append(" c.").Append(column.Name).Append("[i] = ").Append(column.Access).Append(';');
        output.Append(" }\n");
        output.Append(Indent).Append("static void reserve([[maybe_unused]] columns& c, [[maybe_unused]] std::size_t n) {");
        foreach (var column in columns) output.Append(" c.").Append(column.Name).Append(".reserve(n);");
        output.Append(" }\n");
        output.Append("};\n\n");
        return output.ToString();
    }

    /// <summary>Les molds dont un tableau rangé en colonnes apparaît dans ce type : leurs Layouts doivent précéder.</summary>
    private IEnumerable<MoldSymbol> _SoaMoldsIn(IrType type) => _Representation(type) switch
    {
        ArrayType array when _IsSoa(array) => [_SoaMold(array)],
        ArrayType array => _SoaMoldsIn(array.Element),
        NullableType nullable => _SoaMoldsIn(nullable.Inner),
        UnionType union => union.Members.SelectMany(_SoaMoldsIn),
        ResultType result => [.. _SoaMoldsIn(result.Value), .. _SoaMoldsIn(result.Error)],
        DictionaryType dictionary => [.. _SoaMoldsIn(dictionary.Key), .. _SoaMoldsIn(dictionary.Value)],
        TupleType tuple => tuple.Elements.SelectMany(_SoaMoldsIn),
        FunctionType function => [.. function.Parameters.SelectMany(_SoaMoldsIn), .. _SoaMoldsIn(function.Return)],
        _ => [],
    };
}
