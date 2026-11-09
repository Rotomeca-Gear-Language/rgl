using Rgl.Text;

namespace Rgl.Syntax;

/// <summary>
/// Un token produit par le lexer : sa sorte, le morceau du source qu'il couvre et les trivia
/// (espaces, fins de ligne, commentaires) qui l'entourent.
/// </summary>
/// <remarks>
/// <para>
/// Règle de rattachement des trivia : appartient à un token, dans <see cref="After"/>, tout ce
/// qui le suit sur la même ligne, jusqu'à la fin de ligne incluse ; tout le reste appartient,
/// dans <see cref="Before"/>, au token suivant. Un commentaire placé au-dessus d'une
/// déclaration est donc dans les trivia avant de son premier token, et un commentaire en bout
/// de ligne dans les trivia après du dernier token de la ligne.
/// </para>
/// <para>
/// Le token <see cref="TokenKind.EndOfFile"/> récupère dans <see cref="Before"/> tout ce qui
/// reste à la fin du fichier : c'est ce qui permet l'aller-retour jusqu'au dernier caractère.
/// Un fichier sans instruction (W006) ne produit que ce token.
/// </para>
/// <para>
/// L'égalité générée par le <see langword="record"/> compare les listes de trivia par
/// référence : pour comparer deux tokens dans un test, comparer <see cref="Kind"/> et
/// <see cref="Text"/>.
/// </para>
/// </remarks>
/// <param name="Kind">Sorte du token.</param>
/// <param name="Span">
/// Morceau du source couvert par le token seul, sans ses trivia. Vide pour
/// <see cref="TokenKind.EndOfFile"/>, à <c>Content.Length</c>.
/// </param>
/// <param name="Before">Trivia placés avant le token ; vide (<c>[]</c>) s'il n'y en a pas.</param>
/// <param name="After">Trivia placés après le token, jusqu'à la fin de ligne incluse ; vide (<c>[]</c>) s'il n'y en a pas.</param>
internal sealed record class Token(TokenKind Kind, SourceSpan Span, IReadOnlyList<Trivia> Before, IReadOnlyList<Trivia> After)
{
    /// <summary>
    /// Texte du token tel qu'écrit dans le source, calculé à partir de <see cref="Span"/>. Pour
    /// un littéral, c'est le texte brut (<c>"a\n"</c> avec ses guillemets et son échappement),
    /// pas sa valeur décodée.
    /// </summary>
    public string Text => Span.Text;

    /// <summary>
    /// Vérifie si 2 tokens sont identiques
    /// </summary>
    /// <param name="token">Token a à tester</param>
    /// <returns>Vrai si identiques, false sinon</returns>
    public bool IsSame(Token token) => token.Kind == Kind && token.Text == Text;
}
