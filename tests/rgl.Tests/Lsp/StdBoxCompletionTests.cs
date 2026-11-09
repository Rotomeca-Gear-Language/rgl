using Rgl.Lsp;

namespace Rgl.Tests.Lsp;

/// <summary>Les noms de la std que l'éditeur propose à importer : les boxes dont seul l'index existe (Geometry) et rien de privé.</summary>
public class StdBoxCompletionTests
{
    /// <summary>Les propositions à la place de <c>¤</c> dans le corps d'un gear d'un fichier forge.</summary>
    private static IReadOnlyList<CompletionItem> Complete(string beforeCursor)
    {
        using var directory = new TempDirectory();
        directory.File("rgl.json", "{ \"name\": \"p\", \"version\": \"1.0.0\", \"rgl\": \"0\" }");
        var text = "forge: gears\ndescription: ''\n\ngear nom(int n): int {\n    " + beforeCursor + "\n    return n;\n}\n";
        var path = directory.File("test.forge", text);
        var line = text[..text.IndexOf(beforeCursor, StringComparison.Ordinal)].Count(c => c == '\n');
        return CompletionProvider.Complete(path, text, (uint)line, (uint)(4 + beforeCursor.Length), new ProjectIndex().Open(path, _ => null));
    }

    [Fact]
    public void Une_box_de_la_std_sans_fichier_de_son_nom_se_propose_et_donne_ses_molds()
    {
        var geometry = Assert.Single(Complete("Geo"), i => i.Label == "Geometry");
        Assert.Equal("use <Geometry>", geometry.Detail);

        var rect = Assert.Single(Complete("Rec"), i => i.Label == "Rect");
        Assert.Contains("Geometry", rect.Source);
        Assert.Contains(rect.Edits!, edit => edit.Text.Contains("use <Geometry> only {Rect};", StringComparison.Ordinal));
    }

    [Fact]
    public void Les_noms_d_une_box_imbriquee_se_proposent_aussi()
    {
        Assert.Contains(Complete("Vector2"), i => i.Label == "Vector2");
    }

    [Fact]
    public void Les_noms_prives_de_la_std_ne_se_proposent_pas()
    {
        Assert.DoesNotContain(Complete("Rec"), i => i.Label.StartsWith('_'));
        Assert.DoesNotContain(Complete("re"), i => i.Label.StartsWith('_'));
    }
}
