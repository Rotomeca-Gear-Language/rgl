using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le tas binaire, les décimaux exacts, les aides de test et les clés extrêmes d'un dictionnaire.</summary>
public class StdHeapDecimalTests
{
    [ZigFact]
    public void Le_tas_rend_les_elements_dans_l_ordre_et_le_dictionnaire_donne_ses_cles_extremes()
    {
        var output = StdHarness.Main("""
            use <Structures> only {Heap, newHeap};

            gear Test(): void {
                mut Heap<int> heap: newHeap<int>((int a, int b) => a - b);
                [5, 3, 8, 1, 9, 2, 7, 3] ||> (n) => { heap.push(n); };
                std.writeLine(`{heap.size()} {heap.peek() ?? -1}`);
                const [int] sorted: [1..8] ||> (i) => heap.pop() ?? -1;
                std.writeLine(sorted.join(','));
                std.writeLine(`{heap.isEmpty()} {heap.pop() ?? -1}`);
                mut Heap<string> words: newHeap<string>((string a, string b) => a.length > b.length ? 1 : (a.length < b.length ? -1 : 0));
                words.push('ccc');
                words.push('a');
                words.push('bb');
                std.writeLine(`{words.pop() ?? '?'} {words.pop() ?? '?'} {words.pop() ?? '?'}`);

                mut Heap<int> big: newHeap<int>((int a, int b) => a - b);
                [1..60] ||> (i) => { big.push((i * 37) % 61); };
                const [int] all: [1..60] ||> (i) => big.pop() ?? -1;
                const bool ordered: all |+> true with (acc, n, i) => i eq 0 ? acc : (acc and all[i - 1] <= n);
                std.writeLine(`{ordered} {all[0]} {all[59]}`);

                const [int:string] dictionary: [30: 'c', 10: 'a', 20: 'b'];
                const [int:string] empty: [:];
                std.writeLine(`{dictionary.firstKey() ?? -1} {dictionary.lastKey() ?? -1} {empty.firstKey() ?? -1}`);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("8 1\n1,2,3,3,5,7,8,9\ntrue -1\na bb ccc\ntrue 1 60\n10 30 -1\n", output);
    }

    [ZigFact]
    public void Les_decimaux_sont_exacts()
    {
        var output = StdHarness.Main("""
            use <Decimal> only {Decimal, newDecimal, parseDecimal, decimalText, normalizeDecimal, roundDecimal, divide, decimalToDouble};

            gear show(string label, Decimal value): void { std.writeLine(`{label} {decimalText(value)}`); }
            gear d(string text): Decimal => parseDecimal(text) ?? newDecimal(0);

            gear Test(): void {
                show('somme', d('0.1') + d('0.2'));
                show('difference', d('1') - d('0.9'));
                show('produit', d('1.5') * d('2.25'));
                show('quotient', d('1') / d('3'));
                show('divide', divide(d('10'), d('4'), 2));
                show('negatif', -d('2.50'));
                show('normalise', normalizeDecimal(d('1.500')));
                show('arrondi', roundDecimal(d('2.675'), 2));
                show('arrondi negatif', roundDecimal(d('-2.5'), 0));
                show('arrondi plus large', roundDecimal(d('1.5'), 3));
                show('signe plus', d('+7.05'));
                show('zero', d('0.00'));
                std.writeLine(`{d('1.50') eq d('1.5')} {d('1.5') < d('1.51')} {d('2') > d('1.999')} {d('-1') < d('1')}`);
                std.writeLine(`{parseDecimal('abc') eq null} {parseDecimal('') eq null} {parseDecimal('1.2.3') eq null} {parseDecimal('.') eq null} {parseDecimal('1234567890123456789') eq null}`);
                std.writeLine(decimalToDouble(d('0.25')));
                std.writeLine((d('19.99') * newDecimal(3)) eq d('59.97'));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "somme 0.3\ndifference 0.1\nproduit 3.375\nquotient 0.3333333333\ndivide 2.50\nnegatif -2.50\nnormalise 1.5\narrondi 2.68\narrondi negatif -3\narrondi plus large 1.500\nsigne plus 7.05\nzero 0.00\n"
            + "true true true true\ntrue true true true true\n0.25\ntrue\n", output);
    }

    [ZigFact]
    public void Les_aides_de_test_fixtures_journal_d_appels_et_mesure()
    {
        var output = StdHarness.Main("""
            use <Test> only {withFixture, benchmark, CallLog, newCallLog, recordCall, callCount, assertCalled};

            gear Test(): void {
                withFixture<int>(() => 41, (int n) => std.writeLine(n + 1), (int n) => std.writeLine(`nettoyé {n}`));

                mut CallLog log: newCallLog();
                recordCall(log, 'envoyer', 'a');
                recordCall(log, 'envoyer', 'b');
                recordCall(log, 'fermer');
                std.writeLine(`{callCount(log, 'envoyer')} {callCount(log, 'fermer')} {callCount(log, 'rien')} {log.calls.join(';')}`);
                assertCalled(log, 'envoyer', 2);
                assertCalled(log, 'fermer', 1);

                const duration total: benchmark('boucle', 1000, () => { const long x: 1 + 2; });
                std.writeLine(total as long >= 0);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.StartsWith("42\nnettoyé 41\n2 1 0 envoyer(a);envoyer(b);fermer()\nboucle : ", output);
        Assert.EndsWith(" ms pour 1000 itérations\ntrue\n", output);
    }
}
