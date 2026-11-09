using System.Text.RegularExpressions;
using Rgl.Diagnostics;
using Rgl.Lsp;

namespace Rgl.Docs;

/// <summary>
/// Vérifie les documentations contre les déclarations qu'elles décrivent : noms des paramètres, références, résultat, échecs, résumés manquants.
/// Tout est signalé en avertissement ou en notice : une documentation fausse ne fait jamais échouer la compilation.
/// </summary>
internal static partial class DocValidator
{
    [GeneratedRegex(@"<[^>]*>|\([^)]*\)")]
    private static partial Regex _Decoration();

    /// <summary>Vérifie toutes les entrées ; <paramref name="entries"/> contient celles de tous les fichiers du programme, pour résoudre les références.</summary>
    /// <param name="entries">Les déclarations de tous les fichiers.</param>
    /// <param name="diagnostics">Reçoit les avertissements et notices.</param>
    /// <param name="onlyModules">Si donné, seuls les commentaires de ces fichiers sont vérifiés (les autres ne servent qu'à résoudre les références).</param>
    public static void Validate(IReadOnlyList<DocEntry> entries, DiagnosticBag diagnostics, IReadOnlySet<string>? onlyModules = null)
    {
        var known = _Names(entries);
        foreach (var entry in entries)
        {
            if (onlyModules is not null && !onlyModules.Contains(entry.Module)) continue;
            _Validate(entry, entries, known, diagnostics);
        }
    }

    /// <summary>Les noms qu'une référence peut écrire : <c>nom</c>, <c>Type.membre</c>, <c>Fichier.nom</c>, <c>Fichier.Type.membre</c>.</summary>
    private static HashSet<string> _Names(IReadOnlyList<DocEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var local = entry.Container is null ? entry.Name : entry.Container + "." + entry.Name;
            names.Add(local);
            names.Add(entry.Module + "." + local);
            if (entry.Container is null) names.Add(entry.Name);
        }
        return names;
    }

    private static void _Validate(DocEntry entry, IReadOnlyList<DocEntry> all, HashSet<string> known, DiagnosticBag diagnostics)
    {
        var comment = entry.Comment;
        if (comment is null || !comment.IsValid)
        {
            if (comment is null && entry.IsPublic && entry.Kind != DocKind.Extension) _MissingSummary(entry, diagnostics);
            return;
        }

        if (entry.IsPublic && comment.Summary is null && entry.Kind != DocKind.Extension) _MissingSummary(entry, diagnostics);

        var target = entry.Target;
        foreach (var parameter in comment.Params.Where(p => !target.Parameters.Contains(p.Name)))
            diagnostics.Warning(parameter.Span, DocCodes.UnknownParam, $"« {parameter.Name} » n'est pas un paramètre ({_Describe(entry)})", "paramètre inconnu",
                target.Parameters.Count == 0 ? ["cette déclaration n'a aucun paramètre"] : [$"les paramètres sont : {string.Join(", ", target.Parameters)}"]);
        foreach (var parameter in comment.TypeParams.Where(p => !target.TypeParameters.Contains(p.Name)))
            diagnostics.Warning(parameter.Span, DocCodes.UnknownTypeParam, $"« {parameter.Name} » n'est pas un paramètre générique ({_Describe(entry)})", "paramètre générique inconnu",
                target.TypeParameters.Count == 0 ? ["cette déclaration n'est pas générique"] : [$"les paramètres génériques sont : {string.Join(", ", target.TypeParameters)}"]);

        // Décrire un seul paramètre sans les autres est un oubli ; ne rien décrire est un choix.
        if (entry.IsPublic)
        {
            if (comment.Params.Count > 0)
                foreach (var missing in target.Parameters.Where(n => comment.Params.All(p => p.Name != n)))
                    diagnostics.Info(comment.Span, DocCodes.UndocumentedParam, $"le paramètre « {missing} » n'est pas décrit ({_Describe(entry)})", "ici",
                        [$"ajoute <param name=\"{missing}\">…</param>"]);
            if (comment.TypeParams.Count > 0)
                foreach (var missing in target.TypeParameters.Where(n => comment.TypeParams.All(p => p.Name != n)))
                    diagnostics.Info(comment.Span, DocCodes.UndocumentedParam, $"le paramètre générique « {missing} » n'est pas décrit ({_Describe(entry)})", "ici",
                        [$"ajoute <typeparam name=\"{missing}\">…</typeparam>"]);
        }

        if (comment.Returns is { } returns && !target.ReturnsValue)
            diagnostics.Info(returns.Span, DocCodes.Returns, $"<returns> inutile : aucune valeur n'est produite ({_Describe(entry)})", "ici");

        foreach (var fails in comment.Fails)
        {
            if (entry.Kind is not (DocKind.Guard or DocKind.Train))
                diagnostics.Warning(fails.Span, DocCodes.Fails, $"<fails> inutile : seuls un guard ou un train peuvent échouer ({_Describe(entry)})", "ici",
                    entry.Kind == DocKind.Gear ? ["un gear ne peut pas échouer ; déclare un guard si c'est le cas"] : null);
            else if (!_Resolves(fails.Type, entry, known))
                diagnostics.Warning(fails.Span, DocCodes.Fails, $"le type d'erreur « {fails.Type} » n'existe pas", "type inconnu",
                    ["écris le nom d'un mold ou d'une erreur (error) du projet ou de la std"]);
        }

        foreach (var reference in comment.References)
            if (!_Resolves(reference.Cref, entry, known))
                diagnostics.Warning(reference.Span, DocCodes.Cref, $"la référence « {reference.Cref} » ne désigne aucun symbole connu", "symbole inconnu",
                    ["écris le nom d'un gear, d'un type, d'un champ (Type.champ) ou d'un fichier importé (Fichier.nom)"]);
    }

    private static void _MissingSummary(DocEntry entry, DiagnosticBag diagnostics) =>
        diagnostics.Info(entry.NameSpan, DocCodes.MissingSummary, $"pas de résumé pour {_Describe(entry)}, qui est public", "ici",
            ["écris au-dessus de lui un commentaire /// qui le décrit"]);

    private static bool _Resolves(string cref, DocEntry from, HashSet<string> known)
    {
        var name = _Decoration().Replace(cref, "").Trim();
        if (name.StartsWith("std.", StringComparison.Ordinal)) name = name[4..];
        if (name.Length == 0) return false;
        if (known.Contains(name) || from.Target.Parameters.Contains(name) || from.Target.TypeParameters.Contains(name)) return true;
        // Un mot du langage (int, string, gear…) se décrit lui-même.
        if (KeywordDocumentation.For(name) is not null) return true;
        // Un membre du même type se nomme sans lui : « North » dans la documentation de « South ».
        return from.Container is { } container && known.Contains(container + "." + name);
    }

    private static string _Describe(DocEntry entry) => entry.Kind switch
    {
        DocKind.Gear => $"gear « {entry.Name} »",
        DocKind.Guard => $"guard « {entry.Name} »",
        DocKind.Emit => $"emit « {entry.Name} »",
        DocKind.Train => $"train « {entry.Name} »",
        DocKind.Mold => $"mold « {entry.Name} »",
        DocKind.Error => $"error « {entry.Name} »",
        DocKind.Enum => $"enum « {entry.Name} »",
        DocKind.Ingot => $"ingot « {entry.Name} »",
        DocKind.Constant => $"constante « {entry.Name} »",
        DocKind.Extension => $"extend « {entry.Name} »",
        DocKind.Field => $"champ « {entry.Name} »",
        _ => $"variante « {entry.Name} »",
    };
}
