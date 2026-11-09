using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Aides sur les résultats et les nullables, timeout et retry.</summary>
public class StdResultsAsyncTests
{
    [ZigFact]
    public void Les_resultats_et_les_nullables()
    {
        var output = StdHarness.Main("""
            use <Results> only {isOk, valueOf, errorOf, mapOk, mapError, andThen, orElse, mapNullable, flatMapNullable, filterNullable};

            gear pure verifier(int v): int ! string {
                if (v > 3) { return ok(v); }
                return err('petit');
            }

            gear Test(): void {
                int ! string good: ok(4);
                int ! string bad: err('non');
                std.writeLine(isOk(good));
                std.writeLine(isOk(bad));
                std.writeLine(valueOf(good) ?? -1);
                std.writeLine(errorOf(bad) ?? '?');
                std.writeLine(valueOf(mapOk(good, (int v) => v * 2)) ?? -1);
                std.writeLine(errorOf(mapError(bad, (string e) => e.length as int)) ?? -1);
                std.writeLine(valueOf(andThen(good, verifier)) ?? -1);
                std.writeLine(orElse(bad, (string e) => 0));
                int? some: 5;
                int? none: null;
                std.writeLine(mapNullable(some, (int v) => v + 1) ?? -1);
                std.writeLine(mapNullable(none, (int v) => v + 1) ?? -1);
                std.writeLine(flatMapNullable(some, (int v) => v > 9 ? v : null) ?? -1);
                std.writeLine(filterNullable(some, (int v) => v eq 5) ?? -1);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\nfalse\n4\nnon\n8\n3\n4\n0\n6\n-1\n-1\n5\n", output);
    }

    [ZigFact]
    public void Timeout_et_retry()
    {
        var output = StdHarness.Main("""
            use <Async> only {timeout, retry};
            use <Errors> only {AsyncException, AsyncKind};

            @async
            gear lent(): int {
                await sleep(300ms);
                return 7;
            }

            @async
            gear rapide(): int {
                await sleep(2ms);
                return 3;
            }

            @async
            emit principal(): uint {
                var vite: await timeout(rapide(), 200ms);
                int v: match (vite) { ok n |> n; err e |> -1; };
                `{v} ` -> console;
                var lenteur: await timeout(lent(), 20ms);
                string k: match (lenteur) { ok n |> 'ok'; err e |> e.kind eq AsyncKind.Timeout ? 'timeout' : 'autre'; };
                `{k} ` -> console;
                var essais: await retry(3, 1ms, () => {
                    'essai ' -> console;
                    return timeout(lent(), 5ms);
                });
                string f: match (essais) { ok n |> 'ok'; err e |> 'echec'; };
                `{f}` -> console;
                return 0;
            }
            !train |> principal;
            """);

        Assert.Equal("3 timeout essai essai essai echec", output);
    }
}
