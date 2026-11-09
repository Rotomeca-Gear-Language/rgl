using System.Text;

namespace Rgl.Docs;

/// <summary>Le squelette du commentaire de documentation d'une déclaration, écrit quand on tape <c>///</c> au-dessus d'elle.</summary>
internal static class DocSkeleton
{
    /// <summary>
    /// Ce qui suit le <c>///</c> déjà tapé, comme un snippet de l'éditeur : le résumé, puis une balise pour chaque paramètre générique,
    /// chaque paramètre, le résultat (s'il y en a un) et, pour un guard ou un train, un échec. Chaque trou est un arrêt de tabulation.
    /// </summary>
    public static string For(DocEntry entry)
    {
        var stop = 1;
        var text = new StringBuilder($" ${{{stop++}:Résumé.}}");
        var target = entry.Target;
        var tags = new List<string>();

        foreach (var name in target.TypeParameters) tags.Add($"<typeparam name=\"{name}\">${stop++}</typeparam>");
        foreach (var name in target.Parameters) tags.Add($"<param name=\"{name}\">${stop++}</param>");
        if (target.ReturnsValue) tags.Add($"<returns>${stop++}</returns>");
        if (entry.Kind is DocKind.Guard or DocKind.Train) tags.Add($"<fails type=\"${{{stop++}:Erreur}}\">${stop++}</fails>");

        if (tags.Count == 0) return text.ToString();
        text.Append("\n///");
        foreach (var tag in tags) text.Append("\n/// ").Append(tag);
        return text.ToString();
    }
}
