using Rgl.IR.Expressions;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les collections : tableaux (<c>rgl::standard::array</c>, <c>fixed_array</c>, <c>bounded_array</c>,
/// <c>non_empty_array</c>), dictionnaires (<c>rgl::standard::dictionary</c>, un <c>std::flat_map</c>)
/// et tuples (<c>rgl::standard::tuple</c>). Une valeur écrite sans spread est une liste entre
/// accolades, évaluée de gauche à droite ; avec un spread, <c>make_array</c>. Une lecture contrôlée
/// arrête le programme (<c>at(i, where)</c>), ou, dans un guard, le fait échouer.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Un tableau écrit : <c>T{a, b}</c>, ou <c>make_array&lt;E&gt;(spread(a), x)</c> avec un spread.</summary>
    private string _ArrayPack(IrArrayPack pack, Func<IrExpression, string> operand)
    {
        _includes.Add("rgl/standard/array_pack.hpp");
        return $"rgl::standard::pack_array<{_Type(pack.Array)}>({operand(pack.Value)})";
    }

    private string _ArrayValue(IrArrayValue array, Func<IrExpression, string> operand)
    {
        var type = _Type(array.Array);
        if (array.HasKnownCount) return $"{type}{{{string.Join(", ", array.Parts.Select(p => operand(p.Value)))}}}";

        // Un tableau étalé en tête et plus lu ensuite : repris tel quel, les autres morceaux s'ajoutent à sa suite (linéaire dans une boucle).
        if (array.Array.Kind == ArrayKind.Dynamic && !_IsSoa(array.Array) && array.Parts is [{ IsSpread: true } head, ..]
            && _spreadMoves.Contains(head.Value) && head.Value.Type is ArrayType { Kind: ArrayKind.Dynamic } source && source.Element.Equals(array.Array.Element) && !_IsSoa(source))
        {
            _Note(array.Span, $"[...a, …] reprend le bloc du tableau étalé : il n'est plus recopié");
            return $"rgl::standard::make_array_from<{_Type(array.Array.Element)}>({string.Join(", ", array.Parts.Select((p, index) => index == 0 ? $"std::move({operand(p.Value)})" : p.IsSpread ? $"rgl::standard::spread({operand(p.Value)})" : operand(p.Value)))})";
        }

        var parts = array.Parts.Select(p => p.IsSpread ? $"rgl::standard::spread({operand(p.Value)})" : operand(p.Value));
        if (array.Array.Kind == ArrayKind.Dynamic && _IsSoa(array.Array)) return $"rgl::standard::make_soa<{_LayoutName(_SoaMold(array.Array))}>({string.Join(", ", parts)})";
        return $"rgl::standard::make_array<{_Type(array.Array.Element)}>({string.Join(", ", parts)})";
    }

    /// <summary>Un dictionnaire écrit : <c>D{{k1, v1}, {k2, v2}}</c>.</summary>
    private string _DictionaryValue(IrDictionaryValue dictionary, Func<IrExpression, string> operand) =>
        $"{_Type(dictionary.Dictionary)}{{{string.Join(", ", dictionary.Entries.Select(e => $"{{{operand(e.Key)}, {operand(e.Value)}}}"))}}}";

    /// <summary>Un tuple écrit : <c>rgl::standard::tuple&lt;…&gt;{a, b}</c>.</summary>
    private string _TupleValue(IrTupleValue tuple, Func<IrExpression, string> operand) =>
        $"{_Type(tuple.Tuple)}{{{string.Join(", ", tuple.Elements.Select(operand))}}}";

    /// <summary>
    /// <c>a[i]</c> ou <c>d[k]</c> : direct quand l'index est sûr, <c>at(i, where)</c> sinon (arrêt), ou,
    /// dans un guard, un test qui le fait échouer (« index hors limites », « clé absente »).
    /// </summary>
    private string _Index(IrIndex index, Func<IrExpression, string> operand)
    {
        var (target, position) = (operand(index.Target), operand(index.Index));
        if (!index.Checked && _restrictColumns.TryGetValue(index.Target, out var restricted)) return $"{restricted}[{position}]";
        if (!index.Checked) return $"({target})[{position}]";
        if (!_InGuard) return $"({target}).at({position}, {_Where(index.Span)})";

        _usesStatementExpressions = true;
        var (collection, key) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        var isDictionary = index.Target.Type is DictionaryType;
        var test = isDictionary ? $"{collection}.contains({key})" : $"{collection}.has_index({key})";
        var message = _String(new IrStringConstant(index.Span, PrimitiveType.String, isDictionary ? "clé absente du dictionnaire" : "index hors limites"));
        var read = isDictionary ? $"{collection}[{key}]" : $"{collection}.at({key}, {_Where(index.Span)})";
        return $"({{ const auto& {collection} = {target}; const auto {key} = {position}; if (!{test}) return {_StandardFailure(_exceptionMold, message, index.Span)}; {read}; }})";
    }

    /// <summary>
    /// <c>nums.push(v)</c>, sur une variable ou une place du contenu d'une variable <c>mut</c> : un
    /// <c>[T;N..M]</c> plein arrête le programme, ou fait échouer le guard.
    /// </summary>
    private string _Push(IrPush push, Func<IrExpression, string> operand) => _OnPlace(push.Target, variable =>
    {
        var value = push.Value is IrVariableReference reference && _moves.Contains(reference) ? _Consumed(reference) : operand(push.Value);

        if (_Representation(push.Target.Type) is not ArrayType { Kind: ArrayKind.Bounded }) return $"{variable}.push({value})";
        if (!_InGuard) return $"{variable}.push({value}, {_Where(push.Span)})";

        _usesStatementExpressions = true;
        var message = _String(new IrStringConstant(push.Span, PrimitiveType.String, "ajout dans un tableau borné déjà plein"));
        return $"({{ if (!{variable}.can_push()) return {_StandardFailure(_exceptionMold, message, push.Span)}; {variable}.push({value}, {_Where(push.Span)}); }})";
    });

    /// <summary><c>ages.add(k, v)</c> : une clé déjà présente arrête le programme, ou fait échouer le guard.</summary>
    private string _Add(IrAdd add, Func<IrExpression, string> operand) => _OnPlace(add.Target, variable =>
    {
        if (!_InGuard) return $"{variable}.add({operand(add.Key)}, {operand(add.Value)}, {_Where(add.Span)})";

        _usesStatementExpressions = true;
        var key = $"rgl_t{++_temporaryCount}";
        var message = _String(new IrStringConstant(add.Span, PrimitiveType.String, "clé déjà présente dans le dictionnaire"));
        return $"({{ const auto {key} = {operand(add.Key)}; if (!{variable}.can_add({key})) return {_StandardFailure(_exceptionMold, message, add.Span)}; "
            + $"{variable}.add({key}, {operand(add.Value)}, {_Where(add.Span)}); }})";
    });

    /// <summary>
    /// Un tableau d'une sorte vers une autre : <c>T::from(a, where)</c> (qui arrête si la taille ne convient
    /// pas), <c>T::try_from(a)</c> pour <c>as?</c>, et dans un guard un test <c>T::fits</c> qui le fait échouer.
    /// </summary>
    private string _ArrayConvert(IrArrayConvert convert, Func<IrExpression, string> operand)
    {
        var target = _Type(convert.Target);
        var value = operand(convert.Value);
        if (convert.Mode == ArrayConversion.Optional)
        {
            _Type(convert.Type);   // l'en-tête des nullables
            return $"{target}::try_from({value})";
        }
        if (convert.Mode == ArrayConversion.Safe || !_InGuard) return $"{target}::from({value}, {_Where(convert.Span)})";

        _usesStatementExpressions = true;
        var items = $"rgl_t{++_temporaryCount}";
        var message = _String(new IrStringConstant(convert.Span, PrimitiveType.String, $"la taille du tableau ne convient pas à {convert.Target}"));
        return $"({{ const auto& {items} = {value}; if (!{target}::fits({items}.size())) return {_StandardFailure(_castErrorMold, message, convert.Span)}; "
            + $"{target}::from({items}, {_Where(convert.Span)}); }})";
    }

    /// <summary>
    /// Indique si une valeur de ce type a une valeur vide en C++ (<c>T()</c>) : pas un <c>[T;N]</c>, un
    /// <c>[T;N..M]</c> qui doit avoir des éléments, un <c>[T+]</c>, ni ce qui en contient un.
    /// </summary>
    private static bool _HasEmptyValue(IrType type) => type switch
    {
        ArrayType { Kind: ArrayKind.Fixed or ArrayKind.NonEmpty } or FunctionType => false,
        ArrayType { Kind: ArrayKind.Bounded } bounded => bounded.Size == 0,
        MoldType mold => mold.Symbol.Fields.All(f => _HasEmptyValue(f.Type)),
        TupleType tuple => tuple.Elements.All(_HasEmptyValue),
        UnionType union => _HasEmptyValue(union.Members[0]),
        ResultType result => _HasEmptyValue(result.Value),
        IngotType ingot => _HasEmptyValue(ingot.Base),
        _ => true,
    };

    /// <summary>
    /// La vue d'un intervalle, sans tableau : <c>rgl::standard::range&lt;T&gt;(début, fin, pas, exclue, "main.rgl:3:5")</c>
    /// (range.hpp) ; un pas nul à l'exécution arrête le programme à cet endroit.
    /// </summary>
    private string _RangeView(IrRange range)
    {
        _includes.Add("rgl/standard/range.hpp");
        return $"rgl::standard::range<{_Type(range.Array.Element)}>({_Expression(range.Start)}, {_Expression(range.End)}, {_Expression(range.Step)}, "
            + $"{(range.IsExclusive ? "true" : "false")}, {_Where(range.Span)})";
    }

    /// <summary>Un intervalle en valeur : le tableau de son type construit depuis sa vue (<c>T::from(vue, where)</c>).</summary>
    private string _RangeValue(IrRange range) => $"{_Type(range.Array)}::from({_RangeView(range)}, {_Where(range.Span)})";

    /// <summary>Les molds que ce type contient, de près ou de loin : leurs structs doivent être écrits avant.</summary>
    private static IEnumerable<IR.Symbols.MoldSymbol> _ContainedMolds(IrType type) => type switch
    {
        MoldType mold => [mold.Symbol],
        NullableType nullable => _ContainedMolds(nullable.Inner),
        IngotType ingot => _ContainedMolds(ingot.Base),
        UnionType union => union.Members.SelectMany(_ContainedMolds),
        ResultType result => [.. _ContainedMolds(result.Value), .. _ContainedMolds(result.Error)],
        ArrayType array => _ContainedMolds(array.Element),
        DictionaryType dictionary => [.. _ContainedMolds(dictionary.Key), .. _ContainedMolds(dictionary.Value)],
        TupleType tuple => tuple.Elements.SelectMany(_ContainedMolds),
        FunctionType function => [.. function.Parameters.SelectMany(_ContainedMolds), .. _ContainedMolds(function.Return)],
        _ => [],
    };

    /// <summary><c>Moves.find(nom)</c> : <c>rgl::standard::dataset_find</c> cherche le nom dans la constante des noms.</summary>
    private string _DatasetFind(IrDatasetFind find, Func<IrExpression, string> operand)
    {
        _includes.Add("rgl/standard/dataset.hpp");
        _Use(RuntimeComponent.String, "rgl/standard/string.hpp");
        return $"rgl::standard::dataset_find({operand(find.Names)}, {operand(find.Text)})";
    }
}
