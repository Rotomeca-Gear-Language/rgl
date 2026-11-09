using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// La vivacité des champs de mold sur tout le programme, pour ranger les colonnes d'un tableau en SoA :
/// chaude (lue à chaque tour d'un parcours), tiède (lue dans un parcours, derrière une branche), froide (lue
/// seulement ailleurs) ou supprimée (jamais lue : aucune colonne, aucune allocation).
/// </summary>
internal sealed partial class CppEmitter
{
    /// <summary>La chaleur d'une colonne, de la plus froide à la plus chaude.</summary>
    private enum ColumnHeat
    {
        /// <summary>Lue hors des parcours seulement.</summary>
        Cold,

        /// <summary>Lue dans un parcours, derrière une branche.</summary>
        Warm,

        /// <summary>Lue à chaque tour d'un parcours.</summary>
        Hot,
    }

    /// <summary>La plus grande chaleur de chaque champ lu, par mold : chemin depuis le mold (<c>position.x</c>).</summary>
    private readonly Dictionary<(MoldSymbol Mold, string Path), ColumnHeat> _fieldHeat = [];

    /// <summary>
    /// Les molds dont une valeur entière est observée quelque part (comparée, écrite en texte, sérialisée, donnée à du C++) :
    /// aucun de leurs champs ne peut être supprimé, puisque ce qu'on observe les contient tous.
    /// </summary>
    private readonly HashSet<MoldSymbol> _observedMolds = [];

    /// <summary>Note qu'un champ est lu ; garde la plus grande chaleur.</summary>
    private void _NoteRead(MoldSymbol mold, string path, ColumnHeat heat)
    {
        var key = (mold, path);
        if (!_fieldHeat.TryGetValue(key, out var known) || known < heat) _fieldHeat[key] = heat;
    }

    /// <summary>La plus grande chaleur des lectures liées à cette colonne (le même chemin, un champ qui la contient ou qu'elle contient) ; <see langword="null"/> si rien ne la lit.</summary>
    private ColumnHeat? _HeatOf(MoldSymbol mold, string path)
    {
        ColumnHeat? best = null;
        foreach (var ((owner, read), heat) in _fieldHeat)
        {
            if (owner != mold) continue;
            if (read == path || path.StartsWith(read + ".", StringComparison.Ordinal) || read.StartsWith(path + ".", StringComparison.Ordinal))
                if (best is null || best < heat) best = heat;
        }
        return best;
    }

    /// <summary>Indique si la colonne <paramref name="path"/> de <paramref name="mold"/> se supprime : jamais lue, jamais observée en entier, et d'un type qui se construit à vide.</summary>
    private bool _IsDropped(MoldSymbol mold, string path, IrType type) =>
        !_observedMolds.Contains(mold) && _DropsCleanly(type) && _HeatOf(mold, path) is null;

    /// <summary>Un champ supprimé se reconstruit à vide à la lecture d'un élément : seuls les types que C++ construit sans argument le permettent.</summary>
    private static bool _DropsCleanly(IrType type) => _Representation(type) switch
    {
        PrimitiveType { Kind: PrimitiveKind.Byte or PrimitiveKind.UByte or PrimitiveKind.Short or PrimitiveKind.UShort or PrimitiveKind.Int or PrimitiveKind.UInt
            or PrimitiveKind.Long or PrimitiveKind.ULong or PrimitiveKind.Float or PrimitiveKind.Double or PrimitiveKind.Bool or PrimitiveKind.String } => true,
        EnumType => true,
        _ => false,
    };

