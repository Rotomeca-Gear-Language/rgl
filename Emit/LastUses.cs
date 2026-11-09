using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Types;

namespace Rgl.Emit;

/// <summary>
/// Les lectures d'une variable qui peuvent la déplacer au lieu de la copier : la valeur d'une
/// déclaration, d'une affectation ou d'un <c>push</c> qui est la variable seule, quand rien ne la relit
/// ensuite. Une affectation copie toujours en RGL ; déplacer ne change rien au sens, puisque la source
/// n'est plus lue.
/// </summary>
/// <remarks>
/// L'analyse est prudente : la lecture doit être la dernière mention de la variable dans l'ordre du
/// source, dans le corps qui la déclare, hors d'une boucle (étape de parcours, switch qui contient un
/// <c>goto</c>), hors d'un bras de match (qui peut garder une référence sur sa valeur), et la variable
/// ne doit pas être capturée par une lambda. Seules les valeurs qui possèdent de la mémoire (tableaux,
/// dictionnaires, chaînes, lambdas, et ce qui en contient) en profitent.
/// </remarks>
internal static class LastUses
{
    /// <summary>
    /// Les lectures qui déplacent (<see cref="Moves"/>) et les tableaux étalés en tête d'un tableau écrit qu'on peut reprendre tels quels
    /// (<see cref="Spreads"/>), dans les corps des gears et de toutes leurs lambdas.
    /// </summary>
    public sealed record Result(HashSet<IrVariableReference> Moves, HashSet<IrExpression> Spreads);

    /// <summary>Les lectures qui déplacent et les tableaux étalés repris, dans tout le programme.</summary>
    public static Result Of(IrProgram program)
    {
        var moves = new HashSet<IrVariableReference>(ReferenceEqualityComparer.Instance);
        var spreads = new HashSet<IrExpression>(ReferenceEqualityComparer.Instance);
        var bodies = new Queue<(IrBlock Body, ParameterSymbol? Owned)>(program.Gears.Select(g => (g.Body, (ParameterSymbol?)null)));
        while (bodies.Count > 0)
        {
            var (body, owned) = bodies.Dequeue();
            new Walker(moves, spreads, bodies, owned).Run(body);
        }
        return new Result(moves, spreads);
    }

    /// <summary>
    /// Vrai si l'étape d'une accumulation reçoit l'accumulateur par valeur, déplacé : c'est une lambda écrite sur place dont le premier
    /// paramètre, non <c>mut</c>, possède de la mémoire. Le même test sert à l'émetteur, qui écrit alors le paramètre par valeur.
    /// </summary>
    public static bool ConsumesAccumulator(IrLambda lambda) =>
        lambda.Parameters is [{ IsMutable: false } first, ..] && IsWorthMoving(first.Type);

    /// <summary>
    /// Indique si déplacer une valeur de ce type évite une copie coûteuse : elle possède de la mémoire
    /// sur le tas, ou contient une telle valeur.
    /// </summary>
    public static bool IsWorthMoving(IrType type) => type switch
    {
        ArrayType or DictionaryType or FunctionType => true,
        PrimitiveType { Kind: PrimitiveKind.String or PrimitiveKind.MString } => true,
        MoldType mold => mold.Symbol.Fields.Any(f => IsWorthMoving(f.Type)),
        TupleType tuple => tuple.Elements.Any(IsWorthMoving),
        NullableType nullable => IsWorthMoving(nullable.Inner),
        UnionType union => union.Members.Any(IsWorthMoving),
        ResultType result => IsWorthMoving(result.Value) || IsWorthMoving(result.Error),
        IngotType ingot => IsWorthMoving(ingot.Base),
        _ => false,
    };

    /// <summary>Le parcours d'un corps : chaque mention de variable, dans l'ordre, et les lectures candidates.</summary>
    private sealed class Walker(HashSet<IrVariableReference> moves, HashSet<IrExpression> spreads, Queue<(IrBlock Body, ParameterSymbol? Owned)> bodies, ParameterSymbol? owned)
    {
        /// <summary>Position de la dernière mention de chaque variable, dans l'ordre du parcours.</summary>
        private readonly Dictionary<VariableSymbol, int> _last = [];

        /// <summary>Les variables qu'une boucle, un bras de match ou une lambda mentionne : jamais déplacées.</summary>
        private readonly HashSet<object> _pinned = [];

        /// <summary>Chaque mention d'une variable ou d'un paramètre, avec le chemin de champs lu (vide pour la valeur entière), dans l'ordre.</summary>
        private readonly List<(object Root, string[] Path, int Position)> _mentions = [];

        /// <summary>Les tableaux étalés en tête d'un tableau écrit : l'expression, sa racine, son chemin et la position de sa mention.</summary>
        private readonly List<(IrExpression Value, object Root, string[] Path, int Position)> _spreadCandidates = [];

