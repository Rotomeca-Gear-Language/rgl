using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Train;

/// <summary>
/// Un train : <c>[!]train [(paramètres)] [valeur de départ] |&gt; étapes ;</c>, chaque guard suivi de
/// son rail s'il en a un, par exemple <c>train([string] args) args |&gt; run !&gt; [printUsage |&gt; $ 2] |&gt; $ 0;</c>.
/// </summary>
/// <param name="Span">Du <c>!</c> ou du <c>train</c> jusqu'au <c>;</c> final.</param>
/// <param name="IsUnguarded">Vrai si le train est préfixé par <c>!</c> (train sans guard).</param>
/// <param name="Parameters">Les paramètres entre parenthèses ; <see langword="null"/> si le train n'a pas de parenthèses.</param>
/// <param name="StartValue">La valeur qui entre dans le pipeline (<c>args</c>) ; <see langword="null"/> si le pipeline part de <c>null</c>.</param>
/// <param name="Pipeline">Le chemin principal.</param>
internal sealed record TrainDeclaration(
    SourceSpan Span,
    bool IsUnguarded,
    IReadOnlyList<Parameter>? Parameters,
    IExpression? StartValue,
    Pipeline Pipeline,
    ITypeNode? NativeReturnType = null) : IStatement;
