using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>La normalisation Unicode de la std.</summary>
public class StdUnicodeTests
{
    [ZigFact]
    public void Les_quatre_formes_et_les_accents()
    {
        var output = StdHarness.Body("""
            const string decomposed: "é";
            const string composed: std.normalizeNfc(decomposed);
            std.writeLine(composed.length);
            std.writeLine(composed eq 'é');
            std.writeLine(std.normalizeNfd('é').length);
            std.writeLine(std.isNfc('é'));
            std.writeLine(std.isNfc(decomposed));
            std.writeLine(std.normalizeNfkc('ﬁn ① Ａ'));
            std.writeLine(std.normalizeNfkd('ﬁ').length);
            std.writeLine(std.stripAccents("Héros à l'école, naïve Zoë"));
            std.writeLine(std.equalsIgnoringAccents('Éléphant', 'elephant'));
            std.writeLine(std.equalsIgnoringAccents('Éléphant', 'elephent'));
            std.writeLine(std.normalizeNfc('한').length);
            std.writeLine(std.normalizeNfd('한').length);
            """);

        Assert.Equal("1\ntrue\n2\ntrue\nfalse\nfin 1 A\n2\nHeros a l'ecole, naive Zoe\ntrue\nfalse\n1\n3\n", output);
    }
}
