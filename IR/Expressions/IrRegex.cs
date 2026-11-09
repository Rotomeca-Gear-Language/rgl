using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Expressions;

/// <summary>Une regex littérale <c>/motif/flags</c>.</summary>
/// <param name="Span">Le littéral.</param>
/// <param name="Pattern">Le motif, tel qu'écrit entre les barres.</param>
/// <param name="Global">Vrai avec le flag <c>g</c> : <c>find</c> et <c>replace</c> traitent toutes les occurrences.</param>
/// <param name="Flags">Les flags, dans l'ordre alphabétique et sans doublon (<c>g</c>, <c>i</c>, <c>m</c>).</param>
internal sealed record IrRegexConstant(SourceSpan Span, string Pattern, bool Global, string Flags) : IrExpression(Span, PrimitiveType.Regex);

/// <summary>Ce qu'on fait d'une regex.</summary>
internal enum RegexOperation
{
    /// <summary><c>r.test(s)</c> : un <c>bool</c>.</summary>
    Test,

    /// <summary><c>r.match(s)</c> : <c>[string]?</c>, le texte trouvé puis chaque groupe, <c>null</c> sans occurrence.</summary>
    Match,

    /// <summary><c>r.find(s)</c> : <c>[string]</c>, les textes trouvés.</summary>
    Find,

    /// <summary><c>r.replace(s, par)</c> : un <c>string</c>.</summary>
    Replace,
}

/// <summary>Une méthode d'une regex (<c>test</c>, <c>match</c>, <c>find</c>, <c>replace</c>).</summary>
/// <param name="Span">L'appel.</param>
/// <param name="Target">La regex.</param>
/// <param name="Operation">L'opération.</param>
/// <param name="Subject">Le texte cherché.</param>
/// <param name="Replacement">Le texte qui remplace, pour <see cref="RegexOperation.Replace"/> ; sinon <see langword="null"/>.</param>
/// <param name="Type">Le type du résultat.</param>
internal sealed record IrRegexOp(SourceSpan Span, IrExpression Target, RegexOperation Operation, IrExpression Subject, IrExpression? Replacement, IrType Type) : IrExpression(Span, Type);
