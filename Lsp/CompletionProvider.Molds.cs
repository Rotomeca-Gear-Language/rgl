using Rgl.AST.Interfaces;
using Rgl.AST.Collections;
using Rgl.AST.Expressions;
using Rgl.AST.Gears;
using Rgl.AST.Molds;
using Rgl.AST.Statements;
using Rgl.AST.Variables;

namespace Rgl.Lsp;

/// <summary>
/// La création d'un mold : entre les accolades de <c>new Vec2 { … }</c> ou de <c>Vec2 p: { … }</c>, l'éditeur propose les champs qui restent à écrire,
/// les obligatoires (sans valeur par défaut, non nullables) d'abord, et une proposition qui écrit d'un coup tous les obligatoires.
/// </summary>
internal static partial class CompletionProvider
{
    /// <summary>
    /// Les champs du mold que la valeur en cours d'écriture doit encore nommer, ou <see langword="null"/> si le curseur n'est pas à la place d'un nom de champ
    /// (après <c>:</c>, hors accolades, accolades d'un bloc). Le type du mold est celui que l'on écrit après <c>new</c> ou, à défaut, celui que le contexte attend :
    /// déclaration typée, affectation, argument d'un gear, retour, champ d'un autre mold, élément d'un tableau.
    /// </summary>
    private static IReadOnlyList<CompletionItem>? _MoldFields(string path, string content, ProjectContext project, int offset, int wordStart)
    {
        var before = wordStart - 1;
        while (before >= 0 && char.IsWhiteSpace(content[before])) --before;
        if (before < 0 || content[before] is not ('{' or ',')) return null;

        // Le mot en cours est remplacé par un nom provisoire : la valeur existe dans l'arbre même si la ligne est inachevée.
        var text = content[..wordStart] + Placeholder + content[offset..];
        var document = SemanticDocument.Create(path, text, project);
        var nodes = document.PathAt(wordStart, inclusiveEnd: true);
        var index = nodes.FindLastIndex(n => n is MoldExpression mold && mold.Entries.OfType<MoldFieldValue>().Any(e => e.Name.Name == Placeholder));
        if (index < 0) return null;

        var expression = (MoldExpression)nodes[index];
        if (_ExpectedType(document, nodes, index, wordStart) is not { } type) return [];
        var written = expression.Entries.OfType<MoldFieldValue>().Where(e => e.Name.Name != Placeholder).Select(e => e.Name.Name).ToHashSet(StringComparer.Ordinal);
        var spreads = expression.Entries.OfType<MoldSpreadValue>().ToArray();

        var fields = document.MembersOf(SemanticDocument.StripNullable(type), wordStart).Where(m => m.Kind == RglSymbolKind.Field && !written.Contains(m.Name)).DistinctBy(m => m.Name).ToArray();
        if (fields.Length == 0) return [];

        var items = new List<CompletionItem>();
        var required = new List<RglSymbol>();
        foreach (var field in fields)
        {
            var isRequired = _IsRequired(field);
            if (isRequired) required.Add(field);
            var item = _Item(document, field, isRequired ? "0_" : "1_");
            items.Add(item with
            {
                Detail = (isRequired ? "obligatoire · " : "facultatif · ") + field.TypeText,
                InsertText = field.Name + ": $0",
                Kind = CompletionKind.Field,
            });
        }

        // Tous les champs obligatoires d'un coup, chacun avec son emplacement à remplir ; sans eux, tout si le mold a des spreads qui n'en fournissent pas.
        if (required.Count > 1 && spreads.Length == 0)
        {
            var snippet = string.Join(", ", required.Select((f, i) => $"{f.Name}: ${i + 1}"));
            items.Add(new CompletionItem("champs obligatoires", CompletionKind.Snippet, string.Join(", ", required.Select(f => f.Name)),
                "Écrit chaque champ sans valeur par défaut, dans l'ordre du mold.", "0", InsertText: snippet));
        }
        return items;
    }

    /// <summary>Un champ est obligatoire s'il n'a ni valeur par défaut ni type nullable ; un champ de type anonyme ou d'une erreur standard ne se juge pas.</summary>
    private static bool _IsRequired(RglSymbol field) =>
        field.Node is MoldField { Default: null } declared && !declared.Type.Span.Text.TrimEnd().EndsWith('?');

    /// <summary>
    /// Le type que le contexte donne à l'expression <c>nodes[index]</c> : le type écrit d'un <c>new</c>, celui d'une déclaration ou d'un retour,
    /// du paramètre où l'expression est un argument, du champ qu'elle remplit, de l'élément d'un tableau attendu.
    /// </summary>
    private static string? _ExpectedType(SemanticDocument document, List<IAstNode> nodes, int index, int offset)
    {
        if (nodes[index] is MoldExpression { Type: { } written }) return Signatures.Collapse(written.Span.Text);
        if (index == 0) return null;

        var node = nodes[index];
        switch (nodes[index - 1])
        {
            case VarDeclaration { Type: { } declared }:
                return Signatures.Collapse(declared.Span.Text);

            case AssignmentStatement { TypeHint: { } hint }:
                return Signatures.Collapse(hint.Span.Text);

            case AssignmentStatement assignment:
                return document.ResolveName(assignment.Target.Name, offset) is { } target ? document.TypeOfSymbol(target) : null;

            case ReturnStatement:
                return nodes.OfType<GearDeclaration>().LastOrDefault() is { } gear ? Signatures.ReturnTextOf(gear) : null;

            case CallExpression { Callee: NameExpression callee } call:
            {
                var position = call.Arguments.ToList().FindIndex(a => ReferenceEquals(a, node));
                return position >= 0 && document.ResolveName(callee.Name.Name, offset) is { Node: GearDeclaration declaration } && position < declaration.Parameters.Count
                    ? Signatures.Collapse(declaration.Parameters[position].Type.Span.Text) : null;
            }

            case MoldFieldValue field when index >= 2 && nodes[index - 2] is MoldExpression
                && _ExpectedType(document, nodes, index - 2, offset) is { } outer:
                return document.MembersOf(SemanticDocument.StripNullable(outer), offset).FirstOrDefault(m => m.Kind == RglSymbolKind.Field && m.Name == field.Name.Name)?.TypeText;

            case ArrayExpression when _ExpectedType(document, nodes, index - 1, offset) is { } array:
                return SemanticDocument.ElementOf(array);

            default:
                return null;
        }
    }
}
