using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Expressions;

/// <summary>Les trois casts : <c>as</c>, <c>as?</c> et <c>as!</c>.</summary>
internal enum CastKind
{
    /// <summary>
    /// <c>as</c> : une valeur connue à la compilation doit tenir dans le type ; sinon, elle est
    /// contrôlée à l'exécution (arrêt du programme, ou échec dans un guard).
    /// </summary>
    Checked,

    /// <summary><c>as?</c> : <c>null</c> si la conversion échoue.</summary>
    Optional,

    /// <summary><c>as!</c> : dans un guard, qui échoue si la conversion échoue.</summary>
    Forced,
}

/// <summary>Un cast : <c>valeur as T</c>, <c>valeur as? T</c> ou <c>valeur as! T</c>.</summary>
/// <param name="Span">De la valeur au type.</param>
/// <param name="Value">La valeur convertie.</param>
/// <param name="Kind">Le cast.</param>
/// <param name="Type">Le type visé.</param>
internal sealed record CastExpression(SourceSpan Span, IExpression Value, CastKind Kind, ITypeNode Type) : IExpression;
