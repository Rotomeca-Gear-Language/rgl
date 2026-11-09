using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Piles, files, ensembles et files de priorité.</summary>
public class StdStructuresTests
{
    [ZigFact]
    public void Les_structures()
    {
        var output = StdHarness.Main("""
            use <Structures> only {Stack, Queue, Set, PriorityQueue, newStack, newQueue, newSet, newPriorityQueue};
            gear Test(): void {
            mut Stack<int> pile: newStack<int>();
            pile.push(1);
            pile.push(2);
            std.writeLine(pile.peek() ?? -1);
            std.writeLine(pile.pop() ?? -1);
            std.writeLine(pile.size());
            mut Queue<string> file: newQueue<string>();
            file.enqueue('a');
            file.enqueue('b');
            file.enqueue('c');
            std.writeLine(file.dequeue() ?? '?');
            std.writeLine(file.dequeue() ?? '?');
            std.writeLine(file.size());
            std.writeLine(file.peek() ?? '?');
            mut Set<int> ens: newSet<int>();
            std.writeLine(ens.add(3));
            std.writeLine(ens.add(3));
            ens.add(1);
            std.writeLine(ens.values().join(','));
            std.writeLine(ens.has(1));
            std.writeLine(ens.remove(1));
            std.writeLine(ens.size());
            mut PriorityQueue<int> tas: newPriorityQueue<int>((int a, int b) => a - b);
            tas.push(5);
            tas.push(1);
            tas.push(3);
            std.writeLine(tas.pop() ?? -1);
            std.writeLine(tas.pop() ?? -1);
            std.writeLine(tas.size());
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("2\n2\n1\na\nb\n1\nc\ntrue\nfalse\n1,3\ntrue\ntrue\n1\n1\n3\n1\n", output);
    }
}
