using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// La modification en place du contenu d'une variable <c>mut</c> : <c>ps[i].vie &lt;- 0</c>, <c>push</c> dans
/// un tableau d'un mold, un parcours qui modifie ses éléments. La cible devient une place C++ : une suite de
/// références (<c>auto&amp;</c>) prises de la variable vers l'élément, chaque index évalué une fois, de gauche
/// à droite, avant la valeur. Un index contrôlé arrête le programme (<c>at</c>, accès non const), ou, dans
/// un guard, le fait échouer.
/// </summary>
/// <remarks>
/// Un élément d'un tableau en colonnes (SoA) n'existe pas d'un tenant : un champ qui a sa colonne s'y écrit
/// directement (<c>columns().rgl_kN[i]</c>) ; un élément entier, ou un sous-mold aplati, est reconstruit
/// dans une variable, modifié, puis remis à sa place (<c>set</c>).
/// </remarks>
internal sealed partial class CppEmitter
{
    /// <summary>La valeur d'avant de la cible d'une affectation composée en cours d'écriture (<see cref="IrTargetValue"/>).</summary>
    private string? _targetValue;

    /// <summary>
    /// Une place C++ : les instructions qui la préparent (index rangés, références prises), le nom ou
    /// l'accès qui la désigne, et les instructions à écrire après la modification (remettre un élément
    /// reconstruit dans son tableau en colonnes), dans l'ordre où les écrire.
    /// </summary>
    private sealed record Place(List<string> Prelude, string Access, List<string> Epilogue);

    /// <summary>Le type C++ d'un paramètre : une référence modifiable pour un paramètre <c>mut</c>.</summary>
    private string _ParameterType(ParameterSymbol parameter) =>
        parameter.IsMutable ? $"{_Type(parameter.Type)}&" : _ParameterType(parameter.Type);

    /// <summary>La place désignée par <paramref name="target"/>, une suite d'accès qui part d'une variable, d'un paramètre ou de l'élément d'un parcours.</summary>
    private Place _Place(IrExpression target)
    {
        var place = new Place([], "", []);
        var access = _PlaceAccess(target, place);
        return place with { Access = access };
    }

    private string _PlaceAccess(IrExpression target, Place place)
    {
        switch (target)
        {
            case IrVariableReference reference:
                return _variableNames[reference.Variable];
            case IrParameterReference reference:
                return _Name(reference.Parameter);
            case IrElementValue:
                return _elementValue ?? throw new InvalidOperationException("Élément de parcours hors d'un parcours.");
            case IrRetype retype:
                return _PlaceAccess(retype.Value, place);
            case IrNonNull nonNull:
                return $"(*{_PlaceAccess(nonNull.Value, place)})";
            case IrTupleItem item:
                return $"rgl::standard::item<{item.Position}>({_PlaceAccess(item.Target, place)})";
            case IrMemberAccess member when _SoaElement(member, out var element, out var path):
                return _SoaPlace(element, path, place);
            case IrMemberAccess member:
                return _Field(_PlaceAccess(member.Target, place), member.Target.Type, member.Field);
            case IrIndex index when _Representation(index.Target.Type) is ArrayType array && _IsSoa(array):
                return _SoaPlace(index, [], place);
            case IrIndex index:
                var collection = _PlaceAccess(index.Target, place);
                if (!index.Checked) return $"({collection})[{_Expression(index.Index)}]";

                var key = _Hoist(place, $"const auto", _Expression(index.Index));
                var name = $"rgl_t{++_temporaryCount}";
                if (!_InGuard)
                {
                    place.Prelude.Add($"auto& {name} = ({collection}).at({key}, {_Where(index.Span)});");
                    return name;
                }

                var isArray = _Representation(index.Target.Type) is ArrayType;
                var test = isArray ? $"({collection}).has_index({key})" : $"({collection}).contains({key})";
                var message = _String(new IrStringConstant(index.Span, PrimitiveType.String, isArray ? "index hors limites" : "clé absente du dictionnaire"));
                place.Prelude.Add($"if (!{test}) return {_StandardFailure(_exceptionMold, message, index.Span)};");
                place.Prelude.Add($"auto& {name} = ({collection}){(isArray ? $".at({key}, {_Where(index.Span)})" : $"[{key}]")};");
                return name;
            default:
                throw new InvalidOperationException($"Place inattendue : {target.GetType().Name}. Le binder n'accepte qu'une suite d'accès depuis une variable.");
        }
    }

    /// <summary>Range une valeur dans une constante de la préparation de <paramref name="place"/>, et renvoie son nom.</summary>
    private string _Hoist(Place place, string declaration, string value)
    {
        var name = $"rgl_t{++_temporaryCount}";
        place.Prelude.Add($"{declaration} {name} = {value};");
        return name;
    }

