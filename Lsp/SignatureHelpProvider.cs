using Rgl.Syntax;

namespace Rgl.Lsp;

/// <summary>La signature d'un appel en cours d'écriture.</summary>
/// <param name="Label">La signature en une ligne : <c>clamp&lt;T&gt;(T v, T lo, T hi): T</c>.</param>
/// <param name="Parameters">Chaque paramètre dans <paramref name="Label"/> : début et fin.</param>
/// <param name="Active">Le paramètre qu'on écrit.</param>
/// <param name="Documentation">Le commentaire du gear.</param>
/// <param name="ParameterDocumentation">Ce que la documentation <c>///</c> dit de chaque paramètre (<c>&lt;param&gt;</c>), <see langword="null"/> pour un paramètre non décrit.</param>
internal sealed record SignatureResult(string Label, IReadOnlyList<(int Start, int End)> Parameters, int Active, string? Documentation,
    IReadOnlyList<string?>? ParameterDocumentation = null, IReadOnlyList<SignatureResult>? Alternatives = null);

/// <summary>
/// L'aide à la saisie d'un appel : la signature du gear appelé, avec le paramètre qu'on est en train d'écrire. Elle remonte du
/// curseur jusqu'à la parenthèse ouverte de l'appel, compte les virgules, et lit le nom qui précède la parenthèse.
/// </summary>
internal static class SignatureHelpProvider
{
    /// <summary>La signature de l'appel qui contient <paramref name="offset"/> ; <see langword="null"/> hors d'un appel connu.</summary>
    public static SignatureResult? At(SemanticDocument document, int offset)
    {
        var tokens = document.Self.Tokens;
        var last = -1;
        for (var i = tokens.Count - 1; i >= 0; --i)
        {
            if (tokens[i].Kind != TokenKind.EndOfFile && tokens[i].Span.End.Value <= offset)
            {
                last = i;
                break;
            }
        }

        var (depth, commas, open) = (0, 0, -1);
        for (var i = last; i >= 0 && open < 0; --i)
        {
            switch (tokens[i].Kind)
            {
                case TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace:
                    ++depth;
                    break;

                case TokenKind.OpenParen when depth == 0:
                    open = i;
                    break;

                case TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace:
                    if (depth == 0) return null;
                    --depth;
                    break;

                case TokenKind.Comma when depth == 0:
                    ++commas;
                    break;

                case TokenKind.Semicolon when depth == 0:
                    return null;
            }
        }
        if (open < 1) return null;

        // gear<T>(…) : on saute les arguments génériques.
        var name = open - 1;
        if (tokens[name].Kind == TokenKind.Greater)
        {
            while (name >= 0 && tokens[name].Kind != TokenKind.Less) --name;
            --name;
        }
        if (name < 0 || tokens[name].Kind != TokenKind.Identifier) return null;

        RglSymbol? symbol;
        var first = name;
        if (name >= 2 && tokens[name - 1].Kind == TokenKind.Dot && tokens[name - 2].Kind == TokenKind.Identifier)
        {
            first = name - 2;
            var names = document.QualifiedNames(tokens[name - 2].Text);
            symbol = names is not null && names.TryGetValue(tokens[name].Text, out var member) ? member : null;
        }
        else symbol = document.ResolveName(tokens[name].Text, (int)tokens[name].Span.Start.Value);

        symbol ??= document.TargetAt((int)tokens[name].Span.Start.Value)?.Symbol;

        if (symbol is { Kind: RglSymbolKind.Method, Name: "free", Node: null })
            return new SignatureResult("free(): void", [], 0, symbol.Documentation);
        if (symbol is null && tokens[name].Text == "pack")
            return new SignatureResult("pack(tableau)", [(5, 12)], 0, "Copie un tableau en représentation contiguë pour le code natif.");
        if (symbol is not { IsCallable: true } && symbol is not { Kind: RglSymbolKind.Ingot }) return null;
        if (symbol.Kind == RglSymbolKind.Ingot)
            return new SignatureResult($"{symbol.Name}(valeur)", [(symbol.Name.Length + 1, symbol.Name.Length + 7)], 0, symbol.Documentation);

        // v |> f(a) : la valeur qui entre par le pipeline est le premier argument.
        var piped = first > 0 && tokens[first - 1].Kind is TokenKind.Pipe or TokenKind.EachPipe or TokenKind.EachStrictPipe or TokenKind.FoldPipe;
        var choices = symbol.Module?.Overloads.GetValueOrDefault(symbol.Name) ?? [symbol];
        var results = choices.Select(Format).ToArray();
        return results[0] with { Alternatives = results[1..] };

        SignatureResult Format(RglSymbol choice)
        {
            var (callName, parameters, returnType) = Signatures.Call(choice);
            var active = Math.Min(commas + (piped ? 1 : 0), Math.Max(parameters.Count - 1, 0));

            var label = callName + "(";
            var spans = new List<(int, int)>();
            for (var i = 0; i < parameters.Count; ++i)
            {
                if (i > 0) label += ", ";
                spans.Add((label.Length, label.Length + parameters[i].Length));
                label += parameters[i];
            }
            label += ")" + (returnType is null ? "" : ": " + returnType);
            return new SignatureResult(label, spans, active, choice.Documentation, _ParameterDocumentation(choice, parameters));
        }
    }

    /// <summary>La description <c>&lt;param&gt;</c> de chaque paramètre, retrouvé par son nom (le dernier mot avant une éventuelle valeur par défaut).</summary>
    private static IReadOnlyList<string?>? _ParameterDocumentation(RglSymbol symbol, IReadOnlyList<string> parameters)
    {
        if (symbol.Module is null || symbol.Node is null || symbol.Module.DocCommentFor(symbol.Node.Span) is not { Params.Count: > 0 } comment) return null;
        return [.. parameters.Select(text => Docs.DocRenderer.ParameterText(comment, text.Split(':')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? ""))];
    }
}
