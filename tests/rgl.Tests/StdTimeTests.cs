using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Emit;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary>Les dates de la std (<c>Time.forge</c>, <c>time_ext.forge</c>) dans de vrais programmes.</summary>
public class StdTimeTests
{
    private static string Run(string body)
    {
        var main = "gear Test(): void {\n" + body + "\n}\nemit Fin(): uint => 0;\n!train |> Test |> Fin;";
        using var directory = new TempDirectory();
        var diagnostics = new DiagnosticBag();
        var source = new SourceText(Path.Combine(directory.Path, "main.rgl"), main);
        var modules = SourceGraph.Load(source, directory.Path, diagnostics);
        var program = modules is null ? null : new Binder(diagnostics).Bind(modules);
        Assert.True(program is not null, "programme invalide : " + string.Join(", ", diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Format("."))));

        var cpp = Path.Combine(directory.Path, "main.cpp");
        var emitted = CppEmitter.Emit(program, "main.rgl");
        File.WriteAllText(cpp, emitted.Code);
        var executable = ZigBuild.CompileProgram(directory.Path, "programme", emitted, cpp,
            Path.Combine(ZigBuild.Runtime, "src", "arguments.cpp"), Path.Combine(ZigBuild.Runtime, "src", "console.cpp"),
            Path.Combine(ZigBuild.Runtime, "src", "string.cpp"));
        var result = ZigBuild.RunWithInput(executable, null, "un argument");
        Assert.Equal(0, result.Code);
        return result.Output.Replace("\r\n", "\n");
    }

    [ZigFact]
    public void Une_date_se_construit_de_ses_morceaux_ou_est_null()
    {
        var output = Run("""
            datetime zero: 0 as datetime;
            std.writeLine(std.dateTime(2026, 10, 24, 10, 30, 15, 250) ?? zero);
            std.writeLine(std.dateTime(2000, 2, 29) ?? zero);
            std.writeLine(std.dateTime(1969, 12, 31, 23, 59, 59, 999) ?? zero);
            std.writeLine(std.dateTime(2026, 2, 30) eq null);
            std.writeLine(std.dateTime(2026, 13, 1) eq null);
            std.writeLine(std.dateTime(2026, 1, 1, 24) eq null);
            std.writeLine(std.dateTime(1900, 2, 29) eq null);
            std.writeLine(std.isLeapYear(2000));
            std.writeLine(std.isLeapYear(1900));
            std.writeLine(std.daysInMonth(2024, 2));
            std.writeLine(std.daysInMonth(2023, 2));
            std.writeLine(std.daysInMonth(2023, 4));
            """);

        Assert.Equal("2026-10-24T10:30:15.250Z\n2000-02-29T00:00:00Z\n1969-12-31T23:59:59.999Z\ntrue\ntrue\ntrue\ntrue\ntrue\nfalse\n29\n28\n30\n", output);
    }

    [ZigFact]
    public void Une_date_iso_8601_se_lit_avec_sa_zone()
    {
        var output = Run("""
            datetime zero: 0 as datetime;
            std.writeLine(std.parseDateTime('2026-10-24') ?? zero);
            std.writeLine(std.parseDateTime('2026-10-24T10:30') ?? zero);
            std.writeLine(std.parseDateTime('2026-10-24T10:30:00+02:00') ?? zero);
            std.writeLine(std.parseDateTime('2026-10-24 10:30:00.5Z') ?? zero);
            std.writeLine(std.parseDateTime('2026-10-24T10:30:00.123456-05:30') ?? zero);
            std.writeLine(std.parseDateTime('abc') eq null);
            std.writeLine(std.parseDateTime('2026-13-01') eq null);
            std.writeLine(std.parseDateTime('2026-10-24T10:30:00+25:00') eq null);
            std.writeLine(std.parseDateTime('2026-10-24T10:30:00X') eq null);
            std.writeLine(std.parseDateTime('2026-10-24T10') eq null);
            std.writeLine(std.parseDateTime('2026-10-24T10:30:00.') eq null);
            datetime d: 1700000000123 as datetime;
            std.writeLine(std.parseDateTime(`{d}`) eq d);
            """);

        Assert.Equal("2026-10-24T00:00:00Z\n2026-10-24T10:30:00Z\n2026-10-24T08:30:00Z\n2026-10-24T10:30:00.500Z\n2026-10-24T16:00:00.123Z\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\n", output);
    }

    [ZigFact]
    public void Une_date_donne_ses_morceaux()
    {
        var output = Run("""
            datetime d: 1700000000000 as datetime;
            std.writeLine(`{d.year()}-{d.month()}-{d.day()} {d.hour()}:{d.minute()}:{d.second()}.{d.millisecond()}`);
            std.writeLine(`{d.dayOfWeek()} {d.dayOfYear()} {d.startOfDay()}`);
            datetime avant: -1 as datetime;
            std.writeLine(`{avant.year()}-{avant.month()}-{avant.day()} {avant.hour()}:{avant.minute()} {avant.dayOfWeek()} {avant.dayOfYear()} {avant.startOfDay()}`);
            datetime bissextile: 951782400000 as datetime;
            std.writeLine(`{bissextile.dayOfYear()} {bissextile.dayOfWeek()}`);
            std.writeLine((d + 3h).hour());
            """);

        Assert.Equal("2023-11-14 22:13:20.0\n2 318 2023-11-14T00:00:00Z\n1969-12-31 23:59 3 365 1969-12-31T00:00:00Z\n60 2\n1\n", output);
    }

    [ZigFact]
    public void L_instant_present_est_recent_et_croit()
    {
        var output = Run("""
            datetime a: std.now();
            datetime b: std.now();
            std.writeLine(a as long > 1700000000000 and b >= a and b - a < 1min);
            std.writeLine(a.year() >= 2025);
            """);

        Assert.Equal("true\ntrue\n", output);
    }
}
