using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Dictionnaires et tableaux : keys, values, remove, pop, insert, groupBy…</summary>
public class StdCollectionsTests
{
    [ZigFact]
    public void Les_dictionnaires()
    {
        var output = StdHarness.Body("""
            [string:int] ages: ['Zed': 40, 'Ana': 30];
            std.writeLine(ages.keys().join(','));
            std.writeLine(ages.values().join(','));
            std.writeLine(ages.put('Bob', 5).keys().join(','));
            std.writeLine(ages.without('Ana').length);
            std.writeLine(ages.length);
            mut [string:int] copie: ages;
            std.writeLine(copie.remove('Zed'));
            std.writeLine(copie.remove('Zed'));
            std.writeLine(copie.keys().join(','));
            std.writeLine(ages.entries().length);
            std.writeLine(ages.merge(['Ana': 1, 'New': 2]).values().join(','));
            std.writeLine(ages.filter((string k, int v) => v > 35).keys().join(','));
            std.writeLine(std.mapValues(ages, (int v) => v * 2).values().join(','));
            """);

        Assert.Equal("Ana,Zed\n30,40\nAna,Bob,Zed\n1\n2\ntrue\nfalse\nAna\n2\n1,2,40\nZed\n60,80\n", output);
    }

    [ZigFact]
    public void Les_tableaux()
    {
        var output = StdHarness.Body("""
            mut [int] a: [1, 2, 3, 4];
            std.writeLine(a.pop() ?? -1);
            std.writeLine(a.join(','));
            a.insert(1, 9);
            std.writeLine(a.join(','));
            std.writeLine(a.removeAt(0) ?? -1);
            std.writeLine(a.join(','));
            a.clear();
            std.writeLine(`len={a.length}`);
            std.writeLine([1, 2, 3, 4].windowed(2).length);
            std.writeLine(std.zipWith([1, 2, 3], [10, 20], (int x, int y) => x + y).join(','));
            std.writeLine(std.sortBy(['ccc', 'a', 'bb'], (string s) => s.length).join(','));
            std.writeLine(std.minBy(['ccc', 'a', 'bb'], (string s) => s.length) ?? '');
            std.writeLine(std.maxBy(['ccc', 'a', 'bb'], (string s) => s.length) ?? '');
            var groupes: std.groupBy([1, 2, 3, 4, 5], (int n) => n % 2);
            std.writeLine(groupes[1].join(','));
            std.writeLine(groupes[0].join(','));
            const ([int] left, [string] right) split: [(1, 'a'), (2, 'b')].unzip();
            std.writeLine(split.left.join(',') + split.right.join(','));
            """);

        Assert.Equal("4\n1,2,3\n1,9,2,3\n1\n9,2,3\nlen=0\n3\n11,22\na,bb,ccc\na\nccc\n1,3,5\n2,4\n1,2a,b\n", output);
    }
}
