using Rotomeca.Utils.Types;

namespace Rgl.Text;

/// <summary>
/// Un morceau d'un fichier source : à partir de l'indice <see cref="Start"/>, sur
/// <see cref="Length"/> caractères. C'est ce que portent les tokens, les nœuds d'AST et les
/// diagnostics ; la ligne et la colonne ne sont pas stockées, elles sont calculées à la
/// demande par le <see cref="SourceText"/> d'origine.
/// </summary>
/// <remarks>
/// <para>
/// La fin est exclusive : <see cref="End"/> désigne le caractère qui suit le dernier, comme
/// pour <see cref="string.Substring(int, int)"/>. Deux spans consécutifs se touchent donc
/// exactement (<c>a.End == b.Start</c>).
/// </para>
/// <para>
/// Un span de longueur 0 est légal : token de fin de fichier (à <c>Content.Length</c>) ou
/// token manquant inséré par le parseur lors de la récupération sur erreur.
/// </para>
/// <para>
/// Toujours passer par le constructeur ou par <see cref="FromBounds"/> : <c>default</c> donne
/// un span sans <see cref="Source"/>.
/// </para>
/// </remarks>
internal readonly record struct SourceSpan
{
    private readonly SourceText _source;
    private readonly RUint _start;
    private readonly RUint _length;

    /// <summary>Fichier dont provient le span.</summary>
    public SourceText Source => _source;

    /// <summary>Indice, dans <c>Source.Content</c>, du premier caractère du span.</summary>
    public RUint Start => _start;

    /// <summary>Nombre de caractères du span ; 0 pour un span vide.</summary>
    public RUint Length => _length;

    /// <summary>Indice du caractère qui suit le dernier caractère du span (fin exclusive).</summary>
    public RUint End => _start + _length;

    /// <summary>Texte couvert par le span.</summary>
    public string Text => _source.Content.Substring((int)_start.Value, (int)_length.Value);

    /// <summary>Position (ligne, colonne) du premier caractère, celle qu'affiche un diagnostic.</summary>
    public Position StartPosition => _source[_start];

    /// <summary>Position (ligne, colonne) de <see cref="End"/>, pour souligner la fin du span.</summary>
    public Position EndPosition => _source[End];

    /// <summary>Crée un span à partir de son début et de sa longueur.</summary>
    /// <param name="source">Fichier dont provient le span.</param>
    /// <param name="start">Indice du premier caractère.</param>
    /// <param name="length">Nombre de caractères.</param>
    /// <exception cref="ArgumentOutOfRangeException">Si le span dépasse la fin du fichier.</exception>
    public SourceSpan(SourceText source, RUint start, RUint length)
    {
        var contentLength = RUint.CreateChecked(source.Content.Length);
        if (start + length > contentLength)
            throw new ArgumentOutOfRangeException(nameof(length), length, $"Le span [{start}, {start + length}[ dépasse la fin de {source.Path} ({contentLength} caractères).");

        _source = source;
        _start = start;
        _length = length;
    }

    /// <summary>Crée un span à partir de son début et de sa fin (exclusive).</summary>
    /// <param name="source">Fichier dont provient le span.</param>
    /// <param name="start">Indice du premier caractère.</param>
    /// <param name="end">Indice du caractère qui suit le dernier.</param>
    /// <exception cref="OverflowException">Si <paramref name="end"/> est avant <paramref name="start"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Si le span dépasse la fin du fichier.</exception>
    public static SourceSpan FromBounds(SourceText source, RUint start, RUint end) => new(source, start, end - start);

    /// <summary>
    /// Plus petit span qui couvre ce span et <paramref name="other"/>, du début du premier à
    /// la fin du dernier, en incluant ce qui se trouve entre les deux. Sert à donner à un nœud
    /// d'AST le span de ses tokens.
    /// </summary>
    /// <param name="other">Span à réunir avec celui-ci.</param>
    /// <returns>Le span qui couvre les deux.</returns>
    /// <exception cref="ArgumentException">Si les deux spans ne viennent pas du même fichier.</exception>
    public SourceSpan Union(SourceSpan other)
    {
        if (!ReferenceEquals(_source, other._source))
            throw new ArgumentException($"Impossible de réunir un span de {_source.Path} et un span de {other._source.Path}.", nameof(other));

        return FromBounds(_source, RUint.Min(_start, other._start), RUint.Max(End, other.End));
    }

    /// <summary>Le span sous la forme <c>chemin:ligne:colonne</c>, comme dans un diagnostic.</summary>
    public override string ToString()
    {
        var position = StartPosition;
        return $"{_source.Path}:{position.Line}:{position.Column}";
    }
}
