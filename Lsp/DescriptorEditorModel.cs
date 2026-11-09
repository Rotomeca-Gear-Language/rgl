using Rgl.AST.Descriptors;

namespace Rgl.Lsp;

/// <summary>Les signatures publiques des descriptors, avec les positions du fichier écrit par l'utilisateur.</summary>
internal static class DescriptorEditorModel
{
    public static IEnumerable<RglSymbol> Members(RglSymbol type, bool statics, IReadOnlyList<string>? arguments = null)
    {
        if (type.Node is not DescriptorDeclaration descriptor) yield break;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameters = descriptor.TypeParameters ?? [];
        for (var i = 0; i < Math.Min(parameters.Count, arguments?.Count ?? 0); i++) map[parameters[i].Name.Name] = arguments![i];
        foreach (var member in descriptor.Members)
        {
            if ((member.Kind is DescriptorMemberKind.Constructor or DescriptorMemberKind.Static) != statics) continue;
            var name = member.Kind == DescriptorMemberKind.Constructor ? "create" : member.Name.Name;
            var result = member.Kind == DescriptorMemberKind.Constructor ? descriptor.Name.Name + (parameters.Count == 0 ? "" : "<" + string.Join(", ", parameters.Select(p => p.Name.Name)) + ">") : member.Type!.Span.Text;
            yield return new RglSymbol(name, RglSymbolKind.Guard, member.Name.Span, member, type.Module, SemanticDocument.Substitute(result, map),
                Container: descriptor.Name.Name, Documentation: type.Module?.DocumentationFor(member.Span), TypeArguments: map);
            if (member.Kind == DescriptorMemberKind.Field && !member.IsConst)
                yield return new RglSymbol("set_" + name, RglSymbolKind.Guard, member.Name.Span, member, type.Module, "void", Container: descriptor.Name.Name, TypeArguments: map);
        }
    }

    public static IReadOnlyList<string> Parameters(RglSymbol symbol, DescriptorMember member) =>
        (member.Kind == DescriptorMemberKind.Field && symbol.Name.StartsWith("set_", StringComparison.Ordinal)
            ? new[] { member.Type!.Span.Text + " value" } : member.Parameters.Select(p => Signatures.Collapse(p.Span.Text)))
            .Select(p => symbol.TypeArguments is { } map ? SemanticDocument.Substitute(p, map) : p).ToArray();
}
