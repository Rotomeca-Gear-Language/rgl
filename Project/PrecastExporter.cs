using System.Text.Json;
using System.Text.Json.Nodes;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Types;

namespace Rgl.Project;

/// <summary>
/// L'export JSON des precasts (et des constantes de données : datasets, <c>import json</c>) pour les outils : chaque constante avec son fichier, son type
/// et sa valeur, connue à la compilation. Un mold est un objet (ses champs dans l'ordre du mold), un tableau ou un tuple un tableau, un dictionnaire un objet
/// quand ses clés sont des textes et une liste de paires sinon, une variante d'enum son nom, un symbole son nom, une durée ses millisecondes, un ingot sa base.
/// </summary>
internal static class PrecastExporter
{
    /// <summary>La version du format écrit : un outil la lit avant le reste.</summary>
    public const int FormatVersion = 1;

    /// <summary>Le JSON de <paramref name="precasts"/> et les noms de ceux dont la valeur n'a pas pu s'exprimer (ils sont écrits avec une valeur <c>null</c>).</summary>
    public static (string Json, IReadOnlyList<string> Skipped) Export(string project, string root, IReadOnlyList<IrVariableDeclaration> precasts)
    {
        var skipped = new List<string>();
        var items = new JsonArray();
        foreach (var precast in precasts)
        {
            var value = _Node(precast.Value, out var ok);
            if (!ok) skipped.Add(precast.Variable.Name);
            var path = precast.Variable.Declaration.Source.Path;
            items.Add(new JsonObject
            {
                ["name"] = precast.Variable.Name,
                ["file"] = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                ["type"] = precast.Variable.Type.ToString(),
                ["value"] = ok ? value : null,
            });
        }
        var document = new JsonObject { ["formatVersion"] = FormatVersion, ["project"] = project, ["precasts"] = items };
        return (document.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", skipped);
    }

    /// <summary>La valeur d'une expression de donnée ; <paramref name="ok"/> est faux si un morceau n'est pas une donnée connue.</summary>
    private static JsonNode? _Node(IrExpression expression, out bool ok)
    {
        ok = true;
        switch (expression)
        {
            case IrPart part:
                return _Node(part.Value, out ok);
            case IrMoldValue { Mold.Symbol: var mold } value when value.Fields.Count == mold.Fields.Count:
            {
                var fields = new JsonObject();
                for (var i = 0; i < mold.Fields.Count; i++)
                {
                    fields[mold.Fields[i].Name] = _Node(value.Fields[i], out var inner);
                    ok &= inner;
                }
                return fields;
            }
            case IrArrayValue array when array.Parts.All(p => !p.IsSpread):
                return _List(array.Parts.Select(p => p.Value), out ok);
            case IrTupleValue tuple:
                return _List(tuple.Elements, out ok);
            case IrDictionaryValue dictionary:
                return _Dictionary(dictionary, out ok);
            case IrEnumValue enumValue:
                return enumValue.Enum.Symbol.Variants[enumValue.Index].Name;
        }

        // Le reste (conversions, constantes nommées, calculs sur des constantes) se replie sur la valeur connue à la compilation.
        if (ConstantFolder.Fold(expression) is { } folded) return _Folded(folded, expression.Type, out ok);
        ok = false;
        return null;
    }

    private static JsonArray _List(IEnumerable<IrExpression> items, out bool ok)
    {
        ok = true;
        var list = new JsonArray();
        foreach (var item in items)
        {
            list.Add(_Node(item, out var inner));
            ok &= inner;
        }
        return list;
    }

    private static JsonNode _Dictionary(IrDictionaryValue dictionary, out bool ok)
    {
        ok = true;
        var textKeys = dictionary.Entries.All(e => e.Key is IrStringConstant);
        if (textKeys)
        {
            var map = new JsonObject();
            foreach (var (key, value) in dictionary.Entries)
            {
                map[((IrStringConstant)key).Value] = _Node(value, out var inner);
                ok &= inner;
            }
            return map;
        }
        var pairs = new JsonArray();
        foreach (var (key, value) in dictionary.Entries)
        {
            pairs.Add(new JsonArray(_Node(key, out var keyOk), _Node(value, out var valueOk)));
            ok &= keyOk && valueOk;
        }
        return pairs;
    }

    /// <summary>Une valeur repliée par <see cref="ConstantFolder"/>, avec le type qui donne leur nom aux champs d'un mold.</summary>
    private static JsonNode? _Folded(object value, IrType type, out bool ok)
    {
        ok = true;
        switch (value)
        {
            case long integer: return integer;
            case double real when double.IsFinite(real): return real;
            case bool flag: return flag;
            case string text: return text;
            case ConstantFolder.SymbolValue symbol: return symbol.Name;
            case ConstantFolder.DurationValue duration: return duration.Milliseconds;
            case ConstantFolder.NullValue: return null;
            case ConstantFolder.EnumValue enumValue: return enumValue.Enum.Variants[enumValue.Index].Name;
            case ConstantFolder.ArrayValue array: return _FoldedList(array.Items, (type as ArrayType)?.Element, out ok);
            case ConstantFolder.TupleValue tuple: return _FoldedList(tuple.Items, null, out ok, (type as TupleType)?.Elements);
            case ConstantFolder.MoldValue mold when type is MoldType { Symbol: var symbol } && symbol.Fields.Count == mold.Fields.Count:
            {
                var fields = new JsonObject();
                for (var i = 0; i < mold.Fields.Count; i++)
                {
                    fields[symbol.Fields[i].Name] = _Folded(mold.Fields[i], symbol.Fields[i].Type, out var inner);
                    ok &= inner;
                }
                return fields;
            }
            default:
                ok = false;
                return null;
        }
    }

    private static JsonArray _FoldedList(IReadOnlyList<object> items, IrType? element, out bool ok, IReadOnlyList<IrType>? tupleTypes = null)
    {
        ok = true;
        var list = new JsonArray();
        for (var i = 0; i < items.Count; i++)
        {
            list.Add(_Folded(items[i], tupleTypes is not null && i < tupleTypes.Count ? tupleTypes[i] : element ?? PrimitiveType.Bool, out var inner));
            ok &= inner;
        }
        return list;
    }
}
