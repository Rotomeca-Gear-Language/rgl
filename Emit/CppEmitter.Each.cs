using System.Text;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les parcours <c>||&gt;</c> et <c>|!|&gt;</c> : une boucle C++ simple sur les index du tableau, où
/// chaque élément traverse toutes les étapes avant le suivant. Un élément raté par <c>||&gt;</c> passe
/// par son rail puis la boucle continue (<c>continue</c>) ; un échec de <c>|!|&gt;</c> quitte la boucle et
/// part sur le rail de l'étape, fait échouer le train (code 1) ou le guard englobant.
/// Un parcours dont la première étape modifie l'élément (paramètre <c>mut</c>) parcourt la variable
/// elle-même, par référence ; l'élément d'un tableau en colonnes est reconstruit, modifié, puis remis
/// à sa place avant que l'éventuel échec de l'étape ne soit traité.
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>Où un parcours est écrit : ce que devient un échec de <c>|!|&gt;</c>.</summary>
    private enum EachContext
    {
        /// <summary>Étape d'un train : rail de l'étape, ou échec du train.</summary>
        Train,

        /// <summary>Expression dans un guard : le guard échoue avec la même erreur.</summary>
        Guard,

        /// <summary>Expression ailleurs : aucun <c>|!|&gt;</c> n'y peut échouer (le binder l'a vérifié).</summary>
        Other,
    }

    /// <summary>Le C++ de l'élément courant du parcours en cours d'écriture.</summary>
    private string? _elementValue;

    /// <summary>Le C++ de l'accumulateur de l'accumulation en cours d'écriture.</summary>
    private string? _accumulatorValue;

    /// <summary>Le C++ du rang de l'élément courant du parcours en cours d'écriture.</summary>
    private string? _indexValue;

    /// <summary>La variable que <c>done valeur;</c> met à vrai pour arrêter l'accumulation en cours d'écriture ; <see langword="null"/> hors d'une lambda d'accumulation.</summary>
    private string? _foldStop;

    /// <summary>
    /// Un parcours dans une expression : une lambda appelée sur place qui renvoie le tableau obtenu,
    /// ou, dans un guard, une expression-instruction <c>({ … })</c>, dont un échec de <c>|!|&gt;</c>
    /// quitte le guard.
    /// </summary>
    private string _EachExpression(IrEach each)
    {
        var body = new StringBuilder();
        if (_UseStatementExpressions)
        {
            _usesStatementExpressions = true;
            var value = _EachLoop(body, each, _lambdaIndent, _InGuard ? EachContext.Guard : EachContext.Other);
            // La valeur d'une expression-instruction est copiée : le tableau obtenu, qui n'est plus lu, est déplacé.
            return $"({{\n{body}{_lambdaIndent}{(value is null ? "" : $"std::move({value});")} }})";
        }

        var result = _EachLoop(body, each, _lambdaIndent, EachContext.Other);
        var type = each.Type == PrimitiveType.Void ? "void" : _Type(each.Type);
        return $"[{(_atFileLevel ? "" : "&")}]() -> {type} {{\n{body}{_lambdaIndent}{(result is null ? "" : $"return {result}; ")}}}()";
    }

    /// <summary>
    /// La boucle d'un parcours, écrite en instructions dans <paramref name="output"/> : le tableau
    /// obtenu est déclaré avant elle, et son nom renvoyé ; <see langword="null"/> quand le parcours ne
    /// renvoie rien. Un tableau sans guard qui puisse écarter un élément réserve sa taille d'avance.
    /// Avec une accumulation, c'est l'accumulateur qui est déclaré et renvoyé, après la source pour
    /// garder l'ordre du source : aucun tableau n'est construit. Un intervalle parcouru ne l'est pas non plus.
    /// </summary>
    private string? _EachLoop(StringBuilder output, IrEach each, string indent, EachContext context)
    {
        var (savedElement, savedPipeline, savedIndex) = (_elementValue, _pipelineValue, _indexValue);
        var source = $"rgl_t{++_temporaryCount}";
        var index = $"rgl_i{++_temporaryCount}";
        _indexValue = index;
        var inner = indent + Indent;
        var body = inner + Indent;
        string? result = null;
        if (each.Fold is { } fold)
        {
            output.Append(indent).Append("const auto& ").Append(source).Append(" = ").Append(_EachSource(each.Source)).Append(";\n");
            result = $"rgl_t{++_temporaryCount}";
            output.Append(indent).Append("[[maybe_unused]] ").Append(_Type(fold.Type)).Append(' ').Append(result).Append(" = ").Append(_Expression(fold.Seed)).Append(";\n");
            output.Append(indent).Append("{\n");
        }
        else
        {
            if (each.Type != PrimitiveType.Void)
            {
                result = $"rgl_t{++_temporaryCount}";
                output.Append(indent).Append("[[maybe_unused]] ").Append(_Type(each.Type)).Append(' ').Append(result).Append(";\n");
            }
            output.Append(indent).Append("{\n");
            if (each.Modifies)
            {
                var place = _Place(each.Source);
                foreach (var line in place.Prelude) output.Append(inner).Append(line).Append('\n');
                output.Append(inner).Append("auto& ").Append(source).Append(" = ").Append(place.Access).Append(";\n");
            }
            else output.Append(inner).Append("const auto& ").Append(source).Append(" = ").Append(_EachSource(each.Source)).Append(";\n");
            // Au plus un élément par élément parcouru : la place est réservée d'un coup, même si un guard en écarte.
            if (result is not null)
                output.Append(inner).Append(result).Append(".reserve(").Append(source).Append(".size());\n");
        }
        // Vérification groupée : les étapes sont des lambdas sans effet, le débordement s'accumule dans un drapeau, testé une fois après la boucle.
        var savedGrouped = _groupedOverflow;
        string? groupedFlag = null;
        var groupedAt = output.Length;
        _groupedUsed = false;
        if (_overflow == OverflowMode.Checked && !_atFileLevel && each.Fold is null && !each.Modifies
            && each.Stages.All(s => s.Guard is null && s.Rail is null && s.Apply is IrInvoke { Target: IrLambda } && !IrEffects.HasEffects(s.Apply)))
        {
            groupedFlag = $"rgl_ov{++_temporaryCount}";
            _groupedOverflow = groupedFlag;
        }

        // Les colonnes d'un parcours de lignes (|S|>) sont lues par des pointeurs __restrict : deux colonnes ne se recouvrent jamais,
        // ce que Clang ne peut pas deviner et qui lui permet de vectoriser la boucle.
        if (each.Source is IrRange { End: IrRowCount rowCount })
            foreach (var column in rowCount.Columns)
            {
                var pointer = $"rgl_c{++_temporaryCount}";
                output.Append(inner).Append("[[maybe_unused]] const auto* __restrict ").Append(pointer).Append(" = (").Append(_Expression(column)).Append(").data();\n");
                _restrictColumns[column] = pointer;
            }
        output.Append(inner).Append("for (std::size_t ").Append(index).Append(" = 0; ").Append(index).Append(" < ").Append(source).Append(".size(); ++").Append(index).Append(") {\n");

        var element = $"rgl_t{++_temporaryCount}";
        // Un élément modifié sur place : une référence, ou, en colonnes, une copie remise à sa place après l'étape.
        var gathered = each.Modifies && _Representation(each.Source.Type) is ArrayType sourceArray && _IsSoa(sourceArray);
        var elementDeclaration = !each.Modifies ? "[[maybe_unused]] const auto& " : gathered ? "auto " : "auto& ";
        output.Append(body).Append(elementDeclaration).Append(element).Append(" = ").Append(source).Append('[').Append(index).Append("];\n");
        var putBack = gathered ? _PutBack(each.Stages[0], _SoaMold((ArrayType)_Representation(each.Source.Type)), source, index, element) : null;
        // Un élément produit par une étape appartient à la boucle : il entre dans le tableau obtenu déplacé, sans copie.
        var owned = false;

        foreach (var stage in each.Stages)
        {
            _elementValue = element;
            var stagePutBack = stage.Modifies ? putBack : null;
            if (stage.Guard is { } guard)
            {
                var call = (IrCall)stage.Apply;
                var outcome = $"rgl_t{++_temporaryCount}";
                output.Append(body).Append("[[maybe_unused]] auto ").Append(outcome).Append(" = ").Append(_CallText(call, _Expression)).Append(";\n");
                _PutBackLines(output, stagePutBack, body);
                if (guard.Errors is { } errors)
                {
                    output.Append(body).Append("if (").Append(outcome).Append(".failed()) {\n");
                    _EachFailure(output, stage.Rail, stage.IsStrict, outcome, errors, body + Indent, context);
                    output.Append(body).Append("}\n");
                }
                if (call.Type != PrimitiveType.Void)
                {
                    element = $"rgl_t{++_temporaryCount}";
                    output.Append(body).Append("[[maybe_unused]] auto& ").Append(element).Append(" = ").Append(outcome).Append(".value();\n");
                    owned = true;
                }
            }
            else if (stage.Output is null)
            {
                output.Append(body).Append(_Expression(stage.Apply)).Append(";\n");
                _PutBackLines(output, stagePutBack, body);
            }
            else
            {
                var value = _Expression(stage.Apply);
                element = $"rgl_t{++_temporaryCount}";
                // Une copie, pas une référence : l'élément modifié peut être remis à sa place, ou modifié encore.
                output.Append(body).Append("[[maybe_unused]] auto").Append(stagePutBack is null ? "&& " : " ").Append(element).Append(" = ").Append(value).Append(";\n");
                owned = true;
                _PutBackLines(output, stagePutBack, body);
            }
        }

        if (each.Fold is { } accumulation) _EachFold(output, accumulation, result!, element, body, context);
        else if (result is not null)
        {
            // La place est réservée à la taille de la source, et chaque élément ajoute au plus un résultat : le tableau ne grandit jamais.
            var reserved = owned && _Representation(each.Type) is ArrayType { Kind: ArrayKind.Dynamic } resultArray && !_IsSoa(resultArray) ? "push_reserved" : "push";
            output.Append(body).Append(result).Append('.').Append(reserved).Append('(').Append(owned ? $"std::move({element})" : element).Append(");\n");
        }
        output.Append(inner).Append("}\n");
        // Le drapeau n'existe que si une opération du corps l'a utilisé.
        if (groupedFlag is not null && _groupedUsed)
        {
            output.Insert(groupedAt, $"{inner}bool {groupedFlag} = false;\n");
            output.Append(inner).Append("rgl::standard::check_grouped_overflow(").Append(groupedFlag).Append(", ").Append(_Where(each.Span)).Append(");\n");
        }
        output.Append(indent).Append("}\n");

        _groupedOverflow = savedGrouped;
        (_elementValue, _pipelineValue, _indexValue) = (savedElement, savedPipeline, savedIndex);
        return result;
    }

    /// <summary>
    /// L'accumulation d'un élément : l'accumulateur reçoit le résultat de l'étape. Un guard qui échoue
    /// fait échouer toute l'accumulation, comme un <c>|!|&gt;</c> ; son <c>done</c> l'arrête avec la valeur
    /// qu'il renvoie (<c>break</c>).
    /// </summary>
    private void _EachFold(StringBuilder output, IrEachFold fold, string accumulator, string element, string indent, EachContext context)
    {
        var saved = _accumulatorValue;
        (_accumulatorValue, _elementValue) = (accumulator, element);
        if (fold.Guard is { } guard)
        {
            var call = (IrCall)fold.Apply;
            var outcome = $"rgl_t{++_temporaryCount}";
            output.Append(indent).Append("const auto ").Append(outcome).Append(" = ").Append(_CallText(call, _Expression)).Append(";\n");
            if (guard.Errors is { } errors)
            {
                output.Append(indent).Append("if (").Append(outcome).Append(".failed()) {\n");
                _EachFailure(output, fold.Rail, true, outcome, errors, indent + Indent, context);
                output.Append(indent).Append("}\n");
            }
            output.Append(indent).Append(accumulator).Append(" = ").Append(outcome).Append(".value();\n");
            output.Append(indent).Append("if (").Append(outcome).Append(".finished()) break;\n");
        }
        else if (fold.Apply is IrInvoke { Target: IrLambda lambda } && IrWalk.Statements(lambda.Body).OfType<IrReturn>().Any(r => r.StopsFold))
        {
            // done valeur; dans la lambda : elle met l'indicateur à vrai et renvoie la valeur, qui devient le résultat final.
            var stop = $"rgl_t{++_temporaryCount}";
            var savedStop = _foldStop;
            _foldStop = stop;
            output.Append(indent).Append("bool ").Append(stop).Append(" = false;\n");
            output.Append(indent).Append(accumulator).Append(" = ").Append(_Expression(fold.Apply)).Append(";\n");
            output.Append(indent).Append("if (").Append(stop).Append(") break;\n");
            _foldStop = savedStop;
        }
        else
            output.Append(indent).Append(accumulator).Append(" = ").Append(_Expression(fold.Apply)).Append(";\n");
        _accumulatorValue = saved;
    }

    /// <summary>Les lignes qui remettent un élément modifié à sa place, s'il y en a, à l'indentation donnée.</summary>
    private static void _PutBackLines(StringBuilder output, IReadOnlyList<string>? lines, string indent)
    {
        foreach (var line in lines ?? []) output.Append(indent).Append(line).Append('\n');
    }

    /// <summary>Ce que parcourt une boucle : un intervalle est lu par sa vue, sans construire le tableau.</summary>
    private string _EachSource(IrExpression source) => source is IrRange range ? _RangeView(range) : _Expression(source);

    /// <summary>
    /// Ce que fait un élément raté : avec <c>||&gt;</c>, son rail par élément (dont <c>done</c> passe à
    /// l'élément suivant) puis l'élément suivant ; avec <c>|!|&gt;</c> ou une accumulation, le rail de
    /// l'étape, l'échec du train (code 1), ou celui du guard englobant avec la même erreur.
    /// </summary>
    private void _EachFailure(StringBuilder output, IrPath? rail, bool isStrict, string outcome, IrType errors, string indent, EachContext context)
    {
        if (rail is not null)
        {
            _pipelineValue = $"{outcome}.error()";
            _Path(output, rail, indent, perElement: !isStrict);
            return;
        }

        if (!isStrict)
        {
            output.Append(indent).Append("continue;\n");
            return;
        }

        output.Append(indent).Append(context switch
        {
            EachContext.Train => $"return {_TrainFailure($"{outcome}.error()", errors)};\n",
            EachContext.Guard => $"return {_FailWith($"{outcome}.error()", errors)};\n",
            _ => throw new InvalidOperationException("« |!|> » avec un guard hors d'un train et d'un guard : le binder l'aurait refusé."),
        });
    }
}
