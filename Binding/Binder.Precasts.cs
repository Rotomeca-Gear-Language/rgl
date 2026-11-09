using Rgl.IR;
using Rgl.IR.Expressions;
using Rgl.IR.Gears;
using Rgl.IR.Statements;
using Rgl.IR.Symbols;
using Rgl.IR.Train;

namespace Rgl.Binding;

internal sealed partial class Binder
{
    /// <summary>Les variables que des precasts ont données au programme.</summary>
    private readonly HashSet<VariableSymbol> _precastVariables = [];

    /// <summary>
    /// Vrai si <paramref name="value"/> est une donnée : des littéraux, des constantes, des molds, tableaux, dictionnaires et tuples faits
    /// de données, et leurs conversions. Un appel, une lambda, une lecture ou un paramètre n'en font pas partie.
    /// </summary>
    private static bool _IsPrecastData(IrExpression value) =>
        !IrWalk.Expressions(value).Any(e => e is IrCall or IrInvoke or IrLambda or IrGearReference or IrRead or IrEach or IrParameterReference or IrPipelineValue);

    /// <summary>
    /// Un precast, ou une constante de la std, que rien n'atteint n'est pas écrit dans le C++ : un fichier de precasts produit par un outil peut en contenir
    /// des milliers, et le programme n'en compile que ce qu'il lit — directement, par un gear, une lambda, une valeur par défaut
    /// ou un autre precast qu'il lit.
    /// </summary>
    private IReadOnlyList<IrStatement> _WithoutUnusedPrecasts(IReadOnlyList<IrStatement> statements, IReadOnlyList<IrGear> gears)
    {
        var precasts = statements.OfType<IrVariableDeclaration>().Where(d => _precastVariables.Contains(d.Variable) || Project.StdLibrary.Contains(d.Variable.Declaration.Source.Path)).ToDictionary(d => d.Variable);
        if (precasts.Count == 0) return statements;

        var used = new HashSet<VariableSymbol>();
        var pending = new Queue<VariableSymbol>();

        void Scan(IrStatement root)
        {
            foreach (var expression in IrWalk.AllExpressions(root))
            {
                if (expression is IrVariableReference reference && precasts.ContainsKey(reference.Variable) && used.Add(reference.Variable)) pending.Enqueue(reference.Variable);
                // Le corps d'une lambda n'est pas une sous-expression : on y entre.
                if (expression is IrLambda lambda) Scan(lambda.Body);
            }
        }

        void ScanExpression(IrExpression expression) => Scan(new IrExpressionStatement(expression.Span, expression));

        foreach (var statement in statements)
        {
            switch (statement)
            {
                case IrVariableDeclaration declaration when precasts.ContainsKey(declaration.Variable):
                    break;
                case IrEntryTrain entry:
                    if (entry.StartValue is { } start) ScanExpression(start);
                    foreach (var root in IrWalk.PathRoots(entry.Main)) ScanExpression(root);
                    break;
                default:
                    Scan(statement);
                    break;
            }
        }
        foreach (var gear in gears)
        {
            Scan(gear.Body);
            foreach (var parameterDefault in gear.Defaults ?? []) ScanExpression(parameterDefault.Value);
        }
        while (pending.TryDequeue(out var variable)) ScanExpression(precasts[variable].Value);

        return [.. statements.Where(s => s is not IrVariableDeclaration declaration || !precasts.ContainsKey(declaration.Variable) || used.Contains(declaration.Variable))];
    }
}
