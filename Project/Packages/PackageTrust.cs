using System.Text.Json;
using Rgl.Diagnostics;

namespace Rgl.Project.Packages;

/// <summary>
/// La clé <c>package-trust</c> du manifeste du projet : ce que le projet autorise à certains de ses paquets, désignés par leur adresse.
/// <c>{"binaries": ["github.com/alice/json"], "cpp": ["github.com/alice/json"]}</c>. Seul le projet autorise : l'autorisation ne s'étend pas aux
/// dépendances du paquet, et un paquet ne se l'accorde pas à lui-même.
/// </summary>
/// <param name="Binaries">Les paquets qui gardent leurs binaires de bibliothèques (<c>dll so dylib a lib obj o</c>) à l'extraction.</param>
/// <param name="Cpp">Les paquets dont le C++ (blocs <c>cpp</c>, <c>.rgl.cpp</c>, descriptors, <c>forge: native</c>) est admis.</param>
internal sealed record PackageTrust(IReadOnlySet<string> Binaries, IReadOnlySet<string> Cpp)
{
    /// <summary>Aucune autorisation : la même instance pour tous les manifestes qui n'en écrivent pas (l'égalité d'un enregistrement compare les références).</summary>
    public static readonly PackageTrust None = new(new HashSet<string>(), new HashSet<string>());

    private static readonly string[] _keys = ["binaries", "cpp"];

    /// <summary>Lit la clé <c>package-trust</c> ; absente, <see cref="None"/>. Les erreurs sont E157.</summary>
    /// <returns>Si tout est valide, et ce que le projet autorise.</returns>
    public static (bool Ok, PackageTrust Trust) Read(JsonElement root, string path, DiagnosticBag diagnostics)
    {
        if (!root.TryGetProperty("package-trust", out var element)) return (true, None);
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Error("la clé « package-trust » est un objet : {\"binaries\": [adresses], \"cpp\": [adresses]}", path, DiagnosticCodes.InvalidPackage);
            return (false, None);
        }

        var ok = true;
        var lists = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { ["binaries"] = [], ["cpp"] = [] };
        foreach (var property in element.EnumerateObject())
        {
            if (!_keys.Contains(property.Name))
            {
                diagnostics.Error($"package-trust : « {property.Name} » est inconnu (clés : {string.Join(", ", _keys)})", path, DiagnosticCodes.InvalidPackage);
                ok = false;
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            {
                diagnostics.Error($"package-trust.{property.Name} est un tableau d'adresses de paquets, par exemple [\"github.com/alice/json\"]", path, DiagnosticCodes.InvalidPackage);
                ok = false;
                continue;
            }
            foreach (var entry in property.Value.EnumerateArray())
            {
                if (PackageId.Parse(entry.GetString()!, out var error) is { } id) lists[property.Name].Add(id.Source);
                else
                {
                    diagnostics.Error($"package-trust.{property.Name} : {error}", path, DiagnosticCodes.InvalidPackage);
                    ok = false;
                }
            }
        }
        return (ok, ok ? new PackageTrust(lists["binaries"], lists["cpp"]) : None);
    }
}
