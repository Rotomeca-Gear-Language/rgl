using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>La file à deux bouts et le cache à éviction du moins récemment utilisé.</summary>
public class StdDequeLruTests
{
    [ZigFact]
    public void La_file_a_deux_bouts_ajoute_et_retire_des_deux_cotes()
    {
        var output = StdHarness.Main("""
            use <Structures> only {Deque, newDeque};

            gear show(Deque<int> deque): string => deque.toArray().join(',');

            gear Test(): void {
                mut Deque<int> d: newDeque<int>();
                std.writeLine(d.isEmpty());
                std.writeLine((d.popFront() ?? -1) + (d.popBack() ?? -1));
                d.pushBack(3);
                d.pushBack(4);
                d.pushFront(2);
                d.pushFront(1);
                d.pushBack(5);
                std.writeLine(show(d));
                std.writeLine(`{d.size()} {d.peekFront() ?? -1} {d.peekBack() ?? -1}`);
                std.writeLine(d.popFront() ?? -1);
                std.writeLine(d.popBack() ?? -1);
                std.writeLine(show(d));
                std.writeLine(d.popBack() ?? -1);
                std.writeLine(d.popBack() ?? -1);
                std.writeLine(d.popBack() ?? -1);
                std.writeLine(d.popBack() ?? -1);
                std.writeLine(d.isEmpty());

                mut Deque<int> big: newDeque<int>();
                [1..100] ||> (i) => { big.pushBack(i); };
                [1..30] ||> (i) => { big.pushFront(0 - i); };
                std.writeLine(`{big.size()} {big.peekFront() ?? 0} {big.peekBack() ?? 0}`);
                std.writeLine(`{big.popFront() ?? 0} {big.popBack() ?? 0} {big.popFront() ?? 0} {big.popBack() ?? 0}`);
                [1..60] ||> (i) => { big.popFront(); };
                std.writeLine(`{big.size()} {big.peekFront() ?? 0} {big.peekBack() ?? 0}`);
                [1..30] ||> (i) => { big.popBack(); };
                std.writeLine(`{big.size()} {big.peekFront() ?? 0} {big.peekBack() ?? 0}`);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\n-2\n1,2,3,4,5\n5 1 5\n1\n5\n2,3,4\n4\n3\n2\n-1\ntrue\n130 -30 100\n-30 100 -29 99\n66 33 98\n36 33 68\n", output);
    }

    [ZigFact]
    public void Le_cache_retire_l_entree_la_moins_recemment_utilisee()
    {
        var output = StdHarness.Main("""
            use <Structures> only {LruCache, newLruCache};

            gear Test(): void {
                mut LruCache<string, int> cache: newLruCache<string, int>(2);
                cache.put('a', 1);
                cache.put('b', 2);
                std.writeLine(cache.keys().join(','));
                std.writeLine(cache.get('a') ?? -1);
                std.writeLine(cache.keys().join(','));
                cache.put('c', 3);
                std.writeLine(cache.keys().join(','));
                std.writeLine(`{cache.has('a')} {cache.has('b')} {cache.has('c')}`);
                std.writeLine(cache.get('b') ?? -1);
                cache.put('c', 30);
                std.writeLine(`{cache.size()} {cache.peek('c') ?? -1} {cache.keys().join(',')}`);
                std.writeLine(cache.peek('a') ?? -1);
                std.writeLine(cache.keys().join(','));
                std.writeLine(cache.remove('a'));
                std.writeLine(cache.remove('zzz'));
                std.writeLine(`{cache.size()} {cache.keys().join(',')}`);
                mut LruCache<int, string> small: newLruCache<int, string>(0);
                small.put(1, 'x');
                small.put(2, 'y');
                std.writeLine(`{small.size()} {small.get(1) ?? '-'} {small.get(2) ?? '-'}`);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("a,b\n1\nb,a\na,c\ntrue false true\n-1\n2 30 a,c\n1\na,c\ntrue\nfalse\n1 c\n1 - y\n", output);
    }
}
