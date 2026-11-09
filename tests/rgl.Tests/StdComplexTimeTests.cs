using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Les nombres complexes et la lecture d'une date selon un motif.</summary>
public class StdComplexTimeTests
{
    [ZigFact]
    public void Les_complexes_s_ecrivent_avec_les_operateurs()
    {
        var output = StdHarness.Main("""
            use <Complex> only {Complex, complex, magnitude, phase, conj, fromPolar, complexExp, complexPow, complexSqrt, complexText};

            gear Test(): void {
                const Complex a: complex(1.0, 2.0);
                const Complex b: complex(3.0, -1.0);
                std.writeLine(complexText(a + b));
                std.writeLine(complexText(a - b));
                std.writeLine(complexText(a * b));
                std.writeLine(complexText(a / b));
                std.writeLine(complexText(-a));
                std.writeLine(complexText(conj(a)));
                std.writeLine(magnitude(complex(3.0, 4.0)));
                std.writeLine(complexText(complex(5.0)));
                std.writeLine(complexText(complex(0.0, 2.0)));
                std.writeLine(complexText(complexPow(complex(0.0, 1.0), 2)));
                std.writeLine(complexText(complexPow(a, 0)));
                std.writeLine(complexText(complexPow(complex(2.0, 0.0), -2)));
                const Complex root: complexSqrt(complex(-4.0, 0.0));
                std.writeLine(`{root.re > -0.000001 and root.re < 0.000001} {root.im > 1.999999 and root.im < 2.000001}`);
                const Complex unit: complexExp(complex(0.0, 3.141592653589793));
                std.writeLine(`{unit.re > -1.000001 and unit.re < -0.999999} {unit.im > -0.000001 and unit.im < 0.000001}`);
                const Complex polar: fromPolar(2.0, 0.0);
                std.writeLine(complexText(polar));
                std.writeLine(phase(complex(0.0, 1.0)) > 1.5707 and phase(complex(0.0, 1.0)) < 1.5708);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("4.0+1.0i\n-2.0+3.0i\n5.0+5.0i\n0.1+0.7i\n-1.0-2.0i\n1.0-2.0i\n5.0\n5.0\n2.0i\n-1.0\n1.0\n0.25\ntrue true\ntrue true\n2.0\ntrue\n", output);
    }

    [ZigFact]
    public void Une_date_se_lit_selon_un_motif()
    {
        var output = StdHarness.Main("""
            gear show(string text, string pattern): void {
                const datetime? d: std.parseDateTime(text, pattern);
                std.writeLine(d eq null ? 'refusé' : `{d ?? std.now()}`);
            }

            gear Test(): void {
                show('24/10/2026 18:30', 'dd/MM/yyyy HH:mm');
                show('2026/10/24', 'yyyy/MM/dd');
                show('26-1-5', 'yy-M-d');
                show('26-01-05 07:08:09.045', 'yy-MM-dd HH:mm:ss.SSS');
                show('30/02/2026', 'dd/MM/yyyy');
                show('24/10/2026', 'dd-MM-yyyy');
                show('24/10/26', 'dd/MM/yyyy');
                show('24/10/2026 ', 'dd/MM/yyyy');
                show('24/10/2026', 'dd/MM/yyyy HH');
                show('abc', 'yyyy');
                show('', '');
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("2026-10-24T18:30:00Z\n2026-10-24T00:00:00Z\n2026-01-05T00:00:00Z\n2026-01-05T07:08:09.045Z\nrefusé\nrefusé\nrefusé\nrefusé\nrefusé\nrefusé\n1970-01-01T00:00:00Z\n", output);
    }
}
