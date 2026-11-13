namespace Rgl.Project.Packages;

/// <summary>
/// Ce que rend une demande d'archive : le flux <c>.tar.gz</c>, ou la raison de l'échec. <paramref name="NotFound"/> distingue un paquet ou une
/// version qui n'existe pas d'un téléchargement qui a échoué.
/// </summary>
internal sealed record PackageFetch(Stream? Archive, string? Error, bool NotFound = false);

/// <summary>Les versions publiées d'un paquet, de la plus récente à la plus ancienne, ou la raison de l'échec.</summary>
internal sealed record PackageVersions(IReadOnlyList<string>? Versions, string? Error, bool NotFound = false);

/// <summary>
/// Le seul moyen qu'a le compilateur d'obtenir un paquet, injecté par <see cref="Cli.CliContext"/> pour que les tests n'aient pas besoin du réseau.
/// </summary>
internal interface IPackageSource
{
    /// <summary>Récupère l'archive <c>.tar.gz</c> du dépôt à l'étiquette <c>v&lt;version&gt;</c>.</summary>
    /// <param name="id">Le paquet.</param>
    /// <param name="version">Sa version exacte.</param>
    PackageFetch Fetch(PackageId id, string version);

    /// <summary>Les versions du dépôt : ses tags <c>vX.Y.Z</c>, du plus récent au plus ancien (les autres tags sont ignorés).</summary>
    PackageVersions Versions(PackageId id);
}

/// <summary>
/// Télécharge l'archive d'une version par HTTPS : GitHub, GitLab ou Codeberg. Rien n'est envoyé d'autre que la demande de l'archive (pas de
/// compte, pas de jeton) : un dépôt privé n'est pas pris en charge. L'archive passe par un fichier temporaire, jamais par la mémoire.
/// </summary>
internal sealed class HttpPackageSource : IPackageSource
{
    /// <summary>Taille maximale d'une archive téléchargée, en octets.</summary>
    public const long MaxArchiveBytes = 100L * 1024 * 1024;

    private readonly HttpClient _client;

    /// <param name="handler">Remplace la connexion réelle, pour les tests.</param>
    public HttpPackageSource(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromMinutes(2);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("rgl");
    }

    /// <summary>L'adresse de l'archive d'une version sur sa forge.</summary>
    public static string ArchiveUrl(PackageId id, string version)
    {
        var tag = PackageVersion.Tag(version);
        return id.Host switch
        {
            "github.com" => $"https://codeload.github.com/{id.Owner}/{id.Repository}/tar.gz/refs/tags/{tag}",
            "gitlab.com" => $"https://gitlab.com/{id.Owner}/{id.Repository}/-/archive/{tag}/{id.Repository}-{tag}.tar.gz",
            "codeberg.org" => $"https://codeberg.org/{id.Owner}/{id.Repository}/archive/{tag}.tar.gz",
            _ => throw new ArgumentException($"hôte non pris en charge : {id.Host}", nameof(id)),
        };
    }

    /// <summary>L'adresse de l'API qui liste les tags d'un dépôt sur sa forge (les cent premiers).</summary>
    public static string TagsUrl(PackageId id) => id.Host switch
    {
        "github.com" => $"https://api.github.com/repos/{id.Owner}/{id.Repository}/tags?per_page=100",
        "gitlab.com" => $"https://gitlab.com/api/v4/projects/{id.Owner}%2F{id.Repository}/repository/tags?per_page=100",
        "codeberg.org" => $"https://codeberg.org/api/v1/repos/{id.Owner}/{id.Repository}/tags?limit=50",
        _ => throw new ArgumentException($"hôte non pris en charge : {id.Host}", nameof(id)),
    };

    /// <inheritdoc/>
    public PackageVersions Versions(PackageId id)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TagsUrl(id));
            using var response = _client.Send(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new PackageVersions(null, $"« {id.Source} » n'existe pas sur {id.Host}", NotFound: true);
            if (!response.IsSuccessStatusCode)
                return new PackageVersions(null, $"{id.Host} a répondu {(int)response.StatusCode} ({response.ReasonPhrase})");

            using var document = System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStream());
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return new PackageVersions(null, $"{id.Host} n'a pas rendu une liste de tags");
            var versions = document.RootElement.EnumerateArray()
                .Select(tag => tag.ValueKind == System.Text.Json.JsonValueKind.Object && tag.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String ? PackageVersion.FromTag(name.GetString()!) : null)
                .OfType<string>().Distinct().ToList();
            versions.Sort((a, b) => PackageVersion.Compare(b, a));
            return new PackageVersions(versions, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new PackageVersions(null, $"liste des versions impossible à lire : {e.Message}");
        }
    }

    /// <inheritdoc/>
    public PackageFetch Fetch(PackageId id, string version)
    {
        var url = ArchiveUrl(id, version);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new PackageFetch(null, $"le tag {PackageVersion.Tag(version)} n'existe pas sur {id.Host}", NotFound: true);
            if (!response.IsSuccessStatusCode)
                return new PackageFetch(null, $"{id.Host} a répondu {(int)response.StatusCode} ({response.ReasonPhrase})");
            if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                return new PackageFetch(null, $"l'archive dépasse {MaxArchiveBytes / (1024 * 1024)} Mo");

            var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            try
            {
                using var body = response.Content.ReadAsStream();
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes)
                    {
                        file.Dispose();
                        return new PackageFetch(null, $"l'archive dépasse {MaxArchiveBytes / (1024 * 1024)} Mo");
                    }
                    file.Write(buffer, 0, read);
                }
                file.Position = 0;
                return new PackageFetch(file, null);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new PackageFetch(null, $"téléchargement impossible : {e.Message}");
        }
    }
}
