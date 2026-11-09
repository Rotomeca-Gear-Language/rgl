using System.Globalization;
using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.Emit;

/// <summary>
/// Les gears, guards et emits : signatures et corps, et les échecs d'un guard (fail, échecs
/// internes, propagation d'un guard appelé), avec l'endroit du source qu'ils portent.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>
    /// La signature C++ d'un gear : <c>static T rgl_g_nom(…)</c>. Un nombre, un bool ou un symbole
    /// passe par valeur ; une chaîne, un tableau ou une Exception par référence constante. Le gear ne
    /// modifie jamais ses paramètres : les deux sont équivalents, le premier est simplement plus
    /// rapide. Un guard renvoie un <c>rgl::standard::guarded&lt;T&gt;</c>, qui peut porter un échec.
    /// </summary>
    private string _Signature(GearSymbol gear, string suffix = "", IEnumerable<ParameterSymbol>? only = null)
    {
        // Une coroutine survit à l'appel qui la lance : ses paramètres sont copiés dans son cadre, jamais pris par référence (un temporaire serait détruit).
        // Un @task part sur un autre thread : lui aussi reçoit des copies (son corps, lui, les lit par référence : il n'en change aucune).
        var byValue = gear.IsCoroutine || (gear.Async == AsyncMode.Task && suffix != TaskBodySuffix)
            // Un gear écrit en boucle réaffecte ses paramètres à chaque tour : des variables, donc des valeurs.
            || _tailGears.Contains(gear);
        var parameters = (only ?? gear.Parameters).Select(p => $"[[maybe_unused]] {(byValue ? _Type(p.Type) : _ParameterType(p))} {_Name(p)}");
        // Un composant écrit en C++ reçoit en plus la position RGL de l'appel : le « where » de la NativeError qu'une exception y fait naître.
        if (gear.IsNativeGuard && only is null) parameters = parameters.Append("[[maybe_unused]] const char* rgl_where");
        // [[maybe_unused]] : un gear jamais appelé n'est pas une erreur, et Clang ne doit pas avertir.
        return $"[[maybe_unused]] static {(suffix == TaskBodySuffix ? _TaskBodyType(gear) : _ResultType(gear))} rgl_g_{gear.CppName}{suffix}({string.Join(", ", parameters)})";
    }

    /// <summary>
    /// <c>return f(a, b);</c> dans un gear écrit en boucle : les nouveaux arguments sont calculés, tous, avec les valeurs de ce tour, puis déplacés dans
    /// les paramètres, et le tour suivant recommence. Un argument qui est le paramètre lui-même ne change rien.
    /// </summary>
    private void _TailCall(StringBuilder output, IrCall call, string indent)
    {
        var gear = call.Gear;
        output.Append(indent).Append("{\n");
        var inner = indent + Indent;
        var assignments = new List<string>();
        for (var i = 0; i < gear.Parameters.Count; i++)
        {
            var parameter = gear.Parameters[i];
            if (call.Arguments[i] is IrParameterReference same && same.Parameter == parameter) continue;
            var temporary = $"rgl_t{++_temporaryCount}";
            output.Append(inner).Append(_Type(parameter.Type)).Append(' ').Append(temporary).Append(" = ").Append(_Expression(call.Arguments[i])).Append(";\n");
            assignments.Add($"{_Name(parameter)} = std::move({temporary});");
        }
        foreach (var assignment in assignments) output.Append(inner).Append(assignment).Append('\n');
        output.Append(inner).Append("continue;\n");
        output.Append(indent).Append("}\n");
    }

    /// <summary>Le suffixe du corps d'un <c>@task</c> : le gear ordinaire que le gear de ce nom fait exécuter sur le pool.</summary>
    private const string TaskBodySuffix = "_task";

    /// <summary>Le type C++ que renvoie le corps d'un <c>@task</c> : son résultat <c>T ! E</c>, ou rien.</summary>
    private string _TaskBodyType(GearSymbol gear) => gear.BodyType == PrimitiveType.Void ? "void" : _Type(gear.BodyType);

    /// <summary>
    /// Le gear d'un <c>@task</c> : il copie ses paramètres dans une lambda que le pool exécute, et rend la promesse de son résultat. Le corps est un gear
    /// ordinaire, <c>rgl_g_nom_task</c>.
    /// </summary>
    private void _TaskEntry(StringBuilder output, GearSymbol gear)
    {
        _includes.Add("rgl/standard/async.hpp");
        var result = _Type(gear.Promise!.Awaited);
        var names = string.Join(", ", gear.Parameters.Select(_Name));
        var call = $"rgl_g_{gear.CppName}{TaskBodySuffix}({names})";
        output.Append(_Signature(gear)).Append(" {\n")
            .Append(Indent).Append("return rgl::standard::run_blocking<").Append(result).Append(">([").Append(names).Append("]() -> ").Append(result).Append(" { ")
            .Append(gear.BodyType == PrimitiveType.Void ? $"{call}; return {result}{{}};" : $"return {call};").Append(" });\n}\n\n");
    }

    /// <summary>
    /// Le type C++ que renvoie un gear : celui de son type de retour, le <c>guarded</c> d'un guard, ou la promesse d'une
    /// coroutine (<c>@async</c> comme <c>@fire</c>, dont l'appelant ne voit pourtant rien).
    /// </summary>
    private string _ResultType(GearSymbol gear)
    {
        if (gear.Promise is { } promise) return _Type(promise);
        var result = gear.ReturnType == PrimitiveType.Void ? "void" : _Type(gear.ReturnType);
        return gear.Kind == GearKind.Guard ? $"rgl::standard::guarded<{result}, {_ErrorsType(gear)}>" : result;
    }

    /// <summary>
    /// Les nombres d'arguments explicites pour lesquels un appel laisse au gear le soin de calculer une valeur par défaut
    /// écrite : l'appel passe par un enveloppeur <c>rgl_g_nom_dK</c> (voir <see cref="_DefaultEntry"/>).
    /// </summary>
    private static IEnumerable<int> _DefaultEntries(GearSymbol gear) =>
        Enumerable.Range(0, gear.Parameters.Count).Where(k => gear.Parameters.Skip(k).Any(p => p.Default == ParameterDefault.Expression) && gear.Parameters.Skip(k).All(p => p.HasDefault));

    /// <summary>
    /// Les paramètres que reçoit l'enveloppeur d'un appel à <paramref name="given"/> arguments explicites : ces arguments,
    /// puis les valeurs que l'appelant remplit lui-même (<c>__CALLER__</c>…) parmi les paramètres omis.
    /// </summary>
    private static IEnumerable<ParameterSymbol> _EntryParameters(GearSymbol gear, int given) =>
        gear.Parameters.Where((p, i) => i < given || (p.HasDefault && p.Default != ParameterDefault.Expression));

    /// <summary>Le nom C++ de l'enveloppeur d'un gear pour <paramref name="given"/> arguments explicites.</summary>
    private static string _EntrySuffix(int given) => $"_d{given}";

    /// <summary>
    /// L'enveloppeur d'un gear pour <paramref name="given"/> arguments explicites : il calcule, dans l'ordre, les valeurs par
    /// défaut écrites des paramètres omis — chacune lit les paramètres qui la précèdent — puis appelle le gear.
    /// </summary>
    private void _DefaultEntry(StringBuilder output, IrGear gear, int given)
    {
        var symbol = gear.Symbol;
        output.Append(_Signature(symbol, _EntrySuffix(given), _EntryParameters(symbol, given))).Append(" {\n");
        // Les valeurs par défaut sont écrites hors de tout gear : un guard ne peut pas y échouer.
        _gear = null;
        foreach (var parameter in symbol.Parameters.Skip(given).Where(p => p.Default == ParameterDefault.Expression))
        {
            var value = gear.Defaults!.First(d => d.Parameter == parameter).Value;
            output.Append(Indent).Append("[[maybe_unused]] const ").Append(_Type(parameter.Type)).Append(' ').Append(_Name(parameter)).Append(" = ").Append(_Expression(value)).Append(";\n");
        }
        output.Append(Indent).Append("return rgl_g_").Append(symbol.CppName).Append('(').Append(string.Join(", ", symbol.Parameters.Select(_Name))).Append(");\n}\n\n");
    }

    /// <summary>
    /// L'appel C++ du gear de <paramref name="call"/> : directement, ou par l'enveloppeur qui calcule les valeurs par défaut
    /// omises (les arguments omis ne sont pas écrits).
    /// </summary>
    private string _CallText(IrCall call, Func<IrExpression, string> operand)
    {
        var given = call.Arguments.Count;
        var omitted = call.Arguments.TakeWhile(a => a is not IrOmittedArgument).Count();
        if (call.Gear.IsNativeGuard) return $"rgl_g_{call.Gear.CppName}({string.Join(", ", call.Arguments.Select(operand).Append(_Where(call.Span)))})";
        if (omitted == given) return $"rgl_g_{call.Gear.CppName}({string.Join(", ", call.Arguments.Select(operand))})";
        return $"rgl_g_{call.Gear.CppName}{_EntrySuffix(omitted)}({string.Join(", ", call.Arguments.Where(a => a is not IrOmittedArgument).Select(operand))})";
    }

    /// <summary>
    /// Un gear et son corps. Un gear récursif commence par un garde qui compte sa profondeur (sauf
    /// sans limite). Un gear qui renvoie une valeur ne peut pas atteindre la fin de son corps (le
    /// binder l'a vérifié) : <c>__builtin_unreachable()</c> le dit à Clang, qui ne sait pas
    /// toujours le prouver seul (sauts d'un switch) ; inutile quand le corps finit déjà par un
    /// <c>return</c> ou un <c>fail</c>. Un guard <c>void</c> qui atteint la fin de son corps réussit.
    /// </summary>
    private void _Gear(StringBuilder output, IrGear gear)
    {
        _gear = gear.Symbol;
        if (gear.Symbol.IsNative)
        {
            _NativeGear(output, gear.Symbol);
            _gear = null;
            return;
        }
        output.Append(_Signature(gear.Symbol, gear.Symbol.Async == AsyncMode.Task ? TaskBodySuffix : "")).Append(" {\n");
        if (gear.Symbol.IsRecursive && !_tailGears.Contains(gear.Symbol) && _recursionLimit is { } limit)
        {
            _includes.Add("rgl/standard/recursion.hpp");
            output.Append(Indent).Append("[[maybe_unused]] const rgl::standard::recursion_guard rgl_recursion(")
                .Append(limit).Append("u, ").Append(_Where(gear.Symbol.Declaration)).Append(");\n");
        }

        // Avec des @tap, le corps devient une lambda : les taps avant passent d'abord, ceux d'après lisent ce qu'elle renvoie.
        var taps = gear.Taps ?? [];
        var inner = Indent;
        if (taps.Count > 0)
        {
            foreach (var tap in taps.Where(t => !t.IsAfter)) _Statement(output, tap.Statement, Indent);
            output.Append(Indent).Append("auto rgl_tap_body = [&]() -> ").Append(_ResultType(gear.Symbol)).Append(" {\n");
            inner = Indent + Indent;
        }

        // Un gear à récursion terminale : tout le corps est une boucle, que chaque appel terminal recommence (_TailCall).
        var loops = _tailGears.Contains(gear.Symbol);
        if (loops) _Note(gear.Symbol.Declaration, "récursion terminale écrite en boucle : plus de garde de profondeur, plus de limite de 200 appels");
        else if (gear.Symbol.IsRecursive && TailCalls.Reason(gear) is { } why)
            _AddNote(new OptimizationNote(gear.Symbol.Declaration, true, "la récursion reste une récursion", [why]));
        if (loops)
        {
            output.Append(inner).Append("for (;;) {\n");
            inner += Indent;
        }

        foreach (var statement in LoopFusion.Fuse(gear.Body.Statements, _AddNote)) _Statement(output, statement, inner);
        if (gear.Body.Statements is not [.., IrReturn or IrFailStatement or IrTrainBody])
        {
            // Une coroutine sans valeur qui atteint la fin de son corps termine sa promesse : « rien » veut dire que tout s'est bien passé.
            if (gear.Symbol.IsCoroutine) output.Append(inner).Append(gear.Symbol.BodyType == PrimitiveType.Void ? "co_return {};\n" : "__builtin_unreachable();\n");
            // Un guard qui atteint la fin de son corps laisse passer son premier paramètre (done implicite).
            else if (gear.ImplicitDone) output.Append(inner).Append("return ").Append(_Done(_Name(gear.Symbol.Parameters[0]))).Append(";\n");
            else if ((gear.Symbol.Async == AsyncMode.Task ? gear.Symbol.BodyType : gear.Symbol.ReturnType) != PrimitiveType.Void) output.Append(inner).Append("__builtin_unreachable();\n");
            else if (_InGuard) output.Append(inner).Append("return ").Append(_GuardSuccess()).Append(";\n");
        }

        if (loops) output.Append(inner[..^Indent.Length]).Append("}\n");

        if (taps.Count > 0)
        {
            output.Append(Indent).Append("};\n");
            var after = taps.Where(t => t.IsAfter).ToArray();
            var isGuard = gear.Symbol.Kind == GearKind.Guard;
            var isVoid = gear.Symbol.ReturnType == PrimitiveType.Void;
            if (isVoid && !isGuard)
            {
                output.Append(Indent).Append("rgl_tap_body();\n");
                foreach (var tap in after) _Statement(output, tap.Statement, Indent);
            }
            else
            {
                output.Append(Indent).Append("auto rgl_tap_result = rgl_tap_body();\n");
                var tapIndent = isGuard ? Indent + Indent : Indent;
                if (isGuard && after.Length > 0) output.Append(Indent).Append("if (!rgl_tap_result.failed()) {\n");
                if (!isVoid && after.Length > 0)
                    output.Append(tapIndent).Append("[[maybe_unused]] const auto& rgl_tap_value = ").Append(isGuard ? "rgl_tap_result.value()" : "rgl_tap_result").Append(";\n");
                foreach (var tap in after) _Statement(output, tap.Statement, isGuard ? tapIndent : Indent);
                if (isGuard && after.Length > 0) output.Append(Indent).Append("}\n");
                output.Append(Indent).Append("return rgl_tap_result;\n");
            }
        }
        output.Append("}\n\n");
        _gear = null;
        if (gear.Symbol.Async == AsyncMode.Task) _TaskEntry(output, gear.Symbol);
    }

    /// <summary>
    /// Le type C++ d'un paramètre de gear ou de lambda : par valeur pour un nombre, un bool, un symbole
    /// ou un enum ; par référence constante pour le reste (chaîne, tableau, mold, lambda…).
    /// </summary>
    private string _ParameterType(IrType type) =>
        _Representation(type) is PointerType or ArrayType or DictionaryType or TupleType or MoldType or NullableType or UnionType or ResultType or FunctionType
            or PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString }
            ? $"const {_Type(type)}&"
            : _Type(type);

    /// <summary>Le type C++ des erreurs d'un guard : celui de <see cref="GearSymbol.Errors"/>, ou <c>no_error</c> s'il ne peut pas échouer.</summary>
    private string _ErrorsType(GearSymbol guard)
    {
        _includes.Add("rgl/standard/exception.hpp");
        return guard.Errors is { } errors ? _Type(errors) : "rgl::standard::no_error";
    }

    /// <summary>
    /// Le <c>done</c> du guard courant avec la valeur C++ <paramref name="value"/> : une réussite qui
    /// arrête en plus la boucle de guard qui l'appelle.
    /// </summary>
    private string _Done(string value) => $"rgl::standard::done<{_Type(_gear!.ReturnType)}>({value})";

    /// <summary>La réussite du guard <c>void</c> courant : <c>rgl::standard::guarded&lt;void, E&gt;()</c>.</summary>
    private string _GuardSuccess() => $"rgl::standard::guarded<void, {_ErrorsType(_gear!)}>()";

    /// <summary>
    /// L'échec du guard courant avec l'erreur C++ <paramref name="error"/>, de type
    /// <paramref name="type"/> : mise dans l'union des erreurs du guard au besoin.
    /// </summary>
    private string _FailWith(string error, IrType type) =>
        $"rgl::standard::fail<{_ErrorsType(_gear!)}>({_IntoErrors(error, type, _gear!.Errors!)})";

    /// <summary>
    /// L'erreur C++ <paramref name="error"/>, de type <paramref name="type"/>, dans le type
    /// <paramref name="errors"/> qui la reçoit : telle quelle, ou mise dans leur union.
    /// </summary>
    private string _IntoErrors(string error, IrType type, IrType errors) =>
        errors == type ? error
        : errors is not UnionType union ? throw new InvalidOperationException($"Erreur « {type} » hors des erreurs « {errors} ».")
        : type is UnionType ? $"rgl::standard::widen<{_Type(union)}>({error})"
        : $"{_Type(union)}(rgl::standard::member<{union.IndexOf(type)}>, {error})";

    /// <summary>
    /// L'échec du guard courant avec une erreur standard (Exception, CastError) de ce message, à cet
    /// endroit du source : <c>rgl::standard::fail&lt;E&gt;(rgl_m_CastError{message, "main.rgl:3:5"})</c>.
    /// </summary>
    private string _StandardFailure(MoldSymbol mold, string message, SourceSpan span, params string[] more)
    {
        var type = new MoldType(mold);
        // message, where, puis les champs que l'erreur a en plus (item et target d'une CastError) : ceux qui ne sont pas donnés sont vides.
        var fields = new List<string> { message, _WhereString(span) };
        fields.AddRange(more);
        while (fields.Count < mold.Fields.Count) fields.Add("{}");
        return _FailWith($"{_Type(type)}{{{string.Join(", ", fields)}}}", type);
    }

    /// <summary>
    /// L'appel d'un guard dans un guard : sa valeur, ou l'échec immédiat de l'appelant avec la même
    /// erreur (expression-instruction <c>({ … })</c>).
    /// </summary>
    private string _PropagatingCall(IrCall call, Func<IrExpression, string> operand)
    {
        var text = _CallText(call, operand);
        if (call.Gear.Errors is not { } errors)
            return call.Type == PrimitiveType.Void ? $"static_cast<void>({text})" : $"{text}.value()";

        _usesStatementExpressions = true;
        var result = $"rgl_t{++_temporaryCount}";
        // Une valeur à cycle de vie sort du résultat par déplacement : elle ne se copie pas.
        var owning = LifecycleRules.Has(call.Type);
        return $"({{ {(owning ? "auto" : "const auto")} {result} = {text}; if ({result}.failed()) return {_FailWith($"{result}.error()", errors)};"
            + (call.Type == PrimitiveType.Void ? " })" : owning ? $" std::move({result}).value(); }})" : $" {result}.value(); }})");
    }

    /// <summary>L'endroit d'un élément dans le source RGL, en chaîne RGL : le <c>where</c> d'une erreur.</summary>
    private string _WhereString(SourceSpan span)
    {
        var position = span.StartPosition;
        return _String(new IrStringConstant(span, PrimitiveType.String, $"{Path.GetFileName(span.Source.Path)}:{span.Source.OriginalLine(position.Line)}:{position.Column}"));
    }

    /// <summary>L'endroit d'un élément dans le source RGL, en littéral C++ : <c>"main.rgl:3:5"</c>.</summary>
    private string _Where(SourceSpan span)
    {
        var position = span.StartPosition;
        return _Quote($"{Path.GetFileName(span.Source.Path)}:{span.Source.OriginalLine(position.Line)}:{position.Column}");
    }
}
