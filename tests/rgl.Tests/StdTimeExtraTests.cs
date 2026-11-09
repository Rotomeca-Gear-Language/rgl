using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Durées, calendrier, chronomètre, async, résultats.</summary>
public class StdTimeExtraTests
{
    [ZigFact]
    public void Les_durees_et_les_dates()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.seconds(90).totalMinutes());
            std.writeLine(std.hours(2).totalSeconds());
            std.writeLine((std.hours(1) + std.minutes(2) + std.seconds(3) + std.milliseconds(4)).toText());
            std.writeLine(std.milliseconds(0).toText());
            std.writeLine((std.seconds(0) - std.seconds(5)).toText());
            std.writeLine(std.days(1) eq std.hours(24));
            const datetime d: std.dateTime(2026, 1, 31, 10, 30) ?? std.now();
            std.writeLine(d.addMonths(1).format('dd/MM/yyyy HH:mm'));
            std.writeLine(d.addMonths(-2).format('yyyy-MM-dd'));
            std.writeLine(d.addYears(1).format('yyyy-MM-dd'));
            std.writeLine((std.dateTime(2024, 2, 29) ?? std.now()).addYears(1).format('yyyy-MM-dd'));
            std.writeLine(d.format('SSS ss'));
            const duration shift: std.localOffset(d);
            std.writeLine(d.toLocal().toUtc() eq d);
            const std.Stopwatch watch: std.startStopwatch();
            std.writeLine(std.elapsed(watch).isNegative());
            """);

        Assert.Equal("1.5\n7200.0\n1h 2min 3s 4ms\n0ms\n-5s\ntrue\n28/02/2026 10:30\n2025-11-30\n2027-01-31\n2025-02-28\n000 00\ntrue\nfalse\n", output);
    }
}
