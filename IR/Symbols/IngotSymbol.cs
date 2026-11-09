using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>Un ingot : un nom posé sur un type de base. Comparé par identité.</summary>
/// <param name="name">Son nom.</param>
/// <param name="declaration">Son nom dans sa déclaration.</param>
internal sealed class IngotSymbol(string name, SourceSpan declaration)
{
    /// <summary>Son nom.</summary>
    public string Name { get; } = name;

    /// <summary>Son nom dans sa déclaration.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>Le dataset dont il est l'identifiant (<c>Moves.id</c>) ; <see langword="null"/> pour un ingot écrit par l'utilisateur.</summary>
    public DatasetSymbol? Dataset { get; set; }

    /// <summary>Le type sur lequel il repose ; fixé une fois la déclaration liée.</summary>
    public IrType Base { get; set; } = PrimitiveType.Void;

    /// <inheritdoc/>
    public override string ToString() => Name;
}
