using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Les gears de chaînes de la std, écrits en un seul passage sur le texte : mêmes résultats qu'avant, en caractères Unicode (slice B.5).</summary>
public class StdStringsTests
{
    private static string Run(string body) => StdHarness.Body(body);

    [ZigFact]
    public void Un_rang_compte_des_caracteres_pas_des_octets()
    {
        var output = Run("""
            const string s: 'héllo wörld 😀 fin';
            std.writeLine(`{s.length}|{s.substring(1, 5)}|{s.substring(-3, 100)}|{s.substring(12, 13)}|{s.substring(7, 3)}|{s.substring(-100, 2)}`);
            std.writeLine(`{s.indexOf('wör')}|{s.indexOf('😀')}|{s.indexOf('absent')}|{s.indexOf('')}|{s.lastIndexOf('l')}|{s.lastIndexOf('')}`);
            std.writeLine(`{s[1]}|{s[-1]}|{s[12]}`);
            """);

        Assert.Equal("""
            17|éllo|fin|😀||hé
            6|12|null|0|9|17
            é|n|😀

            """.Replace("\r\n", "\n").TrimStart(), output.Replace("\r\n", "\n"));
    }

    [ZigFact]
    public void La_decoupe_la_jonction_et_le_remplacement_se_comportent_comme_avant()
    {
        var output = Run("""
            const string s: 'a, é, b,,c';
            const [string] parts: s.split(', ');
            std.writeLine(`{parts.length}|{parts.join('+')}|{s.split(',').length}|{s.split('').length}|{''.split(',').length}`);
            std.writeLine(`{s.replace(', ', ';')}|{s.replace('é', 'ee')}|{s.replace('', 'x')}|{s.replace('zzz', 'x')}`);
            std.writeLine(`{['a', 'b', 'c'].join()}|{['a'].join('-')}|{[1, 2, 3].join(', ')}`);
            std.writeLine(`{'ab'.repeat(3)}|{'é'.repeat(2)}|{''.repeat(4)}|{'x'.repeat(0)}`);
            """);

        Assert.Equal("""
            3|a+é+b,,c|5|1|1
            a;é;b,,c|a, ee, b,,c|a, é, b,,c|a, é, b,,c
            abc|a|1, 2, 3
            ababab|éé||

            """.Replace("\r\n", "\n").TrimStart(), output.Replace("\r\n", "\n"));
    }

    [ZigFact]
    public void Les_majuscules_ne_touchent_que_l_ASCII_et_l_inversion_garde_les_caracteres_entiers()
    {
        var output = Run("""
            std.writeLine(`{'héllo Wörld 1'.toUpper()}|{'HÉLLO wörld 1'.toLower()}|{'héllo😀'.reverse()}|{''.reverse()}|{'a'.reverse()}`);
            """);

        Assert.Equal("HéLLO WöRLD 1|hÉllo wörld 1|😀olléh||a\n", output.Replace("\r\n", "\n"));
    }

    [ZigFact]
    public void Un_texte_de_vingt_mille_lignes_se_traite_en_un_temps_raisonnable()
    {
        // Avant la slice : plus que quadratique, 20 000 lignes ne se terminaient pas en un temps raisonnable : le test ne finirait pas.
        var output = Run("""
            [string] lines: [0..<20000] ||> (i) => `ligne {i} : valeur {i * 3}`;
            const string text: lines.join(`\n`);
            const [string] parts: text.split(`\n`);
            const string upper: text.toUpper();
            const string replaced: text.replace('valeur', 'v');
            std.writeLine(`{text.length} {parts.length} {upper.length} {replaced.length} {text.indexOf('ligne 19999')} {text.substring(0, 5)}`);
            """);

        var expectedText = string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"ligne {i} : valeur {i * 3}"));
        Assert.Equal($"{expectedText.Length} 20000 {expectedText.Length} {expectedText.Replace("valeur", "v").Length} {expectedText.IndexOf("ligne 19999", StringComparison.Ordinal)} ligne\n", output.Replace("\r\n", "\n"));
    }
}
