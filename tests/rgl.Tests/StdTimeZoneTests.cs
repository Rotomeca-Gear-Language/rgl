using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Les fuseaux horaires de la std : décalage, heure d'été, conversions dans les deux sens.</summary>
public class StdTimeZoneTests
{
    [ZigFact]
    public void Les_decalages_et_les_passages_a_l_heure_d_ete_sont_ceux_de_2026()
    {
        var output = StdHarness.Main("""
            use <TimeZone> only {TimeZone, timeZone, utcZone, formatIso};

            gear at(string iso): datetime => std.parseDateTime(iso) ?? std.now();
            gear zone(string name): TimeZone => timeZone(name) ?? utcZone();
            gear show(string iso, string name): void { std.writeLine(formatIso(at(iso), zone(name))); }

            gear Test(): void {
                show('2026-07-01T12:00:00Z', 'Europe/Paris');
                show('2026-01-15T12:00:00Z', 'Europe/Paris');
                show('2026-03-29T00:59:59Z', 'Europe/Paris');
                show('2026-03-29T01:00:00Z', 'Europe/Paris');
                show('2026-10-25T00:59:59Z', 'Europe/Paris');
                show('2026-10-25T01:00:00Z', 'Europe/Paris');
                show('2026-03-08T06:59:59Z', 'America/New_York');
                show('2026-03-08T07:00:00Z', 'America/New_York');
                show('2026-11-01T05:59:59Z', 'America/New_York');
                show('2026-11-01T06:00:00Z', 'America/New_York');
                show('2026-10-03T15:59:59Z', 'Australia/Sydney');
                show('2026-10-03T16:00:00Z', 'Australia/Sydney');
                show('2026-04-04T15:59:59Z', 'Australia/Sydney');
                show('2026-04-04T16:00:00Z', 'Australia/Sydney');
                show('2026-01-10T00:00:00Z', 'Australia/Sydney');
                show('2026-09-26T13:59:59Z', 'Pacific/Auckland');
                show('2026-09-26T14:00:00Z', 'Pacific/Auckland');
                show('2026-07-01T12:00:00Z', 'Asia/Kolkata');
                show('2026-07-01T12:00:00Z', 'UTC');
                show('2026-07-01T12:00:00.250Z', 'asia/tokyo');
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "2026-07-01T14:00:00+02:00\n2026-01-15T13:00:00+01:00\n"
            + "2026-03-29T01:59:59+01:00\n2026-03-29T03:00:00+02:00\n2026-10-25T02:59:59+02:00\n2026-10-25T02:00:00+01:00\n"
            + "2026-03-08T01:59:59-05:00\n2026-03-08T03:00:00-04:00\n2026-11-01T01:59:59-04:00\n2026-11-01T01:00:00-05:00\n"
            + "2026-10-04T01:59:59+10:00\n2026-10-04T03:00:00+11:00\n2026-04-05T02:59:59+11:00\n2026-04-05T02:00:00+10:00\n2026-01-10T11:00:00+11:00\n"
            + "2026-09-27T01:59:59+12:00\n2026-09-27T03:00:00+13:00\n"
            + "2026-07-01T17:30:00+05:30\n2026-07-01T12:00:00Z\n2026-07-01T21:00:00.250+09:00\n", output);
    }

    [ZigFact]
    public void Les_noms_les_decalages_fixes_et_les_conversions_inverses()
    {
        var output = StdHarness.Main("""
            use <TimeZone> only {TimeZone, timeZone, utcZone, fixedOffset, localTime, inZone, fromZone, formatIso, zoneNames, LocalTime};

            gear at(string iso): datetime => std.parseDateTime(iso) ?? std.now();
            gear zone(string name): TimeZone => timeZone(name) ?? utcZone();

            gear Test(): void {
                std.writeLine((fixedOffset(-90) ?? utcZone()).name);
                std.writeLine(zone('UTC+2').offsetMinutes);
                std.writeLine(zone('+05:30').offsetMinutes);
                std.writeLine(zone('-0800').offsetMinutes);
                std.writeLine(zone('europe/paris').name);
                std.writeLine(timeZone('Mars/Base') eq null);
                std.writeLine(fixedOffset(2000) eq null);
                std.writeLine(zoneNames().length > 50);

                const TimeZone paris: zone('Europe/Paris');
                const LocalTime t: localTime(paris, at('2026-07-01T12:00:00Z'));
                std.writeLine(`{t.hour} {t.dayOfWeek} {t.daylight} {t.offsetMinutes} {t.zone}`);
                std.writeLine(`{localTime(paris, at('2026-01-15T12:00:00Z')).daylight}`);
                std.writeLine(formatIso(at('2026-07-01T12:00:00Z') + 0ms, zone('UTC')));
                std.writeLine(inZone(at('2026-07-01T12:00:00Z'), paris).hour());

                std.writeLine(formatIso(fromZone(paris, at('2026-07-01T14:00:00Z')), zone('UTC')));
                std.writeLine(formatIso(fromZone(paris, at('2026-01-15T14:00:00Z')), zone('UTC')));
                std.writeLine(formatIso(fromZone(paris, at('2026-10-25T02:30:00Z')), zone('UTC')));
                std.writeLine(formatIso(fromZone(paris, at('2026-03-29T02:30:00Z')), zone('UTC')));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "-01:30\n120\n330\n-480\nEurope/Paris\ntrue\ntrue\ntrue\n"
            + "14 3 true 120 Europe/Paris\nfalse\n2026-07-01T12:00:00Z\n14\n"
            + "2026-07-01T12:00:00Z\n2026-01-15T13:00:00Z\n2026-10-25T00:30:00Z\n2026-03-29T01:30:00Z\n", output);
    }
}
