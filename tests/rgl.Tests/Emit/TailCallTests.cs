using Rgl.Tests.Runtime;

namespace Rgl.Tests.Emit;

/// <summary>La récursion terminale s'écrit en boucle : sans garde de profondeur ni pile, et sans changer le résultat (slice B.4d).</summary>
public class TailCallTests
{
    private const string Sum = """
        gear pure sum(long acc, int n): long {
            if (n eq 0) { return acc; }
            return sum(acc + n, n - 1);
        }

        emit run(): uint {
            return (sum(0, 100000) % 1000) as uint;
        }

        !train |> run;
        """;

    [Fact]
    public void Une_recursion_terminale_est_une_boucle_sans_garde()
    {
        var cpp = CppEmitterTests.Emit(Sum);

        Assert.Contains("for (;;) {", cpp);
        Assert.Contains("continue;", cpp);
        Assert.DoesNotContain("recursion_guard", cpp);
        Assert.DoesNotContain("rgl_g_sum(rgl_p_acc", cpp);   // plus d'appel récursif
    }

    [ZigFact]
    public void Cent_mille_tours_depassent_la_limite_de_200_sans_s_arreter()
    {
        // 1 + 2 + … + 100000 = 5000050000, modulo 1000 : 0.
        Assert.Equal(0, CppEmitterTests.CompileAndRun(Sum).Code);
    }

    [ZigFact]
    public void Les_arguments_sont_tous_calcules_avant_d_etre_affectes()
    {
        // swap(1, 2, 3) : (1,2) → (2,1) → (1,2) → (2,1) : a vaut 2.
        Assert.Equal(2, CppEmitterTests.CompileAndRun("""
            gear pure swap(int a, int b, int n): int {
                if (n eq 0) { return a; }
                return swap(b, a, n - 1);
            }

            emit run(): uint {
                return swap(1, 2, 3) as uint;
            }

            !train |> run;
            """).Code);
    }

    [ZigFact]
    public void Un_tableau_passe_tel_quel_n_est_pas_recopie_a_chaque_tour()
    {
        Assert.Equal(21, CppEmitterTests.CompileAndRun("""
            gear pure total([int] values, int i, long acc): long {
                if (i >= values.length) { return acc; }
                return total(values, i + 1, acc + values[i]);
            }

            emit run(): uint {
                [int] values: [1, 2, 3, 4, 5, 6];
                return total(values, 0, 0) as uint;
            }

            !train |> run;
            """).Code);
    }

    [Fact]
    public void Une_recursion_non_terminale_garde_son_garde()
    {
        var cpp = CppEmitterTests.Emit("""
            gear pure fib(int n): long {
                if (n < 2) { return n; }
                return fib(n - 1) + fib(n - 2);
            }

            emit run(): uint { return fib(5) as uint; }

            !train |> run;
            """);

        Assert.Contains("recursion_guard", cpp);
        Assert.DoesNotContain("for (;;)", cpp);
    }

    [Fact]
    public void Une_recursion_mutuelle_garde_ses_gardes()
    {
        var cpp = CppEmitterTests.Emit("""
            gear pure isEven(int n): bool {
                if (n eq 0) { return true; }
                return isOdd(n - 1);
            }

            gear pure isOdd(int n): bool {
                if (n eq 0) { return false; }
                return isEven(n - 1);
            }

            emit run(): uint { return isEven(10) ? 1 : 0; }

            !train |> run;
            """);

        Assert.Contains("recursion_guard", cpp);
        Assert.DoesNotContain("for (;;)", cpp);
    }

    [Fact]
    public void Un_appel_terminal_dans_une_lambda_n_est_pas_une_boucle()
    {
        var cpp = CppEmitterTests.Emit("""
            gear pure count(int n): int {
                if (n eq 0) { return 0; }
                const pure (int) => int f: (int k) => count(k - 1);
                return f(n);
            }

            emit run(): uint { return count(3) as uint; }

            !train |> run;
            """);

        Assert.Contains("recursion_guard", cpp);
    }
}
