using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Le pot à cookies du client HTTP.</summary>
public class StdCookieJarTests
{
    [ZigFact]
    public void Le_pot_garde_renvoie_et_supprime_les_cookies_par_machine()
    {
        var output = StdHarness.Main("""
            use <HttpClient> only {HttpClient, HttpClientResponse, CookieJar, newCookieJar, jarHeader, jarStore};

            gear pure response(string headers): HttpClientResponse => {status: 200, headers, body: ''};

            guard failing(): void {
                mut CookieJar jar: newCookieJar();
                const HttpClientResponse r: HttpClient.create().sendWithJar(jar, 'GET', 'http://127.0.0.1:1/');
                std.writeLine(r.status);
            }

            gear Test(): void {
                mut CookieJar jar: newCookieJar();
                std.writeLine(`[{jarHeader(jar, 'http://exemple.org/')}]`);
                jarStore(jar, 'https://Exemple.org:8443/login?x=1', response("HTTP/1.1 200 OK\r\nSet-Cookie: sid=abc123; Path=/; HttpOnly\r\nSet-Cookie: theme=dark\r\nContent-Type: text/plain\r\n"));
                std.writeLine(jarHeader(jar, 'http://exemple.org/page'));
                std.writeLine(`[{jarHeader(jar, 'http://autre.org/')}]`);
                jarStore(jar, 'http://exemple.org/', response("HTTP/1.1 200 OK\r\nset-cookie: theme=clair; Path=/\r\nSet-Cookie: sid=; Max-Age=0\r\n"));
                std.writeLine(jarHeader(jar, 'http://exemple.org/'));
                jarStore(jar, 'http://exemple.org/', response("HTTP/1.1 200 OK\r\nSet-Cookie: vieux=1; Expires=Thu, 01 Jan 1970 00:00:00 GMT\r\nSet-Cookie: =sansnom\r\nSet-Cookie: invalide\r\nX-Autre: a=b\r\n"));
                std.writeLine(jarHeader(jar, 'http://exemple.org/'));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("[]\nsid=abc123; theme=dark\n[]\ntheme=clair\ntheme=clair\n", output);
    }
}
