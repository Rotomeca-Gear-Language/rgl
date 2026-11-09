using Rgl.IR.Types;
using Rgl.Text;

namespace Rgl.IR.Symbols;

/// <summary>Un enum : son type sous-jacent et ses variantes. Comparé par identité.</summary>
/// <param name="name">Nom de l'enum.</param>
/// <param name="underlying">Le type sous-jacent, écrit ou choisi (le plus petit qui suffit).</param>
/// <param name="declaration">Son nom dans sa déclaration.</param>
internal sealed class EnumSymbol(string name, PrimitiveType underlying, SourceSpan declaration, string? qualifiedName = null)
{
    /// <summary>Nom de l'enum.</summary>
    public string Name { get; } = name;

    /// <summary>Son nom unique dans le programme, d'où vient son nom C++.</summary>
    public string QualifiedName { get; } = qualifiedName ?? name;

    /// <summary>Son nom en C++ : son nom unique, sans les points d'un nom qualifié.</summary>
    public string CppName => QualifiedName.Replace(".", "_d_");

    /// <summary>Le type sous-jacent, écrit après <c>:</c> ou choisi par le compilateur.</summary>
    public PrimitiveType Underlying { get; } = underlying;

    /// <summary>Son nom dans sa déclaration.</summary>
    public SourceSpan Declaration { get; } = declaration;

    /// <summary>Vrai si le type est écrit : chaque variante a alors sa valeur.</summary>
    public bool HasValues { get; init; }

    /// <summary>Les variantes, dans l'ordre.</summary>
    public IReadOnlyList<EnumVariantSymbol> Variants { get; set; } = [];

    /// <summary>La variante de ce nom, ou <see langword="null"/>.</summary>
    public EnumVariantSymbol? this[string variant] => Variants.FirstOrDefault(v => v.Name == variant);

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>Une variante d'enum.</summary>
/// <param name="Name">Son nom.</param>
/// <param name="Index">Sa position.</param>
/// <param name="Declaration">Son nom dans la déclaration.</param>
internal sealed record EnumVariantSymbol(string Name, int Index, SourceSpan Declaration)
{
    /// <summary>Sa valeur dans le type sous-jacent, connue à la compilation (un <see cref="long"/>, un <see cref="double"/>, un <see cref="bool"/>…) ; sa position sans type écrit.</summary>
    public object? Value { get; set; }
}
