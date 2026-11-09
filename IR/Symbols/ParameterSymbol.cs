using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>
/// Un paramètre déclaré. Les références à ce paramètre dans l'IR pointent toutes vers le même
/// objet : c'est une classe, comparée par identité, et non un nom.
/// </summary>
/// <param name="name">Nom du paramètre.</param>
/// <param name="type">Type du paramètre.</param>
/// <param name="declaration">Endroit où le paramètre est déclaré.</param>
/// <param name="isMutable">
/// Vrai pour un paramètre <c>mut</c> : il désigne la variable de l'appelant, que le gear modifie (ou,
/// pour une lambda de parcours, l'élément parcouru).
/// </param>
internal sealed class ParameterSymbol(string name, IrType type, SourceSpan declaration, bool isMutable = false)
{
    /// <summary>Nom du paramètre.</summary>
    public string Name { get; } = name;

    /// <summary>Type du paramètre.</summary>
    public IrType Type { get; } = type;

    /// <summary>Endroit où le paramètre est déclaré.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>Vrai pour un paramètre <c>mut</c> : il désigne la variable de l'appelant, qu'il modifie.</summary>
    public bool IsMutable { get; } = isMutable;

    /// <summary>Ce que vaut le paramètre quand l'appel ne le donne pas.</summary>
    public ParameterDefault Default { get; init; } = ParameterDefault.None;

    /// <summary>Le défaut écrit d'un paramètre de gear, lié dans le corps du gear (<see cref="ParameterDefault.Expression"/>).</summary>
    public AST.Interfaces.IExpression? DefaultSyntax { get; init; }

    /// <summary>Vrai pour <c>params [T]</c> : le dernier paramètre, qui reçoit tous les arguments qui restent (ou un tableau).</summary>
    public bool IsParams => Default == ParameterDefault.Params;

    /// <summary>Vrai si l'appel peut ne pas donner ce paramètre.</summary>
    public bool HasDefault => Default != ParameterDefault.None;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} : {Type}" + (DefaultSyntax is null ? "" : $": {DefaultSyntax.Span.Text}");
}
