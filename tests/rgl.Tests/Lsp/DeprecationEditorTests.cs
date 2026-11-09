using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Un gear <c>@deprecated</c> est barré à chaque emploi : jeton sémantique, proposition de complétion, survol et diagnostic W061.</summary>
public class DeprecationEditorTests
{
    private const string Source = "@deprecated(\"Utiliser nouveau()\")\ngear ancien(): int => 1;\ngear nouveau(): int => 2;\ngear f(): int => ancien() + nouveau();\n!train |> done;";

    private static SemanticDocument Open(string text) => SemanticDocument.Create("document.rgl", text, new ProjectContext("", "", []));

    [Fact]
    public void Seul_l_emploi_est_barre_pas_la_declaration()
    {
        var document = Open(Source);
        var data = SemanticTokensProvider.Encode(document);
        var deprecatedBit = 1 << Array.IndexOf(SemanticTokensProvider.Modifiers, "deprecated");
        var lines = Source.Split('\n');
        var (line, column) = (0, 0);
        var struck = new List<(int Line, string Text)>();
        for (var i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            column = data[i] == 0 ? column + data[i + 1] : data[i + 1];
            if ((data[i + 4] & deprecatedBit) != 0) struck.Add((line, lines[line].Substring(column, data[i + 2])));
        }

        Assert.Equal([(3, "ancien")], struck);
    }

    [Fact]
    public void La_completion_marque_le_gear_obsolete_et_pas_les_autres()
    {
        var text = Source.Replace("ancien() + nouveau()", "1") + "\ngear g(): int => a";
        var before = text.Split('\n');
        var items = CompletionProvider.Complete(text, (uint)before.Length - 1, (uint)before[^1].Length);

        Assert.True(items.First(i => i.Label == "ancien").Deprecated);
        Assert.False(items.First(i => i.Label == "nouveau").Deprecated);
    }

    [Fact]
    public void Le_survol_dit_le_message_d_obsolescence()
    {
        var document = Open(Source);
        var symbol = document.Self.Symbols["ancien"];

        Assert.Equal("Utiliser nouveau()", symbol.Deprecation);
        Assert.Contains("**Obsolète** : Utiliser nouveau()", HoverProvider.Describe(document, symbol));
        Assert.False(document.Self.Symbols["nouveau"].IsDeprecated);
    }
}