        /// <summary>Les lambdas d'accumulation qui reçoivent leur accumulateur par valeur, avec leur premier paramètre.</summary>
        private readonly Dictionary<IrLambda, ParameterSymbol> _owners = new(ReferenceEqualityComparer.Instance);

        /// <summary>Les variables déclarées dans ce corps.</summary>
        private readonly HashSet<VariableSymbol> _locals = [];

        /// <summary>Les lectures candidates, avec leur position.</summary>
        private readonly List<(IrVariableReference Reference, int Position)> _candidates = [];

        private int _position;

        /// <summary>Profondeur de boucles, de bras de match et de lambdas autour de la mention en cours.</summary>
        private int _repeated;

        public void Run(IrBlock body)
        {
            _Statement(body);
            foreach (var (reference, position) in _candidates)
            {
                var variable = reference.Variable;
                if (_locals.Contains(variable) && !variable.IsConstant && !_pinned.Contains(variable) && _last[variable] == position && IsWorthMoving(variable.Type))
                    moves.Add(reference);
            }

            foreach (var (value, root, path, position) in _spreadCandidates)
            {
                if (_pinned.Contains(root)) continue;
                var ownsIt = root switch
                {
                    VariableSymbol variable => _locals.Contains(variable) && !variable.IsConstant,
                    ParameterSymbol parameter => parameter == owned,
                    _ => false,
                };
                // Reprendre le tableau le vide : rien ne doit le relire ensuite, ni lui ni un chemin qui le contient ou qu'il contient.
                if (ownsIt && !_mentions.Any(m => m.Position > position && ReferenceEquals(m.Root, root) && _Overlaps(m.Path, path)))
                    spreads.Add(value);
            }
        }

