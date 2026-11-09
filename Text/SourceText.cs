using System.Buffers;
using System.Text;
using Rgl.Diagnostics;
using Rotomeca.Core.Optionals;
using Rotomeca.Utils.Types;
using Rotomeca.Utils;

namespace Rgl.Text;

/// <summary>
/// Position lisible d'un caractère dans un fichier source, telle qu'on l'affiche à
/// l'utilisateur dans un diagnostic (<c>main.rgl:2:8</c>).
/// </summary>
/// <param name="Line">Numéro de ligne, à partir de 1.</param>
/// <param name="Column">Numéro de colonne dans la ligne, à partir de 1.</param>
internal readonly record struct Position(RUint Line, RUint Column);

/// <summary>
/// Un fichier source chargé en mémoire : son chemin, son contenu et l'indice du début de
/// chaque ligne. Il est créé une fois par fichier et partagé par tous les tokens et
/// diagnostics, qui ne retiennent que des indices dans <see cref="Content"/> ; c'est lui
/// qui les traduit en <see cref="Position"/> (ligne, colonne) au moment de l'affichage.
/// </summary>
internal sealed class SourceText
{
    private readonly string _path;
    private readonly string _content;
    private readonly IReadOnlyList<RUint> _lineStart;

    /// <summary>Chemin du fichier, tel qu'il sera affiché dans les diagnostics.</summary>
    public string Path => _path;

    /// <summary>Contenu du fichier, sans la marque BOM éventuelle.</summary>
    public string Content => _content;

    /// <summary>
    /// Indice, dans <see cref="Content"/>, du premier caractère de chaque ligne : l'élément 0
    /// est le début de la ligne 1 (toujours 0), l'élément 1 celui de la ligne 2, etc.
    /// </summary>
    public IReadOnlyList<RUint> LineStart => _lineStart;

    /// <summary>
    /// Pour un texte engendré à partir d'un autre fichier (une page Paper) : le numéro de ligne, dans le fichier d'origine, de chaque ligne engendrée
    /// (l'élément 0 est la ligne 1) ; 0 pour une ligne qui n'en a pas. <see langword="null"/> pour un fichier ordinaire. Le débogueur et les positions d'arrêt
    /// du programme parlent du fichier d'origine, pas du texte engendré.
    /// </summary>
    public IReadOnlyList<uint>? LineMap { get; init; }

    /// <summary>La ligne, dans le fichier d'origine, d'une ligne de ce texte : la même pour un fichier ordinaire, la ligne 1 pour une ligne engendrée sans équivalent.</summary>
    public RUint OriginalLine(RUint line)
    {
        if (LineMap is not { } map || line.Value < 1 || line.Value > map.Count) return line;
        return RUint.CreateChecked(map[(int)line.Value - 1] is var original and > 0 ? original : 1u);
    }

    /// <summary>Crée le texte d'un fichier déjà lu et calcule les débuts de ligne.</summary>
    /// <param name="path">Chemin du fichier.</param>
    /// <param name="content">Contenu du fichier, BOM déjà retiré.</param>
    public SourceText(string path, string content)
    {
        _path = path;
        _content = content;
        _lineStart = _ComputeLinesStart(content);
    }

