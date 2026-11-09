using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>
/// Une variable déclarée. Comme pour <see cref="ParameterSymbol"/>, toutes les références à cette
/// variable pointent vers le même objet : deux variables de même nom (masquage, redéclaration
/// après <c>undefine</c>) restent deux symboles distincts.
/// </summary>
/// <param name="name">Nom de la variable.</param>
/// <param name="type">Type de la variable, fixé à la déclaration.</param>
/// <param name="isConstant">Vrai pour une constante : elle ne peut plus être réassignée.</param>
/// <param name="declaration">Endroit où la variable est déclarée.</param>
/// <param name="compileTimeValue">
/// Valeur d'une constante entièrement connue à la compilation (voir <see cref="ConstantFolder.Fold"/>) ;
/// <see langword="null"/> sinon.
/// </param>
/// <param name="isMutable">Vrai pour une variable <c>mut</c> : son contenu se modifie en place.</param>
internal sealed class VariableSymbol(string name, IrType type, bool isConstant, SourceSpan declaration, object? compileTimeValue, bool isMutable = false)
{
    public bool IsBorrowedPointer { get; set; }

    /// <summary>Nom de la variable.</summary>
    public string Name { get; } = name;

    /// <summary>Type de la variable.</summary>
    public IrType Type { get; } = type;

    /// <summary>Vrai pour une constante : elle ne peut plus être réassignée.</summary>
    public bool IsConstant { get; } = isConstant;

    /// <summary>Endroit où la variable est déclarée.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>Vrai pour une variable <c>mut</c> : son contenu (éléments, champs) se modifie en place.</summary>
    public bool IsMutable { get; } = isMutable;

    /// <summary>Valeur d'une constante connue à la compilation ; <see langword="null"/> sinon.</summary>
    public object? CompileTimeValue { get; } = compileTimeValue;

    /// <summary>Vrai pour une constante dont la valeur est entièrement connue à la compilation.</summary>
    public bool IsCompileTime => CompileTimeValue is not null;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} : {Type}";
}
