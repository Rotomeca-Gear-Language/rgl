using Rgl.AST.Files;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Un lien dans le document : la plage cliquable et le fichier qu'elle ouvre.</summary>
internal sealed record DocumentLink(SourceSpan Range, string Target, string Tooltip);

/// <summary>
/// Les liens des imports : le nom d'un fichier importé (<c>import Commandes;</c>, <c>use &lt;Math&gt;;</c>, <c>import box Database;</c>, <c>import json settings;</c>…)
/// ouvre ce fichier d'un clic, comme un chemin dans un import JavaScript.
/// </summary>
internal static class DocumentLinkProvider
{
    public static IReadOnlyList<DocumentLink> Of(SemanticDocument document)
    {
        var links = new List<DocumentLink>();
        foreach (var import in document.Unit.Imports)
        {
            if (import.Module.IsMissing || _TargetOf(document, import) is not { } target) continue;
            links.Add(new DocumentLink(import.Module.Span, target, $"Ouvrir {Path.GetFileName(target)}"));
        }
        return links;
    }

    private static string? _TargetOf(SemanticDocument document, ImportDeclaration import)
    {
        var name = import.Module.Name.Trim('"', '\'');
        switch (import.Kind)
        {
            case ImportKind.Plain or ImportKind.Outside:
                return (document.Project.Find(name) ?? document.Project.FindStd(name))?.Path;
            case ImportKind.Std:
                return document.Project.FindStd(name)?.Path;
            case ImportKind.Box:
                return _Find(document, name + ".box", directory: true) is { } box && File.Exists(Path.Combine(box, "index.forge")) ? Path.Combine(box, "index.forge") : null;
            case ImportKind.Json:
                return _Find(document, name + ".json", directory: false);
            case ImportKind.Xml:
                return _Find(document, name + ".xml", directory: false);
            case ImportKind.File or ImportKind.FileOutside:
                return _Find(document, name, directory: false);
            default:
                return null;
        }
    }

    /// <summary>Le premier fichier ou dossier de ce nom dans le projet, hors dossiers cachés et dossier de sortie, dans l'ordre alphabétique.</summary>
    private static string? _Find(SemanticDocument document, string name, bool directory)
    {
        var root = document.Project.Root;
        if (root.Length == 0 || !Directory.Exists(root)) return null;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden };
        var found = directory ? Directory.EnumerateDirectories(root, name, options) : Directory.EnumerateFiles(root, name, options);
        return found.Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/').StartsWith("out/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).FirstOrDefault();
    }
}
