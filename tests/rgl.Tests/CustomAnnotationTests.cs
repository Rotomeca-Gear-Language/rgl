using Rgl.Binding;
using Rgl.Diagnostics;
using Rgl.Project;
using Rgl.Tests.Runtime;
using Rgl.Text;

namespace Rgl.Tests;

/// <summary><c>@before</c> et les annotations déclarées par le programme (<c>annotation nom(…) = …;</c>).</summary>
public class CustomAnnotationTests
{
    /// <summary>Les erreurs d'un programme d'un seul fichier.</summary>
    private static string[] Errors(string content)
    {
        using var directory = new TempDirectory();
        var path = directory.File("main.rgl", content);
        var diagnostics = new DiagnosticBag();
        var modules = SourceGraph.Load(new SourceText(path, content), directory.Path, diagnostics);
        if (modules is not null) new Binder(diagnostics).Bind(modules);
        return [.. diagnostics.Items.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Code + " " + d.Message)];
    }

    private const string Request = "{method: 'GET', target: '/', path: '/', query: '', headers: '', body: '', queryParameters: [:], routeParameters: [:], cookies: [:], headerFields: ";

    [ZigFact]
    public void Une_annotation_declaree_controle_le_role_avant_la_route()
    {
        var output = StdHarness.Main($$"""
            use <Http> only {HttpRequest, HttpResponse};
            use <HttpAuth> only {denyUnless};

            enum Role { Admin, Editor, Reader }

            gear roleOf(HttpRequest r): Role? {
                const string name: r.headerFields.getOr('x-role', '');
                if (name eq 'admin') { return Role.Admin; }
                if (name eq 'editor') { return Role.Editor; }
                if (name eq 'reader') { return Role.Reader; }
                return null;
            }

            annotation requireRole(params [Role] allowed) = @before(denyUnless(roleOf, allowed));

            @requireRole(Role.Admin, Role.Editor)
            gear secret(HttpRequest r): HttpResponse => HttpResponse.text('secret');

            @requireRole(Role.Reader)
            gear library(HttpRequest r): HttpResponse => HttpResponse.text('books');

            gear call(string role, bool useSecret): void {
                const [string:string] none: [:];
                const [string:string] headers: role eq '' ? none : ['x-role': role];
                const HttpRequest request: {{Request}}headers};
                const HttpResponse response: useSecret ? secret(request) : library(request);
                std.writeLine(`{response.status} {response.body}`);
            }

            gear Test(): void {
                call('admin', true);
                call('editor', true);
                call('reader', true);
                call('', true);
                call('reader', false);
                call('admin', false);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("200 secret\n200 secret\n403 Forbidden\n401 Unauthorized\n200 books\n403 Forbidden\n", output);
    }

    [ZigFact]
    public void Un_before_ecrit_directement_remplace_la_route_par_sa_reponse()
    {
        var output = StdHarness.Main($$"""
            use <Http> only {HttpRequest, HttpResponse};

            gear blocked(HttpRequest r): HttpResponse? => r.path eq '/closed' ? HttpResponse.text('closed', 503) : null;

            @before(blocked)
            gear page(HttpRequest r): HttpResponse => HttpResponse.text(`page {r.path}`);

            gear Test(): void {
                const [string:string] none: [:];
                const HttpRequest open: {{Request}}none};
                const HttpRequest closed: {method: 'GET', target: '/closed', path: '/closed', query: '', headers: '', body: '', queryParameters: [:], routeParameters: [:], cookies: [:], headerFields: none};
                std.writeLine(page(open).body);
                std.writeLine(page(closed).status);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("page /\n503\n", output);
    }

    [Theory]
    [InlineData("@inconnue\ngear f(): void {}\ntrain |> done;", "E125")]                                       // ni du langage ni déclarée
    [InlineData("annotation tap(int n) = @before(c);\ntrain |> done;", "E125")]                                 // nom d'une annotation du langage
    [InlineData("annotation a(int n) = @before(c);\nannotation a(int n) = @before(c);\ntrain |> done;", "E125")] // en double
    [InlineData("gear c(int n): string? => null;\nannotation a(int n) = @before(c);\n@a\ngear f(int n): string => 'x';\ntrain |> done;", "E125")]   // arguments manquants
    [InlineData("gear c(int n): string? => null;\n@before(c)\nmold M { int x; }\ntrain |> done;", "E125")]    // pas sur un mold
    [InlineData("gear c(int n): string => 'x';\n@before(c)\ngear f(int n): string => 'y';\ntrain |> done;", "E125")] // le contrôle doit rendre T?
    [InlineData("gear c(int a, int b): string? => null;\n@before(c)\ngear f(int n): string => 'y';\ntrain |> done;", "E125")] // mauvaise forme
    [InlineData("gear c(int n): int? => null;\n@before(c)\ngear f(int n): string => 'y';\ntrain |> done;", "E003")] // la réponse n'entre pas dans le retour
    public void Les_erreurs_d_annotation_sont_signalees(string source, string code)
    {
        var errors = Errors(source);
        Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }
}