    /// <summary>
    /// Vrai si <paramref name="member"/> lit un champ d'un élément d'un tableau en colonnes :
    /// <paramref name="element"/> est alors l'index de l'élément, et <paramref name="path"/> les champs lus depuis lui.
    /// </summary>
    private bool _SoaElement(IrMemberAccess member, out IrIndex element, out List<string> path)
    {
        path = [];
        IrExpression current = member;
        while (current is IrMemberAccess access)
        {
            path.Insert(0, access.Field.Name);
            current = access.Target;
        }
        element = (current as IrIndex)!;
        return current is IrIndex index && _Representation(index.Target.Type) is ArrayType array && _IsSoa(array);
    }

    /// <summary>
    /// Un champ (ou l'élément entier, <paramref name="path"/> vide) d'un élément d'un tableau en colonnes :
    /// sa colonne quand il en a une, sinon l'élément reconstruit dans une variable, remis à sa place après.
    /// </summary>
    private string _SoaPlace(IrIndex element, List<string> path, Place place)
    {
        var array = (ArrayType)_Representation(element.Target.Type);
        var collection = _PlaceAccess(element.Target, place);
        var key = _Hoist(place, "const auto", _Expression(element.Index));
        string position;
        if (!element.Checked) position = $"static_cast<std::size_t>({key})";
        else if (!_InGuard) position = _Hoist(place, "const std::size_t", $"({collection}).index({key}, {_Where(element.Span)})");
        else
        {
            var message = _String(new IrStringConstant(element.Span, PrimitiveType.String, "index hors limites"));
            place.Prelude.Add($"if (!({collection}).has_index({key})) return {_StandardFailure(_exceptionMold, message, element.Span)};");
            position = $"({collection}).index({key}, {_Where(element.Span)})";
        }

        // Le champ a sa colonne, ou se trouve dans une colonne (un mold non aplati) : il s'y écrit directement.
        var joined = string.Join('.', path);
        if (path.Count > 0 && _Columns(_SoaMold(array)).FirstOrDefault(c => joined == c.Path || joined.StartsWith(c.Path + ".", StringComparison.Ordinal)) is { } column)
        {
            var rest = joined.Length == column.Path.Length ? "" : string.Concat(joined[(column.Path.Length + 1)..].Split('.').Select(f => $".rgl_f_{f}"));
            var name = $"rgl_t{++_temporaryCount}";
            place.Prelude.Add($"auto& {name} = ({collection}).columns().{column.Name}[{position}];");
            return name + rest;
        }

        var copy = _Hoist(place, "auto", $"({collection})[{position}]");
        place.Epilogue.Insert(0, $"({collection}).set({position}, {copy});");
        return copy + string.Concat(path.Select(f => $".rgl_f_{f}"));
    }

    /// <summary>
    /// <c>cible &lt;- valeur</c> sur le contenu d'une variable <c>mut</c> : la place, puis la valeur (qui lit
    /// la valeur d'avant dans une affectation composée), puis ce qui remet un élément reconstruit à sa place.
    /// </summary>
    private void _ContentAssignment(StringBuilder output, IrContentAssignment assignment, string indent)
    {
        _Line(output, assignment.Span);
        var place = _Place(assignment.Target);
        var saved = _targetValue;
        _targetValue = place.Access;
        var value = _Expression(assignment.Value);
        _targetValue = saved;

        if (place.Prelude.Count == 0 && place.Epilogue.Count == 0)
        {
            output.Append(indent).Append(place.Access).Append(" = ").Append(value).Append(";\n");
            return;
        }

        output.Append(indent).Append("{\n");
        foreach (var line in place.Prelude) output.Append(indent).Append(Indent).Append(line).Append('\n');
        output.Append(indent).Append(Indent).Append(place.Access).Append(" = ").Append(value).Append(";\n");
        foreach (var line in place.Epilogue) output.Append(indent).Append(Indent).Append(line).Append('\n');
        output.Append(indent).Append("}\n");
    }

    /// <summary>
    /// Ce qui remet un élément d'un tableau en colonnes à sa place après l'étape qui l'a modifié : seules
    /// les colonnes des champs que l'étape écrit (<see cref="_WrittenPaths"/>), ou tout l'élément
    /// (<c>set</c>) quand on ne sait pas lesquels ; rien si elle n'en écrit aucun.
    /// </summary>
    private IReadOnlyList<string>? _PutBack(IrEachStage stage, MoldSymbol mold, string source, string index, string element)
    {
        var parameter = stage.Apply switch
        {
            IrInvoke { Target: IrLambda lambda } => (lambda.Parameters[0], lambda.Body),
            IrCall call when _bodies.TryGetValue(call.Gear, out var body) => (call.Gear.Parameters[0], body),
            _ => default((ParameterSymbol, IrBlock)?),
        };
        var written = parameter is { } known ? _WrittenPaths(known.Item1, known.Item2, []) : null;
        if (written is null || written.Contains("")) return [$"{source}.set({index}, {element});"];

        var columns = _Columns(mold).Where(c => written.Any(w => w == c.Path || w.StartsWith(c.Path + ".", StringComparison.Ordinal) || c.Path.StartsWith(w + ".", StringComparison.Ordinal)));
        var lines = columns.Select(c => $"{source}.columns().{c.Name}[{index}] = {element}{c.Access[1..]};").ToArray();
        return lines.Length == 0 ? null : lines;
    }

