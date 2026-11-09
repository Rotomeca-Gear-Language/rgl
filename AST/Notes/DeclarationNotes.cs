using Rgl.AST.Interfaces;
using Rgl.Text;

namespace Rgl.AST.Notes;

/// <summary>Une annotation <c>@nom</c> : elle change ce que le compilateur génère ou vérifie. La liste est fermée.</summary>
internal interface IAnnotation : IAstNode
{
}

/// <summary><c>@deprecated("Utiliser autre()")</c> : le gear est obsolète ; chaque appel avertit (W061), avec ce message.</summary>
/// <param name="Span">L'annotation.</param>
/// <param name="Message">Le message, un littéral chaîne ; <see langword="null"/> sans.</param>
internal sealed record DeprecatedAnnotation(SourceSpan Span, IExpression? Message) : IAnnotation;

/// <summary>Quand un <c>@tap</c> s'exécute.</summary>
internal enum TapMode
{
    /// <summary>Avant le gear : <c>$</c> y est son premier paramètre.</summary>
    Before,

    /// <summary>Après le gear (par défaut) : <c>$</c> y est la valeur qu'il renvoie.</summary>
    After,
}

/// <summary>
/// <c>@tap(before, `→ {n}` -&gt; console)</c> : un effet de bord attaché à un gear, sans changer sa valeur. Sans <c>closure</c>, c'est une instruction sans
/// <c>;</c> — une écriture <c>valeur -&gt; cible</c> ou un appel ; une lambda implique <c>closure</c>. Une fonction est appelée avec <c>$</c> (après) ou avec les
/// paramètres du gear (avant), sauf une lambda sans paramètre qui est simplement appelée.
/// </summary>
/// <param name="Span">L'annotation.</param>
/// <param name="Mode">Avant ou après.</param>
/// <param name="IsClosure">Vrai si <paramref name="Value"/> est une fonction à appeler.</param>
/// <param name="Value">La valeur écrite, l'appel, ou la fonction.</param>
/// <param name="Target">La cible d'une écriture (<c>console</c>) ; <see langword="null"/> sinon.</param>
/// <param name="Path">Le chemin d'une écriture vers un fichier ; <see langword="null"/> sinon.</param>
internal sealed record TapAnnotation(SourceSpan Span, TapMode Mode, bool IsClosure, IExpression Value, Identifier? Target, IExpression? Path) : IAnnotation;

/// <summary>
/// <c>@before(contrôle)</c> : avant le corps du gear, <c>contrôle</c> (une fonction qui reçoit les paramètres du gear et rend <c>R?</c>) est appelée ; une réponse non
/// nulle est renvoyée à la place du corps, <c>null</c> laisse le gear s'exécuter.
/// </summary>
/// <param name="Span">L'annotation.</param>
/// <param name="Check">L'expression qui donne la fonction de contrôle.</param>
internal sealed record BeforeAnnotation(SourceSpan Span, IExpression Check) : IAnnotation;

/// <summary>Une annotation déclarée par le programme (<c>annotation nom(…) = …;</c>), utilisée comme <c>@nom(arguments)</c> : le binder la développe.</summary>
/// <param name="Span">L'annotation.</param>
/// <param name="Name">Son nom.</param>
/// <param name="Arguments">Les arguments écrits entre parenthèses.</param>
internal sealed record CustomAnnotation(SourceSpan Span, Identifier Name, IReadOnlyList<IExpression> Arguments) : IAnnotation;

/// <summary>
/// <c>annotation exigeRole(params [Role] rôles) = @before(refuseSauf(roleCourant, rôles));</c> : une annotation du programme, un paquet d'annotations de base dont
/// les paramètres se lisent comme des constantes.
/// </summary>
/// <param name="Span">La déclaration.</param>
/// <param name="Name">Son nom.</param>
/// <param name="Parameters">Ses paramètres.</param>
/// <param name="Body">Les annotations de base qu'elle développe.</param>
internal sealed record AnnotationDeclaration(SourceSpan Span, Identifier Name, IReadOnlyList<Train.Parameter> Parameters, IReadOnlyList<IAnnotation> Body) : Interfaces.IStatement;

/// <summary><c>@async</c>, <c>@sync</c>, <c>@fire</c> : reconnues, mais pas encore prises en charge.</summary>
/// <param name="Span">L'annotation.</param>
/// <param name="Name">Son nom.</param>
internal sealed record OtherAnnotation(SourceSpan Span, Identifier Name) : IAnnotation;

/// <summary>Une entrée de métadonnées : <c>db</c> ou <c>cache: "5min"</c>.</summary>
/// <param name="Span">L'entrée.</param>
/// <param name="Key">La clé.</param>
/// <param name="Value">La valeur ; <see langword="null"/> pour une clé seule.</param>
internal sealed record MetadataEntry(SourceSpan Span, Identifier Key, IExpression? Value) : IAstNode;

/// <summary>
/// Des métadonnées <c>[db, cache: "5min"]</c> : le compilateur les ignore, elles servent aux outils (éditeur, documentation, linter) ; toute clé est acceptée.
/// </summary>
/// <param name="Span">De <c>[</c> à <c>]</c>.</param>
/// <param name="Entries">Les entrées.</param>
internal sealed record MetadataBlock(SourceSpan Span, IReadOnlyList<MetadataEntry> Entries) : IAstNode;

/// <summary>Les annotations et les métadonnées écrites devant une déclaration.</summary>
/// <param name="Span">De la première note à la dernière.</param>
/// <param name="Annotations">Les annotations, dans l'ordre.</param>
/// <param name="Metadata">Les blocs de métadonnées, dans l'ordre.</param>
internal sealed record DeclarationNotes(SourceSpan Span, IReadOnlyList<IAnnotation> Annotations, IReadOnlyList<MetadataBlock> Metadata) : IAstNode;
