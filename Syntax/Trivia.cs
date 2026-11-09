using Rgl.Text;

namespace Rgl.Syntax;

/// <summary>Sorte d'un trivia : ce qui sépare les tokens sans avoir de sens pour le compilateur.</summary>
internal enum TriviaKind
{
    /// <summary>Espaces et tabulations à la suite, sans les fins de ligne.</summary>
    Whitespace,

    /// <summary>
    /// Une fin de ligne : <c>\n</c> ou <c>\r\n</c>. Un <c>\r\n</c> forme un seul trivia, comme
    /// pour le calcul des débuts de ligne de <see cref="SourceText"/>.
    /// </summary>
    EndOfLine,

    /// <summary>
    /// Commentaire de ligne : de <c>//</c> jusqu'à la fin de la ligne, sans la fin de ligne
    /// elle-même, qui est un trivia <see cref="EndOfLine"/> à part.
    /// </summary>
    SingleComment,

    /// <summary>
    /// Commentaire de documentation : exactement trois barres <c>///</c> puis autre chose qu'une barre,
    /// jusqu'à la fin de la ligne (<c>////</c> et plus reste un <see cref="SingleComment"/>).
    /// </summary>
    DocComment,

    /// <summary>
    /// Commentaire de bloc : de <c>/*</c> à <c>*/</c>, éventuellement sur plusieurs lignes.
    /// Les commentaires de bloc ne s'imbriquent pas : le premier <c>*/</c> le ferme.
    /// Non fermé, il s'étend jusqu'à la fin du fichier et une erreur lexicale est signalée.
    /// </summary>
    BlockComment,
}

/// <summary>
/// Un morceau du source qui n'est pas un token (espace, fin de ligne, commentaire), gardé et
/// rattaché à un token plutôt que jeté. Le parseur l'ignore ; il sert au futur formateur et
/// garantit l'aller-retour : recoller, pour chaque token, ses trivia avant, son texte et ses
/// trivia après redonne exactement le fichier d'origine.
/// </summary>
/// <param name="Kind">Sorte du trivia.</param>
/// <param name="Span">Morceau du source couvert par le trivia ; son texte s'obtient par <c>Span.Text</c>.</param>
internal readonly record struct Trivia(TriviaKind Kind, SourceSpan Span)
{
    public bool IsEmpty() => Span.Start == Span.End;
}
