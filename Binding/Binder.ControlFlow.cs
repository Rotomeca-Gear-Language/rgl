using Rgl.AST.Expressions;
using Rgl.AST.Statements;
using Rgl.Diagnostics;
using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Types;
using Rgl.Syntax;
using Rgl.Text;
using Rotomeca.Core.Collections;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    /// <summary>Un switch en cours de liaison : de quoi résoudre ses <c>break</c>, <c>goto</c> et <c>$</c>.</summary>
    /// <param name="Id">Son numéro.</param>
    /// <param name="Type">Le type de sa valeur.</param>
    private sealed record SwitchContext(int Id, IrType Type)
    {
        /// <summary>Valeur connue de chaque <c>case</c> constant et rang de son cas.</summary>
        public Dictionary<object, int> Cases { get; } = [];

        /// <summary>Vrai si le switch a au moins un <c>case eval</c>.</summary>
        public bool HasEval { get; set; }
    }

    /// <summary>Les switch englobants, du plus extérieur au plus intérieur.</summary>
    private readonly List<SwitchContext> _switches = [];

    /// <summary>Le switch dont <c>$</c> désigne la valeur : défini seulement pendant la condition d'un <c>case eval</c>.</summary>
    private SwitchContext? _dollar;

    private int _switchCount;

    /// <summary><c>if</c> : la condition est un bool (E040) ; chaque branche a sa portée.</summary>
    private IrStatement? _BindIf(IfStatement ifStatement)
    {
        // Une condition faite de tests de type (T is int) est connue : seule la branche prise est liée, l'autre peut ne pas convenir à ce type.
        if (_IsStaticCondition(ifStatement.Condition))
        {
            if (_StaticCondition(ifStatement.Condition) is not { } taken) return null;
            var branch = taken ? ifStatement.Then : ifStatement.Else;
            return branch is null ? new IrBlock(ifStatement.Span, []) : _BindStatement(branch);
        }

        var condition = _BindExpression(ifStatement.Condition);
        if (condition is not null && condition.Type != PrimitiveType.Bool)
        {
            _Error(condition.Span, DiagnosticCodes.TypeMismatch, $"la condition d'un if est un bool, pas un « {condition.Type} »", $"de type « {condition.Type} »",
                "par exemple : if (n < 0) { … }");
            condition = null;
        }

        // Ce que la condition apprend sur les nullables vaut dans chaque branche ; si une branche ne
        // finit jamais normalement, ce qu'apprend l'autre vaut aussi après le if.
        var facts = _NullFacts(condition);
        var then = _WithNarrowings(facts.WhenTrue, () => _BindBlock(ifStatement.Then));
        var otherwise = ifStatement.Else is null ? null : _WithNarrowings(facts.WhenFalse, () => _BindStatement(ifStatement.Else));

        // Sauf pour une variable modifiée dans la branche qui, elle, continue après le if.
        void NarrowAfter(IReadOnlyList<(object Symbol, IrType Type)> learned, AST.Interfaces.IStatement? continuing)
        {
            var assigned = continuing is null ? [] : _AssignedNames(continuing).ToHashSet(StringComparer.Ordinal);
            foreach (var (symbol, type) in learned)
            {
                if (symbol is not IR.Symbols.VariableSymbol variable || !assigned.Contains(variable.Name)) _scope.Narrow(symbol, type);
            }
        }

        if (!ControlFlow.CanCompleteNormally(then)) NarrowAfter(facts.WhenFalse, ifStatement.Else);
        if (otherwise is not null && !ControlFlow.CanCompleteNormally(otherwise)) NarrowAfter(facts.WhenTrue, ifStatement.Then);

        return condition is null ? null : new IrIf(ifStatement.Span, condition, then, otherwise);
    }

    /// <summary>
    /// <c>switch</c> : sa valeur est pure (E050) ; les valeurs de ses <c>case</c> sont des
    /// constantes (E051), comparables à elle, sans doublon ni second <c>default</c> (E052). Les
    /// libellés sont liés d'abord, pour qu'un <c>goto</c> puisse viser un cas écrit plus bas. Un cas
    /// non vide qui ne finit ni par <c>break</c> ni par <c>goto</c> continue dans le suivant (W003).
    /// </summary>
    private IrStatement? _BindSwitch(SwitchStatement switchStatement)
    {
        _WidenAssignedIn(switchStatement);
        var subject = _BindExpression(switchStatement.Subject);
        if (subject is not null && IrEffects.HasEffects(subject))
        {
            _Error(subject.Span, DiagnosticCodes.ImpureSwitchValue, "la valeur d'un switch doit être pure : elle modifie une variable", "modifie une variable",
                "calcule-la d'abord dans une variable, puis fais le switch sur cette variable");
            subject = null;
        }

        var context = new SwitchContext(++_switchCount, subject?.Type ?? PrimitiveType.Bool);
        var failed = subject is null;

        // Les libellés d'abord.
        var tests = new IrExpression?[switchStatement.Sections.Count];
        var hasDefault = false;
        for (var i = 0; i < switchStatement.Sections.Count; ++i)
        {
            var section = switchStatement.Sections[i];
            switch (section.Kind)
            {
                case SwitchLabelKind.Default:
                    if (hasDefault)
                    {
                        _Error(section.LabelSpan, DiagnosticCodes.DuplicateCase, "second default dans le switch", "en double",
                            "un switch n'a qu'un seul default");
                        failed = true;
                    }
                    hasDefault = true;
                    break;

                case SwitchLabelKind.Eval:
                    context.HasEval = true;
                    if (subject is not null) tests[i] = _BindEvalCondition(section, context);
                    failed |= tests[i] is null;
                    break;

                case SwitchLabelKind.Values:
                    if (subject is not null) tests[i] = _BindCaseValues(section, context, i);
                    failed |= tests[i] is null;
                    break;
            }
        }

        var coversAll = subject is not null && !failed && _CheckEnumCoverage(switchStatement, subject.Type, context.Cases.Keys, hasDefault);

        // Puis les corps, chacun dans sa portée, avec ce switch comme cible de break et goto.
        _switches.Add(context);
        RArray<IrSwitchSection> sections = new();
        for (var i = 0; i < switchStatement.Sections.Count; ++i)
        {
            var section = switchStatement.Sections[i];
            var body = _BindStatements(section.Span, section.Body);
            sections.Push(new IrSwitchSection(section.Span, tests[i], body));

            var isLast = i == switchStatement.Sections.Count - 1;
            if (!isLast && section.Body.Count > 0 && !_LeavesCase(section.Body[^1]))
                _diagnostics.Warning(section.LabelSpan, DiagnosticCodes.SwitchFallthrough,
                    "ce cas ne finit ni par break ni par goto : il continue dans le cas suivant", "continue dans le cas suivant",
                    ["ajoute « break; » à la fin du cas pour sortir du switch",
                     "pour enchaîner volontairement, termine le cas par « goto » vers le cas suivant"]);
        }
        _switches.RemoveAt(_switches.Count - 1);

        return failed ? null : new IrSwitch(switchStatement.Span, context.Id, subject!, [.. sections], coversAll);
    }

    /// <summary>
    /// Indique si une instruction finit toujours par quitter le cas : <c>break</c>, <c>goto</c>,
    /// <c>return</c>, <c>fail</c>, <c>done</c>, un bloc qui finit ainsi, ou un <c>if</c> avec <c>else</c> dont toutes les branches finissent
    /// ainsi. Un cas qui finit autrement continue dans le suivant (W003).
    /// </summary>
    private static bool _LeavesCase(Rgl.AST.Interfaces.IStatement statement) => statement switch
    {
        BreakStatement or GotoStatement or ReturnStatement or FailStatement or PanicStatement or DoneStatement => true,
        BlockStatement block => block.Statements.Count > 0 && _LeavesCase(block.Statements[^1]),
        IfStatement ifStatement => ifStatement.Else is not null && _LeavesCase(ifStatement.Then) && _LeavesCase(ifStatement.Else),
        _ => false,
    };

    /// <summary>Le test d'un <c>case v1, v2:</c> : <c>$ eq v1 or $ eq v2</c>, chaque valeur constante et unique.</summary>
    private IrExpression? _BindCaseValues(SwitchSection section, SwitchContext context, int index)
    {
        IrExpression? test = null;
        var failed = false;

        foreach (var value in section.Values)
        {
            var bound = _BindCaseValue(value, context.Type);
            if (bound is null)
            {
                failed = true;
                continue;
            }

            var comparison = _BindOperation(value.Span, value.Span, TokenKind.EqKeyword, new IrSwitchValue(value.Span, context.Type, context.Id), bound);
            if (comparison is not IrBinary { Right: var compared })
            {
                failed = true;
                continue;
            }

            if (ConstantFolder.Fold(compared) is not { } constant)
            {
                _Error(value.Span, DiagnosticCodes.NonConstantCase, "la valeur d'un case doit être connue à la compilation", "calculée à l'exécution",
                    "utilise un littéral ou une constante (const), ou teste une condition avec : case eval …");
                failed = true;
                continue;
            }

            if (!context.Cases.TryAdd(constant, index))
            {
                _Error(value.Span, DiagnosticCodes.DuplicateCase, $"le cas {value.Span.Text} existe déjà dans ce switch", "en double",
                    "chaque valeur n'apparaît qu'une fois : le second cas ne serait jamais atteint");
                failed = true;
                continue;
            }

            test = test is null ? comparison : new IrBinary(section.LabelSpan, PrimitiveType.Bool, IrBinaryOperator.Or, test, comparison, section.LabelSpan);
        }

        return failed ? null : test;
    }

    /// <summary>La condition d'un <c>case eval</c>, un bool, où <c>$</c> désigne la valeur du switch.</summary>
    private IrExpression? _BindEvalCondition(SwitchSection section, SwitchContext context)
    {
        var previous = _dollar;
        _dollar = context;
        var condition = _BindExpression(section.Condition!);
        _dollar = previous;

        if (condition is null || condition.Type == PrimitiveType.Bool) return condition;

        _Error(condition.Span, DiagnosticCodes.TypeMismatch, $"la condition d'un case eval est un bool, pas un « {condition.Type} »", $"de type « {condition.Type} »",
            "par exemple : case eval $ > 10:");
        return null;
    }

    /// <summary><c>$</c> : la valeur du switch, seulement dans la condition d'un <c>case eval</c> (E048).</summary>
    private IrExpression? _BindDollar(DollarExpression dollar)
    {
        // Dans un @tap, $ est la valeur du gear (après) ou son premier paramètre (avant).
        if (_tapValue is { } tapped) return tapped;
        if (_dollar is { } context) return new IrSwitchValue(dollar.Span, context.Type, context.Id);

        _Error(dollar.Span, DiagnosticCodes.DollarOutsideEval, "« $ » n'a de sens que dans un case eval", "hors d'un case eval",
            "dans un switch, « case eval $ > 10: » teste la valeur du switch");
        return null;
    }

    /// <summary>
    /// <c>break</c> sort du switch courant, <c>break N</c> aussi des N switch englobants, <c>break all</c>
    /// de tous. Hors d'un switch : E049 ; N trop grand : W002, et il sort de tous.
    /// </summary>
    private IrStatement? _BindBreak(BreakStatement breakStatement)
    {
        if (_switches.Count == 0)
        {
            _Error(breakStatement.Span, DiagnosticCodes.JumpOutsideSwitch, "break hors d'un switch", "aucun switch à quitter",
                "break sort d'un switch ; il n'y a pas de boucle en RGL");
            return null;
        }

        var levels = 1;
        if (breakStatement.All) levels = _switches.Count;
        else if (breakStatement.Depth is { } depth)
        {
            var value = LiteralDecoder.Integer(depth.Text);
            if (value + 1 > _switches.Count)
            {
                _diagnostics.Warning(depth.Span, DiagnosticCodes.BreakTooDeep,
                    $"break {depth.Text} dépasse les {_switches.Count} switch englobant(s) : il sort de tous", "trop profond",
                    [_switches.Count == 1 ? "un seul switch englobe ce break : écris simplement « break; »" : $"au plus « break {_switches.Count - 1}; », ou « break all; »"]);
                levels = _switches.Count;
            }
            else levels = (int)value + 1;
        }

        return new IrBreak(breakStatement.Span, _switches[^levels].Id);
    }

    /// <summary>
    /// <c>goto v</c> : saute au cas de valeur v du switch courant. v est une constante (E051) ;
    /// aucun cas ne la porte : E008 si le switch a des <c>case eval</c> (on ne vise pas une
    /// condition), E007 sinon.
    /// </summary>
    private IrStatement? _BindGoto(GotoStatement gotoStatement)
    {
        if (_boundedLoops)
        {
            _Error(gotoStatement.Span, DiagnosticCodes.UnboundedLoop, "goto saute à un autre cas : il peut former une boucle sans limite", "goto interdit",
                "avec la clé « loop » de rgl.json à « safe », écris un parcours ou une boucle de guard bornée plutôt qu'un goto");
            return null;
        }

        if (_switches.Count == 0)
        {
            _Error(gotoStatement.Span, DiagnosticCodes.JumpOutsideSwitch, "goto hors d'un switch", "aucun switch",
                "goto saute à un cas du switch qui le contient");
            return null;
        }

        var context = _switches[^1];
        var target = _BindCaseValue(gotoStatement.Target, context.Type);
        if (target is null) return null;

        var converted = _Coerce(target, context.Type);
        if (converted is null || ConstantFolder.Fold(converted) is not { } value)
        {
            _Error(target.Span, DiagnosticCodes.NonConstantCase, "goto vise un cas par sa valeur, connue à la compilation", "pas une valeur de cas",
                $"par exemple : goto 1; (la valeur du switch est un « {context.Type} »)");
            return null;
        }

        if (context.Cases.TryGetValue(value, out var index)) return new IrGoto(gotoStatement.Span, context.Id, index);

        if (context.HasEval)
            _Error(target.Span, DiagnosticCodes.GotoEvalCase, $"aucun case {target.Span.Text} dans ce switch, et goto ne peut pas viser un case eval", "pas un cas constant",
                "goto ne vise que les cas « case v: » ; un case eval est une condition, pas une valeur");
        else
            _Error(target.Span, DiagnosticCodes.GotoUnknownCase, $"aucun case {target.Span.Text} dans ce switch", "cas inexistant",
                "goto vise un cas « case v: » du switch qui le contient");
        return null;
    }
}