    /// <summary>Calcule la vivacité des champs de tout le programme.</summary>
    private void _ComputeFieldLiveness(IrProgram program)
    {
        var bodies = program.Gears.ToDictionary(g => g.Symbol, g => g.Body);
        var each = new List<IrEach>();

        void Note(IrType type) => _ObserveWhole(type);

        void Expression(IrExpression expression, bool observed)
        {
            if (observed) Note(expression.Type);

            switch (expression)
            {
                case IrMemberAccess access:
                    _NoteFieldAccess(access, ColumnHeat.Cold);
                    break;
                case IrSafeAccess safe:
                    foreach (var mold in _ContainedMolds(safe.Target.Type)) _NoteRead(mold, safe.Field.Name, ColumnHeat.Cold);
                    break;
                case IrUnionField union:
                    foreach (var mold in _ContainedMolds(union.Target.Type)) foreach (var field in union.Fields) _NoteRead(mold, field.Name, ColumnHeat.Cold);
                    break;
                case IrEach loop:
                    each.Add(loop);
                    break;
            }

            var children = IrEffects.Children(expression).ToArray();
            for (var i = 0; i < children.Length; i++) Expression(children[i], _ObservesChild(expression, children[i]));

            switch (expression)
            {
                case IrLambda lambda:
                    Statement(lambda.Body);
                    break;
                case IrEach loop:
                    foreach (var rail in loop.Stages.Select(s => s.Rail).Append(loop.Fold?.Rail).OfType<IrPath>()) Path(rail);
                    break;
                case IrMatch match:
                    foreach (var arm in match.Arms.Where(a => a.Body is not null)) Statement(arm.Body!);
                    break;
            }
        }

        void Path(IrPath path)
        {
            foreach (var root in IrWalk.PathRoots(path)) Expression(root, false);
        }

        void Statement(IrStatement statement)
        {
            switch (statement)
            {
                case IrBlock block:
                    foreach (var inner in block.Statements) Statement(inner);
                    break;
                case IrIf ifStatement:
                    Expression(ifStatement.Condition, false);
                    Statement(ifStatement.Then);
                    if (ifStatement.Else is not null) Statement(ifStatement.Else);
                    break;
                case IrSwitch switchStatement:
                    Expression(switchStatement.Subject, true);
                    foreach (var section in switchStatement.Sections)
                    {
                        if (section.Test is not null) Expression(section.Test, true);
                        Statement(section.Body);
                    }
                    break;
                case IrVariableDeclaration declaration:
                    Expression(declaration.Value, false);
                    break;
                case IrAssignment assignment:
                    Expression(assignment.Value, false);
                    break;
                case IrContentAssignment assignment:
                    Expression(assignment.Target, false);
                    Expression(assignment.Value, false);
                    break;
                case IrReturn { Value: { } value }:
                    Expression(value, false);
                    break;
                case IrFailStatement fail:
                    Expression(fail.Error, false);
                    break;
                case IrExpressionStatement expressionStatement:
                    Expression(expressionStatement.Expression, false);
                    break;
                case IrTrainBody body:
                    if (body.StartValue is { } start) Expression(start, false);
                    Path(body.Main);
                    break;
                case IrEntryTrain train:
                    if (train.StartValue is { } entryStart) Expression(entryStart, false);
                    Path(train.Main);
                    break;
                default:
                    // Toute autre instruction (écriture, panique…) regarde ses valeurs en entier.
                    foreach (var root in IrWalk.Own(statement)) Expression(root, true);
                    break;
            }
        }

        foreach (var gear in program.Gears)
        {
            Statement(gear.Body);
            foreach (var given in gear.Defaults ?? []) Expression(given.Value, false);
            foreach (var tap in gear.Taps ?? []) Statement(tap.Statement);
        }
        foreach (var statement in program.Statements) Statement(statement);
        foreach (var fallback in program.FieldDefaults.Values) Expression(fallback, false);

        // Les champs lus dans une étape d'un parcours sont chauds, ou tièdes derrière une branche.
        foreach (var loop in each)
        {
            var visited = new HashSet<(GearSymbol, bool)>();
            foreach (var stage in loop.Stages) _ReadFields(stage.Apply, bodies, visited, false);
            if (loop.Fold is { } fold) _ReadFields(fold.Apply, bodies, visited, false);
        }
    }

    /// <summary>Note la lecture d'un champ, chemin complet depuis le mold de la valeur lue.</summary>
    private void _NoteFieldAccess(IrMemberAccess access, ColumnHeat heat)
    {
        var path = new List<string>();
        IrExpression target = access;
        while (target is IrMemberAccess inner)
        {
            path.Insert(0, inner.Field.Name);
            target = inner.Target;
        }
        if (_Representation(target.Type) is MoldType { Symbol: var mold }) _NoteRead(mold, string.Join('.', path), heat);
    }

    /// <summary>Marque comme observés entiers les molds que ce type contient, et ceux qu'ils contiennent.</summary>
    private void _ObserveWhole(IrType type)
    {
        var pending = new Stack<MoldSymbol>(_ContainedMolds(type));
        while (pending.Count > 0)
        {
            var mold = pending.Pop();
            if (!_observedMolds.Add(mold)) continue;
            foreach (var inner in mold.Fields.SelectMany(f => _ContainedMolds(f.Type))) pending.Push(inner);
        }
    }

    /// <summary>
    /// Indique si <paramref name="parent"/> observe <paramref name="child"/> en entier. Ce qui ne fait que
    /// transmettre une valeur (un argument, un retour, une conversion), la lire par un champ ou un index, ou la
    /// ranger ne compte pas : l'observation se verra là où la valeur arrive. Tout le reste observe, y compris ce
    /// qu'on ajoutera plus tard à l'IR : le doute garde le champ.
    /// </summary>
    private static bool _ObservesChild(IrExpression parent, IrExpression child) => parent switch
    {
        IrCall { Gear.IsNative: true } => true,
        IrCall or IrInvoke => false,
        IrMemberAccess or IrSafeAccess or IrUnionField or IrTupleItem or IrIndex or IrLength or IrRowCount or IrPoolValue or IrContains or IrDictionaryParts => false,
        IrEach => false,
        IrConversion or IrMove or IrRetype or IrNonNull or IrToUnion or IrUnionWiden or IrUnionConvert or IrResultValue or IrBoundCheck or IrArrayConvert => false,
        IrCoalesce or IrEmptyCoalesce or IrConditional => false,
        IrArrayValue or IrDictionaryValue or IrTupleValue or IrMoldValue or IrWith => false,
        IrPush or IrAdd or IrDictionaryPut or IrDictionaryWithout => false,
        IrAwait or IrTaskCall or IrBlockOn or IrWhenAny => false,
        IrMatch match => ReferenceEquals(child, match.Subject),
        IrLambda or IrGearReference => false,
        IrBinary { Operator: IrBinaryOperator.And or IrBinaryOperator.Or } => false,
        IrUnary or IrNumericCast or IrDurationMilliseconds or IrDurationOf => false,
        _ => true,
    };