        private static bool _Overlaps(string[] left, string[] right)
        {
            var common = Math.Min(left.Length, right.Length);
            for (var i = 0; i < common; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private void _Mention(VariableSymbol variable, string[]? path = null)
        {
            _last[variable] = ++_position;
            if (_repeated > 0) _pinned.Add(variable);
            _mentions.Add((variable, path ?? [], _position));
        }

        private void _MentionParameter(ParameterSymbol parameter, string[] path)
        {
            _mentions.Add((parameter, path, ++_position));
            if (_repeated > 0) _pinned.Add(parameter);
        }

        /// <summary>Une chaîne de champs et d'éléments de tuple lue depuis une variable ou un paramètre : sa racine et son chemin.</summary>
        private static bool _TryPlace(IrExpression expression, out object root, out string[] path)
        {
            var steps = new List<string>();
            while (true)
            {
                switch (expression)
                {
                    case IrTupleItem item:
                        steps.Add("t" + item.Position);
                        expression = item.Target;
                        continue;
                    case IrMemberAccess access:
                        steps.Add("f:" + access.Field.Name);
                        expression = access.Target;
                        continue;
                    case IrVariableReference reference:
                        (root, path) = (reference.Variable, [.. steps.AsEnumerable().Reverse()]);
                        return true;
                    case IrParameterReference parameter:
                        (root, path) = (parameter.Parameter, [.. steps.AsEnumerable().Reverse()]);
                        return true;
                    default:
                        (root, path) = (null!, []);
                        return false;
                }
            }
        }

        /// <summary>Une valeur qui peut être déplacée si elle est la variable seule.</summary>
        private void _Consumed(IrExpression value)
        {
            if (value is IrVariableReference reference && _repeated == 0)
            {
                _Mention(reference.Variable);
                _candidates.Add((reference, _position));
            }
            else _Expression(value);
        }

        private void _Statement(IrStatement statement)
        {
            switch (statement)
            {
                case IrVariableDeclaration declaration:
                    _Consumed(declaration.Value);
                    _locals.Add(declaration.Variable);
                    _Mention(declaration.Variable);
                    break;

                case IrAssignment assignment:
                    if (assignment.Value is IrVariableReference self && self.Variable == assignment.Variable) _Expression(self);
                    else _Consumed(assignment.Value);
                    _Mention(assignment.Variable);
                    break;

                case IrUndefine undefine:
                    _Mention(undefine.Variable);
                    break;

                case IrBlock block:
                    foreach (var inner in block.Statements) _Statement(inner);
                    break;

                case IrIf ifStatement:
                    _Expression(ifStatement.Condition);
                    _Statement(ifStatement.Then);
                    if (ifStatement.Else is { } otherwise) _Statement(otherwise);
                    break;

                case IrSwitch switchStatement:
                    // Un goto peut revenir à un cas déjà passé : tout le switch se répète alors.
                    var loops = IrWalk.Statements(switchStatement).OfType<IrGoto>().Any();
                    if (loops) ++_repeated;
                    _Expression(switchStatement.Subject);
                    foreach (var section in switchStatement.Sections)
                    {
                        if (section.Test is { } test) _Expression(test);
                        _Statement(section.Body);
                    }
                    if (loops) --_repeated;
                    break;

                case IrExpressionStatement { Expression: IrPush push }:
                    _Expression(push.Target);
                    _Consumed(push.Value);
                    break;

                case IrContentAssignment assignment:
                    _Expression(assignment.Target);
                    _Expression(assignment.Value);
                    break;

                case IrReturn { Value: { } value }:
                    _Expression(value);
                    break;

                case IrPanic panic:
                    _Expression(panic.Value);
                    break;

                case IrFailStatement fail:
                    _Expression(fail.Error);
                    break;

                case IrWrite write:
                    _Expression(write.Value);
                    break;

                case IrWriteFile write:
                    _Expression(write.Value);
                    _Expression(write.Path);
                    break;

                case IrWriteJson write:
                    _Expression(write.Value);
                    _Expression(write.Path);
                    break;

                case IrWriteXml write:
                    _Expression(write.Value);
                    _Expression(write.Path);
                    break;

                case IrExpressionStatement expression:
                    _Expression(expression.Expression);
                    break;
            }
        }

        private void _Expression(IrExpression expression)
        {
            switch (expression)
            {
                case IrVariableReference reference:
                    _Mention(reference.Variable);
                    return;

                case IrParameterReference parameter:
                    _MentionParameter(parameter.Parameter, []);
                    return;

                // Un champ ou un élément de tuple lu depuis une variable : la mention porte son chemin, pour qu'un autre champ du même objet
                // ne compte pas comme une relecture de celui-là.
                case IrTupleItem or IrMemberAccess when _TryPlace(expression, out var root, out var path):
                    if (root is VariableSymbol placed) _Mention(placed, path);
                    else _MentionParameter((ParameterSymbol)root, path);
                    return;

                // [...a, x, …] : si a est le premier morceau et n'est plus lu après, make_array_from reprend son bloc.
                case IrArrayValue { Parts: [{ IsSpread: true } first, ..] } when _repeated == 0 && _TryPlace(first.Value, out var spreadRoot, out var spreadPath):
                    _spreadCandidates.Add((first.Value, spreadRoot, spreadPath, _position + 1));
                    break;

                case IrIncrement increment:
                    _Mention(increment.Variable);
                    break;

                // Une lambda capture ce qu'elle lit : la variable reste lue après ; son corps est un autre corps.
                case IrLambda lambda:
                    ++_repeated;
                    foreach (var captured in _Captured(lambda.Body))
                    {
                        if (captured is VariableSymbol capturedVariable) _Mention(capturedVariable);
                        else _MentionParameter((ParameterSymbol)captured, []);
                    }
                    --_repeated;
                    bodies.Enqueue((lambda.Body, _owners.GetValueOrDefault(lambda)));
                    return;

                case IrEach each:
                    _Expression(each.Source);
                    ++_repeated;
                    foreach (var stage in each.Stages) _Expression(stage.Apply);
                    if (each.Fold is { Apply: IrInvoke { Target: IrLambda consumer, Arguments: [IrAccumulatorValue, ..] } } && ConsumesAccumulator(consumer))
                        _owners[consumer] = consumer.Parameters[0];
                    if (each.Fold is { } fold)
                    {
                        --_repeated;
                        _Expression(fold.Seed);
                        ++_repeated;
                        _Expression(fold.Apply);
                    }
                    --_repeated;
                    return;

                // p with {…} copie p d'abord : si rien ne la relit ensuite, la copie est un déplacement.
                case IrWith with:
                    _Consumed(with.Source);
                    foreach (var change in with.Changes)
                    {
                        _Expression(change.Target);
                        _Expression(change.Value);
                    }
                    return;

                case IrMatch match:
                    _Expression(match.Subject);
                    ++_repeated;
                    foreach (var arm in match.Arms)
                    {
                        if (arm.Value is { } value) _Expression(value);
                        if (arm.Body is { } body) _Statement(body);
                    }
                    --_repeated;
                    return;
            }

            foreach (var child in IrEffects.Children(expression)) _Expression(child);
        }

        /// <summary>Les variables lues dans un corps de lambda, lambdas imbriquées comprises.</summary>
        private static IEnumerable<object> _Captured(IrBlock body) =>
            IrWalk.AllExpressions(body).SelectMany(e => e switch
            {
                IrVariableReference reference => [reference.Variable],
                IrParameterReference parameter => [parameter.Parameter],
                IrLambda inner => _Captured(inner.Body),
                _ => Array.Empty<object>(),
            });
    }
}
