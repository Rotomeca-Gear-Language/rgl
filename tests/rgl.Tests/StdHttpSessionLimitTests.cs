using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Sessions par cookie signé et limitation de débit des requêtes HTTP.</summary>
public class StdHttpSessionLimitTests
{
    private const string Request = "{method: 'GET', target: '/', path: '/', query: '', headers: '', body: '', queryParameters: [:], routeParameters: [:], headerFields: [:], cookies: ";

    [ZigFact]
    public void La_session_vit_dans_un_cookie_signe()
    {
        var output = StdHarness.Main($$"""
            use <Http> only {HttpRequest, HttpResponse};

            gear Test(): void {
                const [string:string] data: ['user': 'alice', 'role': 'admin'];
                const [string:string] empty: [:];
                const HttpResponse response: HttpResponse.text('ok').withSession('s3cret', data);
                const [string] none: [];
                const string setCookie: (response.cookies ?? none)[0];
                std.writeLine(setCookie.contains('Max-Age=3600'));
                std.writeLine(setCookie.contains('HttpOnly'));
                const string pair: setCookie.splitOnce(';').before;
                const string token: pair.splitOnce('=').after;

                const HttpRequest valid: {{Request}}['rgl_session': token]};
                const HttpRequest tampered: {{Request}}['rgl_session': token + 'x']};
                const HttpRequest anonymous: {{Request}}empty};
                std.writeLine((valid.session('s3cret') ?? empty).getOr('user', 'aucune'));
                std.writeLine((valid.session('s3cret') ?? empty).getOr('role', 'aucun'));
                std.writeLine((valid.session('autre') ?? empty).getOr('user', 'aucune'));
                std.writeLine((tampered.session('s3cret') ?? empty).getOr('user', 'aucune'));
                std.writeLine((anonymous.session('s3cret') ?? empty).getOr('user', 'aucune'));

                const HttpResponse expired: HttpResponse.text('ok').withSession('s3cret', data, 0);
                const string expiredCookie: (expired.cookies ?? none)[0];
                const string expiredToken: expiredCookie.splitOnce(';').before.splitOnce('=').after;
                const HttpRequest old: {{Request}}['rgl_session': expiredToken]};
                std.writeLine((old.session('s3cret') ?? empty).getOr('user', 'expirée'));

                const string ended: ((HttpResponse.text('bye').withoutSession().cookies) ?? none)[0];
                std.writeLine(ended.contains('Max-Age=0'));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("true\ntrue\nalice\nadmin\naucune\naucune\naucune\nexpirée\ntrue\n", output);
    }

    [ZigFact]
    public void La_limitation_de_debit_repond_429_au_dela_de_la_limite()
    {
        var output = StdHarness.Main($$"""
            use <Http> only {HttpRequest, HttpResponse};
            use <HttpLimit> only {rateLimitBy, rateLimit};

            gear Test(): void {
                const [string:string] empty: [:];
                const HttpRequest request: {{Request}}empty};
                const HttpRequest options: {method: 'OPTIONS', target: '/', path: '/', query: '', headers: '', body: '', queryParameters: [:], routeParameters: [:], headerFields: [:], cookies: empty};
                const (HttpRequest) => HttpResponse handler: (HttpRequest r) => HttpResponse.text('ok');
                const (HttpRequest, (HttpRequest) => HttpResponse) => HttpResponse limited: rateLimitBy(2, 10s, (HttpRequest r) => r.path);
                std.writeLine(limited(request, handler).status);
                std.writeLine(limited(request, handler).status);
                const HttpResponse refused: limited(request, handler);
                std.writeLine(refused.status);
                std.writeLine((refused.headers ?? empty).getOr('Retry-After', '?'));
                std.writeLine(limited(options, handler).status);

                const (HttpRequest, (HttpRequest) => HttpResponse) => HttpResponse byUser: rateLimitBy(1, 10s, (HttpRequest r) => r.headerFields.getOr('x-user', 'anonyme'));
                const HttpRequest alice: {method: 'GET', target: '/', path: '/', query: '', headers: '', body: '', queryParameters: [:], routeParameters: [:], headerFields: ['x-user': 'alice'], cookies: empty};
                const HttpRequest bob: {method: 'GET', target: '/', path: '/', query: '', headers: '', body: '', queryParameters: [:], routeParameters: [:], headerFields: ['x-user': 'bob'], cookies: empty};
                std.writeLine(byUser(alice, handler).status);
                std.writeLine(byUser(bob, handler).status);
                std.writeLine(byUser(alice, handler).status);

                const (HttpRequest, (HttpRequest) => HttpResponse) => HttpResponse shared: rateLimit(1, 10s);
                std.writeLine(shared(request, handler).status);
                std.writeLine(shared(request, handler).status);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("200\n200\n429\n10\n200\n200\n200\n429\n200\n429\n", output);
    }
}
