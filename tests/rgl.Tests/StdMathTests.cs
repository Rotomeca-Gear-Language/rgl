using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Maths supplémentaires, durées, dates, async, résultats.</summary>
public class StdMathTests
{
    [ZigFact]
    public void Les_maths_supplementaires()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.isNaN(0.0 / 0.0 + 0.0));
            std.writeLine(std.isFinite(1.5));
            std.writeLine(std.isInfinity(1.5));
            std.writeLine(std.tanh(0.0));
            std.writeLine(std.modulo(-7, 3));
            std.writeLine(std.floorDiv(-7, 2));
            std.writeLine(std.ceilDiv(7, 2));
            std.writeLine(std.bitCount(255));
            std.writeLine(std.isPrime(97));
            std.writeLine(std.isPrime(91));
            std.writeLine(std.bigAdd('99999999999999999999', '1') ?? '?');
            std.writeLine(std.bigSub('5', '12') ?? '?');
            std.writeLine(std.bigMul('123456789012345678901234567890', '987654321') ?? '?');
            std.writeLine(std.bigFactorial(25));
            std.writeLine(std.bigPow('2', 100) ?? '?');
            const [string] none: [];
            const [string] qr: std.bigDivMod('100000000000000000000', '7') ?? none;
            std.writeLine(qr.join(' r '));
            std.writeLine(std.bigCompare('-5', '3') ?? 9);
            std.writeLine(std.bigAdd('12x', '1') eq null);
            """);

        Assert.Equal("true\ntrue\nfalse\n0.0\n2\n-4\n4\n8\ntrue\nfalse\n100000000000000000000\n-7\n121932631124828532112482853211126352690\n15511210043330985984000000\n1267650600228229401496703205376\n14285714285714285714 r 2\n-1\ntrue\n", output);
    }
}
