using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Encodages, points de code, CSV.</summary>
public class StdTextTests
{
    [ZigFact]
    public void Les_encodages()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.base64Encode('Bonjour' as bytes));
            std.writeLine(std.base64Encode('Ma' as bytes));
            const bytes? back: std.base64Decode('Qm9uam91cg==');
            const bytes fallback: '?' as bytes;
            const string? decoded: (back ?? fallback) as? string;
            std.writeLine(decoded ?? 'x');
            std.writeLine(std.base64Decode('@@') eq null);
            std.writeLine(std.hexEncode('Aé' as bytes));
            std.writeLine(std.hexDecode('4142') eq null);
            std.writeLine(std.urlEncode('a b&é'));
            std.writeLine(std.urlDecode('a+b%26%C3%A9') ?? 'x');
            std.writeLine(std.urlDecode('%zz') eq null);
            std.writeLine(std.codePoints('aé€').join(','));
            std.writeLine(std.fromCodePoint(8364) ?? '?');
            std.writeLine(std.fromCodePoint(55296) eq null);
            std.writeLine(std.compareText('a', 'b'));
            std.writeLine(std.levenshtein('kitten', 'sitting'));
            """);

        Assert.Equal("Qm9uam91cg==\nTWE=\nBonjour\ntrue\n41c3a9\nfalse\na%20b%26%C3%A9\na b&é\ntrue\n97,233,8364\n€\ntrue\n-1\n3\n", output);
    }

    [ZigFact]
    public void Le_csv()
    {
        var output = StdHarness.Body(""""
            const [[string]] rows: std.parseCsv("a,\"b,c\",d\n1,\"x \"\"y\"\"\",\n", ',');
            std.writeLine(rows.length);
            std.writeLine(rows[0].join('|'));
            std.writeLine(rows[1].join('|'));
            std.writeLine(rows[1].length);
            std.writeLine(std.toCsv(rows, ';'));
            """");

        Assert.Equal("2\na|b,c|d\n1|x \"y\"|\n3\na;b,c;d\n1;\"x \"\"y\"\"\";\n\n", output);
    }

    [ZigFact]
    public void Les_categories_et_la_mise_en_forme()
    {
        var output = StdHarness.Body("""
            std.writeLine('A'.isUpper());
            std.writeLine('a'.isUpper());
            std.writeLine('a'.isLower());
            std.writeLine('7'.isAlphaNumeric());
            std.writeLine('!'.isPunctuation());
            std.writeLine('a'.isPunctuation());
            std.writeLine('{0}-{1}-{0}'.format('x', 'y'));
            std.writeLine(3.14159.toFixed(2));
            std.writeLine(2.5.toFixed(0));
            std.writeLine((-0.004).toFixed(2));
            std.writeLine((7).toFixed(3));
            std.writeLine((255).toHex());
            std.writeLine((5).toBinary());
            std.writeLine((-255).toRadix(16));
            std.writeLine((35).toRadix(36));
            """);

        Assert.Equal("true\nfalse\ntrue\ntrue\ntrue\nfalse\nx-y-x\n3.14\n3\n0.00\n7.000\nff\n101\n-ff\nz\n", output);
    }
}
