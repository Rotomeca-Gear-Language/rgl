using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Découpe en mots et mini-gabarits de texte.</summary>
public class StdWordsTemplateTests
{
    [ZigFact]
    public void Un_texte_se_decoupe_en_mots()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.splitWords("Aujourd'hui, le héros peut-être ira à l'école !").join('|'));
            std.writeLine(std.splitWords('  a_b  42 -x- ').join('|'));
            std.writeLine(std.splitWords('').length);
            std.writeLine(std.splitWords('日本語 テスト').join('|'));
            """);

        Assert.Equal("Aujourd'hui|le|héros|peut-être|ira|à|l'école\na_b|42|x\n0\n日本語|テスト\n", output);
    }

    [ZigFact]
    public void Un_gabarit_se_remplit_et_garde_les_noms_inconnus()
    {
        var output = StdHarness.Body("""
            const [string:string] values: ['nom': 'Héros', 'niveau': '3', 'x': '{{nom}}'];
            std.writeLine(std.fillTemplate('Bonjour {{nom}}, niveau {{ niveau }} : {{inconnu}} {{x}} {{ouvert', values));
            std.writeLine(std.fillTemplate('rien', values));
            """);

        Assert.Equal("Bonjour Héros, niveau 3 : {{inconnu}} {{nom}} {{ouvert\nrien\n", output);
    }
}
