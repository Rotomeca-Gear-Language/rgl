namespace Rgl.IR.Types;

/// <summary>
/// <c>Promise&lt;T ! E&gt;</c> : le résultat, pas encore connu, d'un gear <c>@async</c>. <c>await</c> en donne le résultat,
/// <c>T ! E</c> (ou <c>E?</c> quand le gear ne renvoie rien : <see langword="null"/> veut dire que tout s'est bien passé).
/// </summary>
/// <param name="Value">Le type de la valeur ; <c>void</c> pour un gear async qui ne renvoie rien.</param>
/// <param name="Error">Le type de l'erreur : <c>AsyncException</c>, ou un type d'erreur qui en a les champs.</param>
internal sealed record PromiseType(IrType Value, IrType Error) : IrType
{
    /// <summary>Le type de ce que <c>await</c> donne : <c>T ! E</c>, ou <c>E?</c> sans valeur.</summary>
    public IrType Awaited => Value == PrimitiveType.Void ? new NullableType(Error) : new ResultType(Value, Error);

    /// <inheritdoc/>
    public override string ToString() => $"Promise<{Value} ! {Error}>";
}
