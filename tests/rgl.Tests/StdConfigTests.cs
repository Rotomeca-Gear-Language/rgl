using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>INI, TOML et YAML en dictionnaire à clés pointées.</summary>
public class StdConfigTests
{
    private static string Show(string call) => $$"""
        var r: {{call}};
        match (r) {
            ok d |> { d.entries().length -> console; "\n" -> console; d.keys().join(",") -> console; "\n" -> console; d.values().join(",") -> console; "\n" -> console; }
            err e |> { e.message -> console; "\n" -> console; }
        }
        """;

    [ZigFact]
    public void L_ini()
    {
        var output = StdHarness.Body(Show("""std.parseIni("top=1\n[a]\nx = 2 ; ok\ny: \"t\"\n")"""));
        Assert.Equal("3\na.x,a.y,top\n2 ; ok,t,1\n", output);
    }

    [ZigFact]
    public void Le_toml()
    {
        var output = StdHarness.Body(Show("""std.parseToml("name = \"rgl\"\nn = 1_000\nok = true\ntags = [\"a\", \"b\"]\n[srv]\nport = 80 # c\ninline = { x = 1, y = \"z\" }\n[[items]]\nid = 1\n[[items]]\nid = 2\n")"""));
        Assert.Equal("10\nitems[0].id,items[1].id,n,name,ok,srv.inline.x,srv.inline.y,srv.port,tags[0],tags[1]\n1,2,1000,rgl,true,1,z,80,a,b\n", output);
    }

    [ZigFact]
    public void Le_yaml()
    {
        var output = StdHarness.Body(Show("""std.parseYaml("name: rgl\nsrv:\n  port: 80\n  hosts:\n    - a\n    - b\nlist:\n  - id: 1\n    ok: true\n  - id: 2\nflow: [1, 2]\n")"""));
        Assert.Equal("9\nflow[0],flow[1],list[0].id,list[0].ok,list[1].id,name,srv.hosts[0],srv.hosts[1],srv.port\n1,2,1,true,2,rgl,a,b,80\n", output);
    }

    [ZigFact]
    public void Les_erreurs_et_l_ecriture()
    {
        var output = StdHarness.Body("""
            match (std.parseToml("a = \n")) {
                ok d |> { "ok\n" -> console; }
                err e |> { e.message -> console; "\n" -> console; }
            }
            match (std.parseIni("pas de signe")) {
                ok d |> { "ok\n" -> console; }
                err e |> { e.message -> console; "\n" -> console; }
            }
            std.toIni(['top': '1', 'a.x': '2', 'a.y': '3']) -> console;
            """);
        Assert.Equal("TOML invalide, ligne 1 : valeur attendue\nINI invalide, ligne 1 : « = » attendu entre la clé et la valeur\ntop=1\n\n[a]\nx=2\ny=3\n", output);
    }
}
