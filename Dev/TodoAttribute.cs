namespace Rgl.Dev;

/// <summary>
/// Marque une classe ou une méthode inachevée. En configuration Debug, l'analyseur
/// <c>RGLDEV001</c> (projet <c>analyzers/Rgl.Analyzers</c>) signale chaque marque à la
/// compilation, avec le fichier et la position ; rien n'est signalé en Release.
/// </summary>
/// <param name="description">Ce qu'il reste à faire.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
internal sealed class TodoAttribute(string description) : Attribute
{
    /// <summary>Ce qu'il reste à faire.</summary>
    public string Description { get; } = description;
}