    /// <summary>
    /// Lit un fichier source en UTF-8 strict ; une marque BOM UTF-8 en tête est retirée.
    /// </summary>
    /// <remarks>
    /// Un fichier UTF-16 ou UTF-32 (reconnu à sa marque BOM) est refusé (E029) et rien n'est
    /// renvoyé. Un octet UTF-8 invalide est signalé (E028) à sa position, et le texte est quand
    /// même renvoyé, l'octet remplacé par <c>U+FFFD</c>, pour que le lexer signale aussi les
    /// autres erreurs du fichier.
    /// </remarks>
    /// <param name="path">Chemin du fichier à lire.</param>
    /// <param name="diagnostics">Reçoit les erreurs d'encodage.</param>
    /// <returns>Le texte du fichier, ou <see langword="null"/> si son encodage est refusé.</returns>
    public static SourceText? FromPath(string path, DiagnosticBag diagnostics)
    {
        var bytes = File.ReadAllBytes(path);

        var foreignEncoding = _DetectForeignBom(bytes);
        if (foreignEncoding is not null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticLevel.Error,
                $"le fichier est encodé en {foreignEncoding} (marque BOM détectée) ; RGL n'accepte que l'UTF-8", path, DiagnosticCodes.UnsupportedEncoding,
                Help: ["réenregistre le fichier en UTF-8 depuis ton éditeur",
                       "sous Windows PowerShell 5.1, « > » écrit en UTF-16 : utilise plutôt « Out-File -Encoding utf8 »"]));
            return null;
        }

        ReadOnlySpan<byte> data = bytes;
        if (data.StartsWith(_utf8Bom)) data = data[_utf8Bom.Length..];

        var source = new SourceText(path, Encoding.UTF8.GetString(data));

        var invalidOffset = _FindInvalidUtf8(data);
        if (invalidOffset.HasValue)
        {
            // Le préfixe est valide : son nombre de caractères donne l'indice du premier U+FFFD.
            var index = RUint.CreateChecked(Encoding.UTF8.GetCharCount(data[..invalidOffset.Value]));
            diagnostics.Error(new SourceSpan(source, index, RUint.One), DiagnosticCodes.InvalidUtf8,
                $"octet UTF-8 invalide (0x{data[invalidOffset.Value]:X2}) : le fichier doit être encodé en UTF-8", "octet invalide",
                ["le fichier a sans doute été enregistré dans un autre encodage (Latin-1, Windows-1252…)",
                 "réenregistre-le en UTF-8 depuis ton éditeur"]);
        }

        return source;
    }

    /// <summary>Position (ligne, colonne) du caractère d'indice <paramref name="index"/>.</summary>
    /// <param name="index">
    /// Indice dans <see cref="Content"/> ; <c>Content.Length</c> est accepté et désigne la fin
    /// du fichier (position du token de fin).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Si <paramref name="index"/> dépasse <c>Content.Length</c>.</exception>
    public Position this[RUint index] => _GetPositionFromIndex(index);

    /// <summary>Marque BOM UTF-8, tolérée et retirée en tête de fichier.</summary>
    private static readonly byte[] _utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Marques BOM des encodages refusés, avec leur nom. UTF-32 LE est testé avant UTF-16 LE,
    /// dont la marque est un préfixe de la sienne.
    /// </summary>
    private static readonly (byte[] Bom, string Name)[] _foreignBoms =
    [
        ([0xFF, 0xFE, 0x00, 0x00], "UTF-32 LE"),
        ([0x00, 0x00, 0xFE, 0xFF], "UTF-32 BE"),
        ([0xFF, 0xFE], "UTF-16 LE"),
        ([0xFE, 0xFF], "UTF-16 BE"),
    ];

    /// <summary>Nom de l'encodage si <paramref name="bytes"/> commence par la marque BOM d'un encodage refusé.</summary>
    private static string? _DetectForeignBom(ReadOnlySpan<byte> bytes)
    {
        foreach (var (bom, name) in _foreignBoms)
        {
            if (bytes.StartsWith(bom)) return name;
        }

        return null;
    }

    /// <summary>Position, en octets, de la première séquence UTF-8 invalide ou tronquée.</summary>
    private static MayBe<int> _FindInvalidUtf8(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (Rune.DecodeFromUtf8(data[offset..], out _, out var consumed) != OperationStatus.Done)
                return MayBe<int>.Some(offset);

            offset += consumed;
        }

        return MayBe<int>.Null;
    }

    /// <summary>
    /// Débuts de ligne : 0, puis l'indice qui suit chaque <c>'\n'</c>. Un <c>"\r\n"</c> compte
    /// donc pour un seul saut de ligne, le <c>'\r'</c> restant en fin de la ligne précédente.
    /// </summary>
    private static IReadOnlyList<RUint> _ComputeLinesStart(string content)
    {
        Rotomeca.Core.Collections.RArray<RUint> lines = new(0);

        for (int i = 0, len = content.Length; i < len; ++i)
        {
            if (content[i] == char.EndOfLine) lines.Push(RUint.CreateChecked(i + 1));
        }

        return lines.AsReadOnly();
    }

    /// <summary>
    /// Cherche la ligne qui contient <paramref name="index"/> : la dernière dont le début est
    /// inférieur ou égal à l'indice. Si aucun début de ligne ne dépasse l'indice, c'est la
    /// dernière ligne du fichier.
    /// </summary>
    private Position _GetPositionFromIndex(RUint index)
    {
        if (index > RUint.CreateChecked(_content.Length))
            throw new ArgumentOutOfRangeException(nameof(index), index, $"L'index {index} dépasse la fin du fichier ({_content.Length} caractères).");

        // Par défaut, la dernière ligne ; LineStart[line - 1] est le début de la ligne n° line.
        var line = _lineStart.Count;
        for (int i = 1, len = _lineStart.Count; i < len; ++i)
        {
            if (_lineStart[i] > index)
            {
                line = i;
                break;
            }
        }

        return new Position(RUint.CreateChecked(line), index - _lineStart[line - 1] + RUint.One);
    }
}
