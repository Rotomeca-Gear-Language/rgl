using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Authentification HTTP (Basic, Bearer, JWT) et formulaires (urlencoded, multipart).</summary>
public class StdHttpAuthFormTests
{
    [ZigFact]
    public void Basic_bearer_et_jwt()
    {
        var output = StdHarness.Main("""
            use <Auth> only {BasicCredentials, parseBasicAuthorization, parseBearerAuthorization, basicAuthorization, bearerAuthorization, signJwt, verifyJwt};

            gear Test(): void {
                const string basic: basicAuthorization('alice', 'p:ss');
                std.writeLine(basic);
                const BasicCredentials? read: parseBasicAuthorization(basic);
                std.writeLine((read ?? {user: '?', password: '?'}).user);
                std.writeLine((read ?? {user: '?', password: '?'}).password);
                std.writeLine(parseBasicAuthorization('Bearer abc') eq null);
                std.writeLine(parseBasicAuthorization('Basic !!!') eq null);
                std.writeLine(parseBasicAuthorization('Basic bm9jb2xvbg==') eq null);
                std.writeLine(parseBearerAuthorization(bearerAuthorization('tok.en')) ?? '?');
                std.writeLine(parseBearerAuthorization('Basic abc') eq null);

                const string payload: '{"sub":"1234567890","name":"John Doe","iat":1516239022}';
                const string token: signJwt(payload, 'your-256-bit-secret');
                std.writeLine(token);
                std.writeLine(verifyJwt(token, 'your-256-bit-secret') ?? 'refusé');
                std.writeLine(verifyJwt(token, 'autre secret') ?? 'refusé');
                std.writeLine(verifyJwt(token + 'x', 'your-256-bit-secret') ?? 'refusé');
                std.writeLine(verifyJwt('pas.un.jeton', 'your-256-bit-secret') ?? 'refusé');
                std.writeLine(verifyJwt('deux.parties', 'your-256-bit-secret') ?? 'refusé');
                const string expired: signJwt('{"exp":1000}', 's');
                std.writeLine(verifyJwt(expired, 's') ?? 'refusé');
                const string alive: signJwt('{"exp":99999999999}', 's');
                std.writeLine(verifyJwt(alive, 's') ?? 'refusé');
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "Basic YWxpY2U6cDpzcw==\nalice\np:ss\ntrue\ntrue\ntrue\ntok.en\ntrue\n"
            + "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c\n"
            + "{\"sub\":\"1234567890\",\"name\":\"John Doe\",\"iat\":1516239022}\nrefusé\nrefusé\nrefusé\nrefusé\nrefusé\n{\"exp\":99999999999}\n", output);
    }

    [ZigFact]
    public void Les_formulaires_urlencoded_et_multipart()
    {
        var output = StdHarness.Main("""
            use <HttpForm> only {FormPart, parseForm, parseMultipart};

            gear Test(): void {
                const [string:string] fields: parseForm('nom=Ada+Lovelace&age=36&mot=caf%C3%A9');
                std.writeLine(fields['nom']);
                std.writeLine(fields['age']);
                std.writeLine(fields['mot']);

                const string body: "--XYZ\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nHello\r\n--XYZ\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\nContent-Type: text/plain\r\n\r\nline1\r\nline2\r\n--XYZ--\r\n";
                const [FormPart]? parts: parseMultipart('multipart/form-data; boundary=XYZ', body);
                const [FormPart] none: [];
                const [FormPart] list: parts ?? none;
                std.writeLine(list.length);
                list ||> (p) => {
                    std.writeLine(`{p.name}|{p.fileName ?? '-'}|{p.contentType ?? '-'}|{p.content.length}`);
                };
                std.writeLine(parseMultipart('multipart/form-data', body) eq null);
                std.writeLine(parseMultipart('multipart/form-data; boundary="XYZ"', body) eq null);
                std.writeLine(parseMultipart('multipart/form-data; boundary=ABC', body) eq null);
                std.writeLine(parseMultipart('multipart/form-data; boundary=XYZ', "--XYZ\r\nbroken") eq null);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal("Ada Lovelace\n36\ncafé\n2\ntitle|-|-|5\nfile|a.txt|text/plain|12\ntrue\nfalse\ntrue\ntrue\n", output);
    }
}
