namespace Rgl.Build.Toolchain;

/// <summary>
/// Le runtime C++ (<c>runtime/include</c>, <c>runtime/src</c>, <c>runtime/third_party</c>) embarqué dans le compilateur : il est écrit une
/// fois par version du compilateur dans <c>&lt;cache&gt;/runtime-sources/&lt;identité&gt;</c> avec la même disposition que dans le dépôt,
/// pour qu'un exécutable distribué seul compile sans dossier <c>runtime/</c> à côté de lui.
/// </summary>
internal static class EmbeddedRuntime
{
    /// <summary>Préfixe du nom des ressources ; le chemin relatif au dossier <c>runtime/</c> suit, avec des « / ».</summary>
    public const string ResourcePrefix = "Rgl.Runtime/";

    private static readonly object _lock = new();

    /// <summary>Le dossier du runtime extrait ; <see langword="null"/> s'il n'est pas embarqué ou ne peut pas être écrit.</summary>
    /// <param name="cacheRoot">Racine du cache de RGL.</param>
    public static string? Extract(string cacheRoot)
    {
        var assembly = typeof(EmbeddedRuntime).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
        if (names.Length == 0) return null;

        var target = Path.Combine(cacheRoot, "runtime-sources", assembly.ManifestModule.ModuleVersionId.ToString("N"));
        var done = Path.Combine(target, ".complete");
        lock (_lock)
        {
            try
            {
                if (File.Exists(done)) return target;

                foreach (var name in names)
                {
                    var path = Path.Combine(target, name[ResourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var input = assembly.GetManifestResourceStream(name)!;
                    using var output = File.Create(path);
                    input.CopyTo(output);
                }
                File.WriteAllText(done, "");
                return target;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
