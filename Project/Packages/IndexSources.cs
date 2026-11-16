using System.Text.Json;

namespace Rgl.Project.Packages;

/// <summary>Ce que rend une demande de l'index : son texte, ou la raison de l'échec.</summary>
internal sealed record IndexFetch(string? Json, string? Error);

/// <summary>Le seul moyen qu'a le compilateur d'obtenir l'index, injecté par <see cref="Cli.CliContext"/> pour que les tests n'aient pas besoin du réseau.</summary>
internal interface IIndexSource
{
    /// <summary>Télécharge l'index.</summary>
    IndexFetch Fetch();
}

/// <summary>Les fichiers d'un gist à une révision : nom → contenu, ou la raison de l'échec.</summary>
internal sealed record GistFetch(IReadOnlyDictionary<string, string>? Files, string? Error);

/// <summary>Le seul moyen qu'a le compilateur d'obtenir un gist, injecté comme <see cref="IIndexSource"/>.</summary>
internal interface IGistSource
{
    /// <summary>Lit les fichiers du gist <paramref name="id"/> à la révision <paramref name="revision"/>.</summary>
    GistFetch Fetch(string id, string revision);
}

/// <summary>
/// Télécharge l'index par HTTPS, à l'adresse de la variable d'environnement <c>RGL_INDEX_URL</c> ou, sans elle, <see cref="DefaultUrl"/> : rien n'est envoyé d'autre que la demande.
/// </summary>
internal sealed class HttpIndexSource : IIndexSource
{
    /// <summary>L'adresse de l'index publié (GitHub Pages du dépôt d'index).</summary>
    public const string DefaultUrl = "https://rotomeca.github.io/rgl-index/index.json";

    /// <summary>Taille maximale de l'index, en octets.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly string _url;

    /// <param name="handler">Remplace la connexion réelle, pour les tests.</param>
    /// <param name="url">Remplace l'adresse de l'index.</param>
    public HttpIndexSource(HttpMessageHandler? handler = null, string? url = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("rgl");
        var fromEnvironment = Environment.GetEnvironmentVariable("RGL_INDEX_URL");
        _url = url ?? (string.IsNullOrEmpty(fromEnvironment) ? DefaultUrl : fromEnvironment);
    }

    /// <inheritdoc/>
    public IndexFetch Fetch()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            using var response = _client.Send(request);
            if (!response.IsSuccessStatusCode) return new IndexFetch(null, $"{_url} a répondu {(int)response.StatusCode} ({response.ReasonPhrase})");
            if (response.Content.Headers.ContentLength > MaxBytes) return new IndexFetch(null, "l'index est trop gros");
            using var stream = response.Content.ReadAsStream();
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxBytes) return new IndexFetch(null, "l'index est trop gros");
            }
            return new IndexFetch(new System.Text.UTF8Encoding(false).GetString(memory.ToArray()), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new IndexFetch(null, $"téléchargement impossible : {e.Message}");
        }
    }
}

/// <summary>Lit un gist par l'API de GitHub, à une révision précise : le contenu de cette révision ne change jamais.</summary>
internal sealed class HttpGistSource : IGistSource
{
    /// <summary>Taille maximale de l'ensemble des fichiers d'un gist, en octets.</summary>
    public const int MaxBytes = 1024 * 1024;

    private readonly HttpClient _client;

    /// <param name="handler">Remplace la connexion réelle, pour les tests.</param>
    public HttpGistSource(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("rgl");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>L'adresse de l'API pour un gist à une révision.</summary>
    public static string Url(string id, string revision) => $"https://api.github.com/gists/{id}/{revision}";

    /// <inheritdoc/>
    public GistFetch Fetch(string id, string revision)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(id, revision));
            using var response = _client.Send(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new GistFetch(null, "ce gist ou cette révision n'existe pas (ou le gist est privé)");
            if (!response.IsSuccessStatusCode) return new GistFetch(null, $"GitHub a répondu {(int)response.StatusCode} ({response.ReasonPhrase})");

            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return new GistFetch(null, "la réponse de GitHub n'a pas de fichiers");

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var file in files.EnumerateObject())
            {
                if (file.Value.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
                    return new GistFetch(null, $"le fichier « {file.Name} » est trop gros pour être lu");
                var content = file.Value.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
                total += content.Length;
                if (total > MaxBytes) return new GistFetch(null, "le gist dépasse 1 Mo");
                result[file.Name] = content;
            }
            return new GistFetch(result, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or JsonException)
        {
            return new GistFetch(null, $"lecture du gist impossible : {e.Message}");
        }
    }
}
