using System.Text.RegularExpressions;

namespace Rgl.Project.Packages;

/// <summary>
/// Identité d'un paquet : l'adresse de son dépôt, sans schéma (<c>github.com/alice/json</c>). C'est elle qui nomme le paquet, donc personne ne
/// peut prendre le nom d'un autre. Les noms sont écrits en minuscules (les forges ne distinguent pas la casse) : deux écritures de la même
/// adresse désignent le même paquet.
/// </summary>
/// <param name="Host">La forge : <c>github.com</c>, <c>gitlab.com</c> ou <c>codeberg.org</c>.</param>
/// <param name="Owner">Le compte ou l'organisation.</param>
/// <param name="Repository">Le dépôt.</param>
internal sealed partial record PackageId(string Host, string Owner, string Repository)
{
    /// <summary>Les forges dont on sait télécharger une archive.</summary>
    public static readonly string[] Hosts = ["github.com", "gitlab.com", "codeberg.org"];

    /// <summary>L'adresse du paquet, telle qu'elle s'écrit dans le manifeste et dans le lock.</summary>
    public string Source => $"{Host}/{Owner}/{Repository}";

    /// <inheritdoc/>
    public override string ToString() => Source;

    /// <summary>Lit une adresse de paquet ; <see langword="null"/> avec la raison dans <paramref name="error"/> si elle ne convient pas.</summary>
    /// <param name="text">L'adresse écrite : <c>hôte/compte/dépôt</c>.</param>
    /// <param name="error">Pourquoi l'adresse est refusée.</param>
    public static PackageId? Parse(string text, out string? error)
    {
        error = null;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            error = $"« {text} » : l'adresse d'un paquet s'écrit sans schéma, par exemple « github.com/alice/json »";
            return null;
        }

        var parts = text.Split('/');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
        {
            error = $"« {text} » : l'adresse d'un paquet s'écrit « hôte/compte/dépôt », par exemple « github.com/alice/json »";
            return null;
        }

        var host = parts[0].ToLowerInvariant();
        if (!Hosts.Contains(host))
        {
            error = $"« {text} » : l'hôte « {parts[0]} » n'est pas pris en charge (hôtes : {string.Join(", ", Hosts)})";
            return null;
        }

        foreach (var part in parts.Skip(1))
        {
            if (!SegmentPattern().IsMatch(part) || part is "." or "..")
            {
                error = $"« {text} » : « {part} » n'est pas un nom de compte ou de dépôt valide (lettres, chiffres, « . », « - » et « _ »)";
                return null;
            }
        }

        if (parts[2].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            error = $"« {text} » : écris le nom du dépôt sans « .git »";
            return null;
        }

        return new PackageId(host, parts[1].ToLowerInvariant(), parts[2].ToLowerInvariant());
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SegmentPattern();
}

/// <summary>Les versions d'un paquet : trois nombres, <c>1.2.0</c>, qui sont aussi son tag Git précédé d'un <c>v</c> (<c>v1.2.0</c>).</summary>
internal static partial class PackageVersion
{
    /// <summary>Vrai si <paramref name="text"/> est une version exacte <c>majeur.mineur.correctif</c>, sans zéro de tête.</summary>
    public static bool IsValid(string text) => Pattern().IsMatch(text);

    /// <summary>Le tag Git d'une version : <c>v1.2.0</c>.</summary>
    public static string Tag(string version) => "v" + version;

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex Pattern();
}