    /// <summary>Note les champs lus par <paramref name="expression"/> dans un parcours, gears appelés et lambdas compris.</summary>
    private void _ReadFields(IrExpression expression, IReadOnlyDictionary<GearSymbol, IrBlock> bodies, HashSet<(GearSymbol, bool)> visited, bool conditional)
    {
        var heat = conditional ? ColumnHeat.Warm : ColumnHeat.Hot;

        void Children(bool branch)
        {
            foreach (var child in IrEffects.Children(expression)) _ReadFields(child, bodies, visited, branch);
        }

        switch (expression)
        {
            case IrMemberAccess access:
                _NoteFieldAccess(access, heat);
                _ReadFields(access.Target, bodies, visited, conditional);
                return;
            case IrSafeAccess safe:
                foreach (var mold in _ContainedMolds(safe.Target.Type)) _NoteRead(mold, safe.Field.Name, heat);
                break;
            case IrUnionField union:
                foreach (var mold in _ContainedMolds(union.Target.Type)) foreach (var field in union.Fields) _NoteRead(mold, field.Name, heat);
                break;
            case IrConditional conditionalExpression:
                _ReadFields(conditionalExpression.Condition, bodies, visited, conditional);
                _ReadFields(conditionalExpression.WhenTrue, bodies, visited, true);
                _ReadFields(conditionalExpression.WhenFalse, bodies, visited, true);
                return;
            case IrBinary { Operator: IrBinaryOperator.And or IrBinaryOperator.Or } logical:
                _ReadFields(logical.Left, bodies, visited, conditional);
                _ReadFields(logical.Right, bodies, visited, true);
                return;
            case IrCoalesce coalesce:
                _ReadFields(coalesce.Left, bodies, visited, conditional);
                _ReadFields(coalesce.Right, bodies, visited, true);
                return;
            case IrEmptyCoalesce coalesce:
                _ReadFields(coalesce.Left, bodies, visited, conditional);
                _ReadFields(coalesce.Right, bodies, visited, true);
                return;
            case IrMatch match:
                _ReadFields(match.Subject, bodies, visited, conditional);
                foreach (var arm in match.Arms)
                {
                    if (arm.Test is not null) _ReadFields(arm.Test, bodies, visited, true);
                    if (arm.Value is not null) _ReadFields(arm.Value, bodies, visited, true);
                    if (arm.Body is not null) _ReadStatement(arm.Body, bodies, visited, true);
                }
                return;
            case IrCall call:
                if (bodies.TryGetValue(call.Gear, out var body) && visited.Add((call.Gear, conditional))) _ReadStatement(body, bodies, visited, conditional);
                break;
            case IrLambda lambda:
                _ReadStatement(lambda.Body, bodies, visited, conditional);
                break;
        }
        Children(conditional);
    }

    /// <summary>Note les champs lus par une instruction d'un corps appelé dans un parcours : ce qui est derrière une branche est tiède.</summary>
    private void _ReadStatement(IrStatement statement, IReadOnlyDictionary<GearSymbol, IrBlock> bodies, HashSet<(GearSymbol, bool)> visited, bool conditional)
    {
        void Expression(IrExpression expression, bool branch) => _ReadFields(expression, bodies, visited, branch);

        switch (statement)
        {
            case IrBlock block:
                foreach (var inner in block.Statements) _ReadStatement(inner, bodies, visited, conditional);
                break;
            case IrIf ifStatement:
                Expression(ifStatement.Condition, conditional);
                _ReadStatement(ifStatement.Then, bodies, visited, true);
                if (ifStatement.Else is not null) _ReadStatement(ifStatement.Else, bodies, visited, true);
                break;
            case IrSwitch switchStatement:
                Expression(switchStatement.Subject, conditional);
                foreach (var section in switchStatement.Sections)
                {
                    if (section.Test is not null) Expression(section.Test, true);
                    _ReadStatement(section.Body, bodies, visited, true);
                }
                break;
            default:
                foreach (var root in IrWalk.Own(statement)) Expression(root, conditional);
                break;
        }
    }
}
