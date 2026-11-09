using Rgl.AST.Extensions;
using Rgl.Diagnostics;
using Rgl.AST.Files;
using Rgl.Syntax;
using Rgl.Text;

namespace Rgl.Lsp;

/// <summary>Un changement de texte : de (ligne, colonne) à (ligne, colonne), en unités UTF-16 à partir de 0.</summary>
internal sealed record CodeEdit(uint StartLine, uint StartCharacter, uint EndLine, uint EndCharacter, string Text);

/// <summary>Une action rapide : son titre, sa sorte du protocole (<c>quickfix</c>, <c>source.organizeImports</c>) et ses changements dans le document.</summary>
internal sealed record CodeAction(string Title, string Kind, IReadOnlyList<CodeEdit> Edits, bool IsPreferred = false);

/// <summary>
/// Les actions rapides (l'ampoule de l'éditeur, Ctrl+;) : importer le nom que rien ne désigne et que le projet ou la std déclare, ajouter l'en-tête ou la
/// description qui manque à un fichier <c>.forge</c>, retirer les imports que rien n'emploie (aussi « Organiser les imports »).
/// </summary>
internal static class CodeActionProvider
{
    public const string QuickFix = "quickfix";
    public const string OrganizeImports = "source.organizeImports";

    /// <summary>Les actions pour la plage <paramref name="start"/>–<paramref name="end"/> (positions dans le texte), sachant les codes de diagnostic qui la touchent.</summary>
    /// <param name="only">Les sortes demandées par l'éditeur ; <see langword="null"/> pour toutes.</param>
    public static IReadOnlyList<CodeAction> At(SemanticDocument document, int start, int end, IReadOnlyCollection<string> diagnosticCodes, IReadOnlyCollection<string>? only = null)
    {
        bool Wants(string kind) => only is null || only.Count == 0 || only.Any(o => kind == o || kind.StartsWith(o + ".", StringComparison.Ordinal));
        var actions = new List<CodeAction>();

        if (Wants(QuickFix))
        {
            actions.AddRange(_Imports(document, start, end));
            if (diagnosticCodes.Contains(DiagnosticCodes.MissingDescription) && _AddDescription(document) is { } description) actions.Add(description);
            if (diagnosticCodes.Contains(DiagnosticCodes.MissingHeader) && document.Self.Path.EndsWith(".forge", StringComparison.OrdinalIgnoreCase))
                actions.Add(new CodeAction("Ajouter l'en-tête « forge: gears »", QuickFix, [new CodeEdit(0, 0, 0, 0, "forge: gears" + _Eol(document) + "description: ''" + _Eol(document) + _Eol(document))]));

            foreach (var unused in UnusedImports(document).Where(u => u.Declaration.Span.Start.Value <= (uint)end && (uint)start <= u.Declaration.Span.End.Value))
                actions.Add(new CodeAction(unused.WholeImport ? "Supprimer l'import inutilisé" : $"Retirer {string.Join(", ", unused.Names.Select(n => "« " + n.LocalName.Name + " »"))} de l'import", QuickFix, [_RemovalEdit(document, unused)]));
        }

        if (Wants(OrganizeImports) && UnusedImports(document) is { Count: > 0 } all)
            actions.Add(new CodeAction("Supprimer les imports inutilisés", OrganizeImports, [.. all.Select(u => _RemovalEdit(document, u))]));
        return actions;
    }

    private static string _Eol(SemanticDocument document) => document.Source.Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    #region Importer un nom inconnu

