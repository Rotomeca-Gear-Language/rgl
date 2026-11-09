using System.Text;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Le JSON (<c>&lt;- json&lt;T&gt; chemin</c>, <c>valeur -&gt; json chemin;</c>). Le C++ n'a pas de réflexion : pour chaque type lu
/// ou écrit, le compilateur génère une fonction (<c>rgl_jr_N</c> lit un <c>rgl::standard::json::value</c> — l'arbre que le runtime a
/// analysé — en une <c>nullable&lt;T&gt;</c>, <c>rgl_jw_N</c> écrit une valeur dans une <c>mstring</c>), qui appelle celles de ses champs,
/// éléments ou membres ; le chemin de la valeur fautive se construit pendant la descente. Une fonction n'existe qu'une fois par type
/// (<see cref="_JsonKey"/>) et seuls les types réellement lus ou écrits en ont une.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Les fonctions de lecture déjà demandées, par type (<see cref="_JsonKey"/>).</summary>
    private readonly Dictionary<string, string> _jsonReaders = new(StringComparer.Ordinal);

    /// <summary>Les fonctions d'écriture déjà demandées, par type.</summary>
    private readonly Dictionary<string, string> _jsonWriters = new(StringComparer.Ordinal);

    /// <summary>Les fonctions demandées dont le corps n'est pas encore écrit : nom, type, et vrai pour une lecture.</summary>
    private readonly Queue<(string Name, IrType Type, bool Read)> _jsonPending = new();

    private int _jsonCount;

    /// <summary>Le mold <c>ParseError</c> du programme, envoyé par un JSON qui ne convient pas.</summary>
    private MoldSymbol _parseErrorMold = null!;

    /// <summary>La valeur par défaut des champs de mold qui en ont une.</summary>
    private IReadOnlyDictionary<MoldFieldSymbol, IrExpression> _fieldDefaults = new Dictionary<MoldFieldSymbol, IrExpression>();

    /// <summary>Le type sous sa forme JSON : un ingot est sa base, et deux types qui s'écrivent pareil partagent leurs fonctions.</summary>
    private string _JsonKey(IrType type) => type switch
    {
        IngotType ingot => _JsonKey(ingot.Underlying),
        MoldType mold => "m:" + _moldNames[mold.Symbol],
        EnumType enumeration => "e:" + enumeration.Symbol.CppName,
        NullableType nullable => "?" + _JsonKey(nullable.Inner),
        ArrayType array => $"[{array.Kind}{array.Size}.{array.Max}|{_JsonKey(array.Element)}]",
        DictionaryType dictionary => $"{{{_JsonKey(dictionary.Key)}:{_JsonKey(dictionary.Value)}}}",
        TupleType tuple => "(" + string.Join(",", tuple.Elements.Select(_JsonKey)) + ")",
        _ => type.ToString() ?? "",
    };

    /// <summary>Le nom de la fonction qui lit un <paramref name="type"/> en JSON ; elle est écrite avec les autres, à la fin.</summary>
    private string _JsonReader(IrType type) => _JsonFunction(_jsonReaders, "rgl_jr_", type, read: true);

    /// <summary>Le nom de la fonction qui écrit un <paramref name="type"/> en JSON.</summary>
    private string _JsonWriter(IrType type) => _JsonFunction(_jsonWriters, "rgl_jw_", type, read: false);

    private string _JsonFunction(Dictionary<string, string> known, string prefix, IrType type, bool read)
    {
        type = _Representation(type);
        var key = _JsonKey(type);
        if (known.TryGetValue(key, out var name)) return name;

        name = prefix + ++_jsonCount;
        known[key] = name;
        _jsonPending.Enqueue((name, type, read));
        return name;
    }

    /// <summary>
    /// Le C++ de toutes les fonctions JSON demandées : leurs déclarations (elles s'appellent entre elles, parfois en cercle pour un
    /// mold qui contient un tableau de lui-même) puis leurs corps. Écrire un corps peut en demander d'autres.
    /// </summary>
    private string _JsonFunctions()
    {
        var prototypes = new StringBuilder();
        var bodies = new StringBuilder();
        while (_jsonPending.Count > 0)
        {
            var (name, type, read) = _jsonPending.Dequeue();
            var cpp = _Type(type);
            if (read)
            {
                var signature = $"static bool {name}([[maybe_unused]] const rgl::standard::json::value& v, [[maybe_unused]] rgl::standard::nullable<{cpp}>& out, [[maybe_unused]] rgl::standard::json::context& ctx)";
                prototypes.Append("[[maybe_unused]] ").Append(signature).Append(";\n");
                bodies.Append(signature).Append(" {\n").Append(_JsonReaderBody(type)).Append("}\n\n");
            }
            else
            {
                var signature = $"static bool {name}([[maybe_unused]] rgl::standard::mstring& out, [[maybe_unused]] const {cpp}& v, [[maybe_unused]] int depth, [[maybe_unused]] rgl::standard::json::context& ctx)";
                prototypes.Append("[[maybe_unused]] ").Append(signature).Append(";\n");
                bodies.Append(signature).Append(" {\n").Append(_JsonWriterBody(type)).Append("}\n\n");
            }
        }
        return prototypes.Length == 0 ? "" : prototypes.Append('\n').Append(bodies).ToString();
    }

    private const string JsonNs = "rgl::standard::json::";

    /// <summary>Le corps de la fonction qui lit un <paramref name="type"/> : la valeur <c>v</c> dans <c>out</c>, ou l'erreur dans <c>ctx</c>.</summary>
    private string _JsonReaderBody(IrType type)
    {
        var body = new StringBuilder();
        void Line(string text) => body.Append(Indent).Append(text).Append('\n');

        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.Bool }:
                Line($"return {JsonNs}read_bool(v, out, ctx);");
                break;

            case PrimitiveType { IsInteger: true } integer:
                Line($"if (!{JsonNs}read_integer<{_Type(integer)}>(v, out, ctx, {_Quote(integer.Unbounded.ToString())})) return false;");
                _JsonBoundsCheck(Line, integer);
                Line("return true;");
                break;

            case PrimitiveType { Kind: PrimitiveKind.Float or PrimitiveKind.Double } real:
                Line($"if (!{JsonNs}read_real<{_Type(real)}>(v, out, ctx, {_Quote(real.Unbounded.ToString())})) return false;");
                _JsonBoundsCheck(Line, real);
                Line("return true;");
                break;

            case PrimitiveType { Kind: PrimitiveKind.String }:
                Line($"return {JsonNs}read_string(v, out, ctx);");
                break;

            case PrimitiveType { Kind: PrimitiveKind.MString }:
                Line($"return {JsonNs}read_mstring(v, out, ctx);");
                break;

            case NullableType nullable:
                Line($"if (v.type == {JsonNs}kind::null) {{ out.emplace(); return true; }}");
                Line($"rgl::standard::nullable<{_Type(nullable.Inner)}> inner;");
                Line($"if (!{_JsonReader(nullable.Inner)}(v, inner, ctx)) return false;");
                Line("out.emplace(std::move(inner));");
                Line("return true;");
                break;

            case EnumType enumeration:
                Line($"if (v.type != {JsonNs}kind::text) return {JsonNs}mismatch(v, ctx, \"une string\");");
                var cppEnum = _Type(enumeration);
                foreach (var variant in enumeration.Symbol.Variants)
                    Line($"if (v.text == {_Quote(variant.Name)}) {{ out.emplace({cppEnum}::rgl_c_{variant.Name}); return true; }}");
                Line($"return ctx.fail({_Quote("variante inconnue : attendu " + string.Join(", ", enumeration.Symbol.Variants.Select(v => v.Name)))});");
                break;

            case MoldType mold:
                _JsonMoldReader(Line, mold);
                break;

            case ArrayType array:
                _JsonArrayReader(Line, array);
                break;

            case DictionaryType dictionary:
                Line($"if (v.type != {JsonNs}kind::object) return {JsonNs}mismatch(v, ctx, \"un objet\");");
                Line("const auto mark = ctx.mark();");
                Line($"{_Type(dictionary)} entries;");
                Line("for (std::size_t i = 0; i < v.items.size(); ++i) {");
                Line($"{Indent}ctx.key(v.keys[i]);");
                Line($"{Indent}rgl::standard::nullable<{_Type(dictionary.Value)}> item;");
                Line($"{Indent}if (!{_JsonReader(dictionary.Value)}(v.items[i], item, ctx)) return false;");
                Line($"{Indent}if (!entries.can_add(v.keys[i])) return ctx.fail(\"clé en double\");");
                Line($"{Indent}ctx.reset(mark);");
                Line($"{Indent}entries.add(v.keys[i], std::move(*item), \"json\");");
                Line("}");
                Line("out.emplace(std::move(entries));");
                Line("return true;");
                break;

            case TupleType tuple:
                Line($"if (v.type != {JsonNs}kind::array) return {JsonNs}mismatch(v, ctx, \"un tableau\");");
                Line($"if (v.items.size() != {tuple.Elements.Count}) return ctx.fail({_Quote($"attendu un tableau de {tuple.Elements.Count} éléments")});");
                Line("const auto mark = ctx.mark();");
                for (var i = 0; i < tuple.Elements.Count; ++i)
                {
                    Line($"rgl::standard::nullable<{_Type(tuple.Elements[i])}> item{i};");
                    Line($"ctx.index({i});");
                    Line($"if (!{_JsonReader(tuple.Elements[i])}(v.items[{i}], item{i}, ctx)) return false;");
                    Line("ctx.reset(mark);");
                }
                Line($"out.emplace({string.Join(", ", Enumerable.Range(0, tuple.Elements.Count).Select(i => $"std::move(*item{i})"))});");
                Line("return true;");
                break;

            default:
                throw new InvalidOperationException($"Type sans lecture JSON : {type}. Le binder ne devrait pas l'avoir laissé passer.");
        }
        return body.ToString();
    }

    /// <summary>Le contrôle des bornes d'un type borné, juste après la lecture de sa base.</summary>
    private void _JsonBoundsCheck(Action<string> line, PrimitiveType type)
    {
        if (type.Bounds is null) return;
        _includes.Add("rgl/standard/cast.hpp");
        var (min, max) = _Bounds(type);
        line($"if (!rgl::standard::in_bounds<{_Type(type)}>(*out, {min}, {max})) return ctx.fail({_Quote(_OutOfBoundsMessage(type))});");
    }

    /// <summary>Un objet en mold : chaque champ cherché par son nom, les absents prennent leur défaut, les membres en trop sont ignorés.</summary>
    private void _JsonMoldReader(Action<string> line, MoldType mold)
    {
        var fields = mold.Symbol.Fields;
        line($"if (v.type != {JsonNs}kind::object) return {JsonNs}mismatch(v, ctx, \"un objet\");");
        if (fields.Count > 0) line("const auto mark = ctx.mark();");

        for (var i = 0; i < fields.Count; ++i)
            line($"rgl::standard::nullable<{_Type(fields[i].Type)}> f{i};");
        for (var i = 0; i < fields.Count; ++i)
        {
            var name = _Quote(fields[i].Name);
            line($"if (const auto* m{i} = {JsonNs}member(v, {name}); m{i} != nullptr) {{");
            line($"{Indent}ctx.key({name});");
            line($"{Indent}if (!{_JsonReader(fields[i].Type)}(*m{i}, f{i}, ctx)) return false;");
            line($"{Indent}ctx.reset(mark);");
            line("}");
        }
        for (var i = 0; i < fields.Count; ++i)
        {
            if (_fieldDefaults.TryGetValue(fields[i], out var fallback)) line($"if (!f{i}.has_value()) f{i}.emplace({_Expression(fallback)});");
            else if (fields[i].Type is NullableType) line($"if (!f{i}.has_value()) f{i}.emplace();");
            else line($"if (!f{i}.has_value()) return {JsonNs}missing(ctx, {_Quote(fields[i].Name)});");
        }
        line($"out.emplace({_Type(mold)}{{{string.Join(", ", Enumerable.Range(0, fields.Count).Select(i => $"std::move(*f{i})"))}}});");
        line("return true;");
    }

    /// <summary>Un tableau JSON : ses éléments lus un à un, puis rangés dans le tableau de la bonne sorte (dont la taille est vérifiée).</summary>
    private void _JsonArrayReader(Action<string> line, ArrayType array)
    {
        var element = _Type(array.Element);
        line($"if (v.type != {JsonNs}kind::array) return {JsonNs}mismatch(v, ctx, \"un tableau\");");
        line("const auto mark = ctx.mark();");
        line($"rgl::standard::array<{element}> items;");
        line("items.reserve(v.items.size());");
        line("for (std::size_t i = 0; i < v.items.size(); ++i) {");
        line($"{Indent}ctx.index(i);");
        line($"{Indent}rgl::standard::nullable<{element}> item;");
        line($"{Indent}if (!{_JsonReader(array.Element)}(v.items[i], item, ctx)) return false;");
        line($"{Indent}ctx.reset(mark);");
        line($"{Indent}items.push(std::move(*item));");
        line("}");

        var target = _Type(array);
        if (array.Kind != ArrayKind.Dynamic)
        {
            line($"out = {target}::try_from(items);");
            line($"if (!out.has_value()) return ctx.fail({_Quote($"le tableau n'a pas une taille permise par {array}")});");
        }
        else if (_IsSoa(array)) line($"out.emplace({target}::from(items));");
        else line("out.emplace(std::move(items));");
        line("return true;");
    }

    /// <summary>Le corps de la fonction qui écrit un <paramref name="type"/> : <c>v</c> ajouté à <c>out</c>, à la profondeur <c>depth</c>.</summary>
    private string _JsonWriterBody(IrType type)
    {
        var body = new StringBuilder();
        void Line(string text) => body.Append(Indent).Append(text).Append('\n');

        switch (type)
        {
            case PrimitiveType { Kind: PrimitiveKind.Bool }:
                Line($"{JsonNs}write_bool(out, v);");
                Line("return true;");
                break;

            case PrimitiveType { IsInteger: true }:
                Line($"{JsonNs}write_integer(out, v);");
                Line("return true;");
                break;

            case PrimitiveType { Kind: PrimitiveKind.Float or PrimitiveKind.Double }:
                Line($"return {JsonNs}write_real(out, v, ctx);");
                break;

            case PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString }:
                Line($"{JsonNs}write_string(out, v);");
                Line("return true;");
                break;

            case NullableType nullable:
                Line("if (!v.has_value()) { out.append(\"null\", 4); return true; }");
                Line($"return {_JsonWriter(nullable.Inner)}(out, *v, depth, ctx);");
                break;

            case EnumType enumeration:
                var cppEnum = _Type(enumeration);
                foreach (var variant in enumeration.Symbol.Variants)
                    Line($"if (v == {cppEnum}::rgl_c_{variant.Name}) {{ {JsonNs}write_string(out, {_Quote(variant.Name)}, {Encoding.UTF8.GetByteCount(variant.Name)}); return true; }}");
                Line("return false;");
                break;

            case MoldType mold:
                var fields = mold.Symbol.Fields;
                Line("out.push_back('{');");
                if (fields.Count > 0) Line("const auto mark = ctx.mark();");
                for (var i = 0; i < fields.Count; ++i)
                {
                    var name = _Quote(fields[i].Name);
                    if (i > 0) Line("out.push_back(',');");
                    Line($"{JsonNs}newline(out, depth + 1);");
                    Line($"{JsonNs}write_string(out, {name}, {Encoding.UTF8.GetByteCount(fields[i].Name)});");
                    Line("out.append(\": \", 2);");
                    Line($"ctx.key({name});");
                    Line($"if (!{_JsonWriter(fields[i].Type)}(out, v.rgl_f_{fields[i].Name}, depth + 1, ctx)) return false;");
                    Line("ctx.reset(mark);");
                }
                if (fields.Count > 0) Line($"{JsonNs}newline(out, depth);");
                Line("out.push_back('}');");
                Line("return true;");
                break;

            case ArrayType array:
                Line("out.push_back('[');");
                Line("const auto mark = ctx.mark();");
                Line("for (std::size_t i = 0; i < v.size(); ++i) {");
                Line($"{Indent}if (i != 0) out.push_back(',');");
                Line($"{Indent}{JsonNs}newline(out, depth + 1);");
                Line($"{Indent}ctx.index(i);");
                Line($"{Indent}const auto& item = v[i];");
                Line($"{Indent}if (!{_JsonWriter(array.Element)}(out, item, depth + 1, ctx)) return false;");
                Line($"{Indent}ctx.reset(mark);");
                Line("}");
                Line($"if (v.size() != 0) {JsonNs}newline(out, depth);");
                Line("out.push_back(']');");
                Line("return true;");
                break;

            case DictionaryType dictionary:
                _Use(RuntimeComponent.Memory, "rgl/standard/dictionary.hpp");
                Line("out.push_back('{');");
                Line("const auto mark = ctx.mark();");
                Line("bool first = true;");
                Line("for (const auto& [key, stored] : v) {");
                Line($"{Indent}const auto& item = stored;");
                Line($"{Indent}if (!first) out.push_back(',');");
                Line($"{Indent}first = false;");
                Line($"{Indent}{JsonNs}newline(out, depth + 1);");
                Line($"{Indent}{JsonNs}write_string(out, key);");
                Line($"{Indent}out.append(\": \", 2);");
                Line($"{Indent}ctx.key(key);");
                Line($"{Indent}if (!{_JsonWriter(dictionary.Value)}(out, item, depth + 1, ctx)) return false;");
                Line($"{Indent}ctx.reset(mark);");
                Line("}");
                Line($"if (!first) {JsonNs}newline(out, depth);");
                Line("out.push_back('}');");
                Line("return true;");
                break;

            case TupleType tuple:
                Line("out.push_back('[');");
                Line("const auto mark = ctx.mark();");
                for (var i = 0; i < tuple.Elements.Count; ++i)
                {
                    if (i > 0) Line("out.push_back(',');");
                    Line($"{JsonNs}newline(out, depth + 1);");
                    Line($"ctx.index({i});");
                    Line($"if (!{_JsonWriter(tuple.Elements[i])}(out, rgl::standard::item<{i}>(v), depth + 1, ctx)) return false;");
                    Line("ctx.reset(mark);");
                }
                if (tuple.Elements.Count > 0) Line($"{JsonNs}newline(out, depth);");
                Line("out.push_back(']');");
                Line("return true;");
                break;

            default:
                throw new InvalidOperationException($"Type sans écriture JSON : {type}. Le binder ne devrait pas l'avoir laissé passer.");
        }
        return body.ToString();
    }

    /// <summary>
    /// <c>&lt;- json&lt;T&gt; chemin</c>, dans un guard : la valeur lue, ou l'échec du guard — une <c>IoError</c> si le fichier manque, une
    /// <c>ParseError</c> avec le chemin de la valeur fautive si le JSON ne convient pas.
    /// </summary>
    private string _ReadJson(IrReadJson read, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.Json, "rgl/standard/json.hpp");
        _usesStatementExpressions = true;
        var (path, context, result, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        return $"({{ const auto& {path} = {operand(read.Path)}; rgl::standard::json::context {context}; rgl::standard::nullable<{_Type(read.Type)}> {result}; "
            + $"const auto {status} = rgl::standard::json::read({path}, {context}, {result}, &{_JsonReader(read.Type)}); "
            + $"if (!{status}.ok) {{ if ({status}.io) return {_IoFailure($"{status}.io_status", path, read.Span)}; return {_ParseFailure(context, read.Span)}; }} "
            + $"std::move(*{result}); }})";
    }

    /// <summary>
    /// <c>valeur -&gt; json chemin;</c>, dans un guard : un bloc qui écrit la valeur, puis le fichier, et fait échouer le guard si l'un
    /// des deux ne va pas.
    /// </summary>
    private string _WriteJson(IrWriteJson write)
    {
        _Use(RuntimeComponent.Json, "rgl/standard/json.hpp");
        var (value, path, text, context, status) = ($"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}", $"rgl_t{++_temporaryCount}");
        return $"{{ const auto& {value} = {_Expression(write.Value)}; const auto& {path} = {_Expression(write.Path)}; rgl::standard::mstring {text}; rgl::standard::json::context {context}; "
            + $"if (!{_JsonWriter(write.Value.Type)}({text}, {value}, 0, {context})) return {_ParseFailure(context, write.Span)}; "
            + $"const auto {status} = rgl::standard::json::write_file({path}, {text}); "
            + $"if (!{status}.ok) return {_IoFailure($"{status}.io_status", path, write.Span)}; }}";
    }

    /// <summary>L'échec du guard courant avec une <c>ParseError</c> : le message du <c>context</c> et le chemin de la valeur fautive.</summary>
    private string _ParseFailure(string context, SourceSpan span) =>
        _StandardFailure(_parseErrorMold, $"{context}.message()", span, "400u", $"{context}.failed_path()");

    /// <summary>
    /// <c>parseJson&lt;T&gt;(texte)</c> : une lambda appelée sur place qui lit le texte et rend <c>ok(valeur)</c>, ou <c>err(ParseError)</c> avec
    /// le message et le chemin de la valeur fautive. Elle ne fait jamais échouer un guard, et sert donc partout.
    /// </summary>
    private string _ParseJson(IrParseJson parse, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.Json, "rgl/standard/json.hpp");
        var result = _Type(parse.Result);
        var type = _Type(parse.Target);
        var fields = new List<string> { "c.message()", _WhereString(parse.Span), "400u", "c.failed_path()" };
        while (fields.Count < _parseErrorMold.Fields.Count) fields.Add("{}");
        return $"[&]() -> {result} {{ const auto& t = {operand(parse.Text)}; rgl::standard::json::context c; rgl::standard::nullable<{type}> r; "
            + $"if (!rgl::standard::json::from_text(t, c, r, &{_JsonReader(parse.Target)})) return {result}::err({_Type(new MoldType(_parseErrorMold))}{{{string.Join(", ", fields)}}}); "
            + $"return {result}::ok(std::move(*r)); }}()";
    }

    /// <summary><c>toJson(valeur)</c> : le JSON de la valeur dans une <c>string</c>, ou une <c>ParseError</c> pour ce qui n'en a pas (NaN, infini).</summary>
    private string _ToJson(IrToJson write, Func<IrExpression, string> operand)
    {
        _Use(RuntimeComponent.Json, "rgl/standard/json.hpp");
        var result = _Type(write.Result);
        var fields = new List<string> { "c.message()", _WhereString(write.Span), "400u", "c.failed_path()" };
        while (fields.Count < _parseErrorMold.Fields.Count) fields.Add("{}");
        return $"[&]() -> {result} {{ const auto& v = {operand(write.Value)}; rgl::standard::mstring text; rgl::standard::json::context c; "
            + $"if (!{_JsonWriter(write.Value.Type)}(text, v, 0, c)) return {result}::err({_Type(new MoldType(_parseErrorMold))}{{{string.Join(", ", fields)}}}); "
            + $"return {result}::ok(text.to_immutable()); }}()";
    }
}