    /// <summary>
    /// Les champs que <paramref name="body"/> écrit dans son paramètre <c>mut</c> <paramref name="parameter"/>,
    /// en chemins depuis lui (<c>position.x</c>, <c>""</c> pour le paramètre entier) : affectations en place,
    /// <c>push</c>, <c>add</c>, parcours qui modifient, et ce qu'en font les gears auxquels il est passé en
    /// <c>mut</c>. <see langword="null"/> si un gear appelé n'est pas connu.
    /// </summary>
    private HashSet<string>? _WrittenPaths(ParameterSymbol parameter, IrBlock body, HashSet<GearSymbol> visiting)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in IrWalk.Statements(body))
        {
            if (statement is IrContentAssignment assignment && _PathFrom(parameter, assignment.Target) is { } path) paths.Add(path);
        }

        foreach (var expression in IrWalk.AllExpressions(body))
        {
            switch (expression)
            {
                case IrPush push when _PathFrom(parameter, push.Target) is { } path:
                    paths.Add(path);
                    break;
                case IrAdd add when _PathFrom(parameter, add.Target) is { } path:
                    paths.Add(path);
                    break;
                case IrEach { Modifies: true } each when _PathFrom(parameter, each.Source) is { } path:
                    paths.Add(path);
                    break;
                case IrCall call:
                    for (var i = 0; i < call.Arguments.Count; ++i)
                    {
                        if (!call.Gear.Parameters[i].IsMutable || _PathFrom(parameter, call.Arguments[i]) is not { } prefix) continue;
                        if (!visiting.Add(call.Gear) || !_bodies.TryGetValue(call.Gear, out var callee)) return null;
                        var inner = _WrittenPaths(call.Gear.Parameters[i], callee, visiting);
                        visiting.Remove(call.Gear);
                        if (inner is null) return null;
                        foreach (var written in inner) paths.Add(prefix.Length == 0 ? written : written.Length == 0 ? prefix : $"{prefix}.{written}");
                    }
                    break;
            }
        }
        return paths;
    }

    /// <summary>
    /// Le chemin de champs depuis <paramref name="parameter"/> jusqu'à <paramref name="target"/> (<c>position.x</c>),
    /// arrêté au premier index ou élément de tuple, qui touche tout le champ qui le contient ;
    /// <see langword="null"/> si la cible ne part pas de ce paramètre.
    /// </summary>
    private static string? _PathFrom(ParameterSymbol parameter, IrExpression target)
    {
        var fields = new List<string>();
        var current = target;
        while (true)
        {
            switch (current)
            {
                case IrMemberAccess member:
                    fields.Insert(0, member.Field.Name);
                    current = member.Target;
                    continue;
                case IrIndex or IrTupleItem:
                    fields.Clear();
                    current = current is IrIndex index ? index.Target : ((IrTupleItem)current).Target;
                    continue;
                case IrRetype retype:
                    current = retype.Value;
                    continue;
                case IrParameterReference reference when reference.Parameter == parameter:
                    return string.Join('.', fields);
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Une opération sur une place écrite dans une expression (<c>push</c>, <c>add</c>) : directe sur une
    /// variable, sinon dans une lambda appelée sur place qui prépare la place d'abord (une
    /// expression-instruction dans un guard, dont un échec doit quitter le guard).
    /// </summary>
    private string _OnPlace(IrExpression target, Func<string, string> operation)
    {
        var place = _Place(target);
        var code = operation(place.Access);
        if (place.Prelude.Count == 0 && place.Epilogue.Count == 0) return code;

        var body = string.Join(' ', [.. place.Prelude, code + ";", .. place.Epilogue]);
        if (!_UseStatementExpressions) return $"[&]() {{ {body} }}()";
        _usesStatementExpressions = true;
        return $"({{ {body} }})";
    }

    /// <summary>
    /// <c>p with {…}</c> : la copie de la valeur dans une variable de passage, les changements écrits comme
    /// des affectations en place, puis la copie. Dans un guard, une instruction-expression, pour qu'un index
    /// absent puisse faire échouer le guard ; ailleurs, une lambda appelée sur place.
    /// </summary>
    private string _With(IrWith with)
    {
        var type = _Type(with.Type);
        var source = _Consumed(with.Source);
        var name = _Declare(with.Temp);

        var body = new StringBuilder();
        foreach (var change in with.Changes) _ContentAssignment(body, change, "");
        // Les #line ne vont que sur une ligne à eux : ici, tout tient dans une expression.
        var statements = string.Concat(body.ToString().Split('\n').Where(line => !line.StartsWith("#line", StringComparison.Ordinal)).Select(line => line.Trim() + " "));

        if (_UseStatementExpressions)
        {
            _usesStatementExpressions = true;
            return $"({{ {type} {name} = {source}; {statements}{name}; }})";
        }
        return $"[{(_atFileLevel ? "" : "&")}]() -> {type} {{ {type} {name} = {source}; {statements}return {name}; }}()";
    }
}