    /// <summary>Pour chaque nom écrit dans la plage que rien ne désigne : l'import du fichier du projet, ou de la std, qui le déclare.</summary>
    private static IEnumerable<CodeAction> _Imports(SemanticDocument document, int start, int end)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in document.Self.Tokens)
        {
            if (token.Kind != TokenKind.Identifier || token.Span.End.Value < (uint)start || (uint)end < token.Span.Start.Value) continue;
            var at = (int)token.Span.Start.Value;
            if (at > 0 && document.TokenContaining(at - 1) is { Kind: TokenKind.Dot or TokenKind.At or TokenKind.Colon }) continue;
            if (document.TargetAt(at)?.Symbol is not null || document.ResolveName(token.Text, at) is not null || !done.Add(token.Text)) continue;

            foreach (var module in document.Project.Modules)
            {
                if (module.Exported.TryGetValue(token.Text, out var symbol) && symbol.IsTopLevel)
                    yield return new CodeAction($"Importer « {token.Text} » depuis {module.Name}", QuickFix, [_Insert(CompletionProvider.ImportEdit(document, module.Name, symbol.Name))], IsPreferred: true);
            }
            foreach (var module in document.Project.Std ?? [])
            {
                if (module.Exported.TryGetValue(token.Text, out var symbol) && symbol.IsTopLevel)
                    yield return new CodeAction($"Importer « {token.Text} » depuis la std ({module.Name})", QuickFix, [_Insert(CompletionProvider.ImportEdit(document, module.Name, symbol.Name, std: true))]);
            }
        }
    }

    private static CodeEdit _Insert(TextInsertion insertion) => new(insertion.Line, insertion.Character, insertion.Line, insertion.Character, insertion.Text);

    private static CodeAction? _AddDescription(SemanticDocument document)
    {
        if (document.Unit.Header is not { Kind: { IsMissing: false } kind } header || header.Description is not null) return null;
        var source = document.Source;
        var position = source[kind.Span.End];
        var line = position.Line.Value - 1;
        var column = kind.Span.End.Value - source.LineStart[(int)line].Value;
        return new CodeAction("Ajouter « description: '' »", QuickFix, [new CodeEdit(line, column, line, column, _Eol(document) + "description: ''")], IsPreferred: true);
    }

    #endregion

    #region Imports inutilisés

    /// <summary>Un import dont rien n'est employé : en entier, ou seulement certains des noms de son <c>only</c>.</summary>
    internal sealed record UnusedImport(ImportDeclaration Declaration, bool WholeImport, IReadOnlyList<ImportName> Names);

    /// <summary>
    /// Les imports que rien n'emploie : un nom de <c>only</c> qui n'apparaît nulle part hors des imports, un fichier importé en entier dont le nom n'apparaît nulle part
    /// (<c>Fichier.nom</c>, <c>Fichier(…)</c>). Sont laissés de côté ce qui peut servir sans être écrit : un fichier qui contient des extensions (elles s'appliquent
    /// sans qu'on les nomme), les imports de boîtes, de dossiers d'outils et de fichiers de données.
    /// </summary>
    public static IReadOnlyList<UnusedImport> UnusedImports(SemanticDocument document)
    {
        var imports = document.Unit.Imports.ToList();
        if (imports.Count == 0) return [];

        // Les noms écrits hors des imports (les exports comptent comme des emplois).
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in document.Self.Tokens)
        {
            if (token.Kind != TokenKind.Identifier || imports.Any(i => i.Span.Start.Value <= token.Span.Start.Value && token.Span.End.Value <= i.Span.End.Value)) continue;
            used.Add(token.Text);
        }

        var result = new List<UnusedImport>();
        foreach (var import in imports)
        {
            if (import.Kind is not (ImportKind.Plain or ImportKind.Outside or ImportKind.Std)) continue;
            var module = import.Kind == ImportKind.Std ? document.Project.FindStd(import.Module.Name) : document.Project.Find(import.Module.Name);
            if (module is null || module.Unit.Statements.OfType<ExtendDeclaration>().Any() || module.Kind == "extend") continue;

            if (import.Only is { } only)
            {
                var unused = only.Where(n => !n.LocalName.IsMissing && !used.Contains(n.LocalName.Name)).ToList();
                if (unused.Count == 0) continue;
                result.Add(new UnusedImport(import, unused.Count == only.Count, unused));
            }
            else if (!used.Contains(import.LocalModuleName))
            {
                result.Add(new UnusedImport(import, true, []));
            }
        }
        return result;
    }

    /// <summary>Le changement qui retire l'import (la ligne entière si rien d'autre n'y est écrit) ou seulement ses noms inutilisés.</summary>
    private static CodeEdit _RemovalEdit(SemanticDocument document, UnusedImport unused)
    {
        var source = document.Source;
        (uint Line, uint Column) Place(uint offset)
        {
            var line = source[Rotomeca.Utils.Types.RUint.CreateChecked(offset)].Line.Value - 1;
            return (line, offset - source.LineStart[(int)line].Value);
        }

        if (!unused.WholeImport)
        {
            var kept = unused.Declaration.Only!.Where(n => !unused.Names.Contains(n)).ToList();
            var (startLine, startColumn) = Place(unused.Declaration.Only![0].Span.Start.Value);
            var (endLine, endColumn) = Place(unused.Declaration.Only![^1].Span.End.Value);
            return new CodeEdit(startLine, startColumn, endLine, endColumn, string.Join(", ", kept.Select(n => n.Span.Text)));
        }

        var span = unused.Declaration.Span;
        var first = Place(span.Start.Value);
        var last = Place(span.End.Value);
        var lineText = source.Content.Split('\n')[(int)last.Line].TrimEnd('\r');
        var aloneBefore = source.Content.Split('\n')[(int)first.Line][..(int)first.Column].Trim().Length == 0;
        var aloneAfter = lineText[(int)Math.Min(last.Column, (uint)lineText.Length)..].Trim().Length == 0;
        if (aloneBefore && aloneAfter) return new CodeEdit(first.Line, 0, last.Line + 1, 0, "");
        return new CodeEdit(first.Line, first.Column, last.Line, last.Column, "");
    }

    #endregion
}
