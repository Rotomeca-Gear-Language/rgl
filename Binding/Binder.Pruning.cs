using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;

namespace Rgl.Binding;

/// <summary>
/// Élagage : un gear d'extension ou de la std n'est écrit en C++ que si le programme l'appelle, directement ou par un autre
/// gear d'extension qu'il appelle. Les autres gears restent tous écrits.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>Vrai pour un gear qui n'est écrit que s'il est appelé : un gear d'extension, ou un gear de la std embarquée.</summary>
    private bool _IsPrunable(GearSymbol gear) => _extensionGears.Contains(gear) || Project.StdLibrary.Contains(gear.Declaration.Source.Path);

    /// <summary>Les gears sans ceux d'extension que rien n'atteint depuis les instructions du fichier ou un gear ordinaire.</summary>
    private IReadOnlyList<IrGear> _WithoutUnusedExtensions(IReadOnlyList<IrStatement> statements, IReadOnlyList<IrGear> gears)
    {

        var used = new HashSet<GearSymbol>();
        var pending = new Queue<GearSymbol>();

        void Mark(GearSymbol gear)
        {
            if (_IsPrunable(gear) && used.Add(gear)) pending.Enqueue(gear);
        }

        void Scan(IrStatement root)
        {
            foreach (var expression in IrWalk.AllExpressions(root))
            {
                switch (expression)
                {
                    case IrCall call:
                        Mark(call.Gear);
                        break;
                    case IrGearReference reference:
                        Mark(reference.Gear);
                        break;
                    // Le corps d'une lambda n'est pas une sous-expression : on y entre.
                    case IrLambda lambda:
                        Scan(lambda.Body);
                        break;
                }
            }
        }

        void ScanExpression(IrExpression expression) => Scan(new IrExpressionStatement(expression.Span, expression));

        foreach (var statement in statements)
        {
            if (statement is IrEntryTrain entry)
            {
                if (entry.StartValue is { } start) ScanExpression(start);
                foreach (var root in IrWalk.PathRoots(entry.Main)) ScanExpression(root);
            }
            else Scan(statement);
        }

        var byGear = gears.ToDictionary(g => g.Symbol);
        foreach (var gear in gears.Where(g => !_IsPrunable(g.Symbol)))
            ScanGear(gear);
        while (pending.TryDequeue(out var gear))
            if (byGear.TryGetValue(gear, out var bound)) ScanGear(bound);

        return [.. gears.Where(g => !_IsPrunable(g.Symbol) || used.Contains(g.Symbol))];

        void ScanGear(IrGear gear)
        {
            Scan(gear.Body);
            // Les taps font partie du corps exécutable du gear. Leurs appels, y compris ceux qui
            // vivent dans une lambda, doivent garder les gears de la std et les extensions qu'ils
            // utilisent ; sinon le C++ appelle une fonction élaguée.
            foreach (var tap in gear.Taps ?? []) Scan(tap.Statement);
            foreach (var parameterDefault in gear.Defaults ?? []) ScanExpression(parameterDefault.Value);
        }
    }
}
