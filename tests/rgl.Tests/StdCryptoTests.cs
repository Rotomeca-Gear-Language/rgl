using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Empreintes, UUID, octets aléatoires.</summary>
public class StdCryptoTests
{
    [ZigFact]
    public void Les_empreintes_connues()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.sha256('abc'));
            std.writeLine(std.sha256(''));
            std.writeLine(std.sha1('abc' as bytes));
            std.writeLine(std.md5('abc' as bytes));
            std.writeLine(std.crc32('123456789' as bytes));
            std.writeLine(std.hmacSha256('key' as bytes, 'The quick brown fox jumps over the lazy dog' as bytes));
            std.writeLine(std.constantTimeEquals('a' as bytes, 'a' as bytes));
            std.writeLine(std.constantTimeEquals('a' as bytes, 'b' as bytes));
            std.writeLine(std.randomBytes(16).length);
            const string id: std.uuid4();
            std.writeLine(id.length);
            std.writeLine(id.substring(14, 15));
            std.writeLine(std.uuid4() eq std.uuid4());
            """);

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\n"
            + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n"
            + "a9993e364706816aba3e25717850c26c9cd0d89d\n"
            + "900150983cd24fb0d6963f7d28e17f72\n"
            + "3421780262\n"
            + "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8\n"
            + "true\nfalse\n16\n36\n4\nfalse\n", output);
    }

    [ZigFact]
    public void Sha512_hmac_et_pbkdf2_donnent_les_vecteurs_connus()
    {
        var output = StdHarness.Body("""
            std.writeLine(std.sha512('abc'));
            std.writeLine(std.sha512(''));
            std.writeLine(std.sha512('abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu'));
            std.writeLine(std.hmacSha512('Jefe' as bytes, 'what do ya want for nothing?' as bytes));
            std.writeLine(std.hexEncode(std.pbkdf2Sha256('password' as bytes, 'salt' as bytes, 1, 32)));
            std.writeLine(std.hexEncode(std.pbkdf2Sha256('password' as bytes, 'salt' as bytes, 2, 32)));
            std.writeLine(std.hexEncode(std.pbkdf2Sha256('passwd' as bytes, 'salt' as bytes, 1, 64)));
            std.writeLine(std.hexEncode(std.pbkdf2Sha512('password' as bytes, 'salt' as bytes, 1, 64)));
            """);

        Assert.Equal(
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f\n"
            + "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e\n"
            + "8e959b75dae313da8cf4f72814fc143f8f7779c6eb9f7fa17299aeadb6889018501d289e4900f7e4331b99dec4b5433ac7d329eeb6dd26545e96e55b874be909\n"
            + "164b7a7bfcf819e2e395fbe73b56e0a387bd64222e831fd610270cd7ea2505549758bf75c05a994a6d034f65f8f0e6fdcaeab1a34d4a6b4b636e070a38bce737\n"
            + "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b\n"
            + "ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43\n"
            + "55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc49ca9cccf179b645991664b39d77ef317c71b845b1e30bd509112041d3a19783\n"
            + "867f70cf1ade02cff3752599a3a53dc4af34c7a669815ae5d513554e1c8cf252c02d470a285a0501bad999bfe943c08f050235d7d68b1da55e63f73b60a57fce\n", output);
    }

    [ZigFact]
    public void Un_mot_de_passe_se_range_et_se_verifie()
    {
        var output = StdHarness.Body("""
            const string first: std.hashPassword('secret', 1000);
            const string second: std.hashPassword('secret', 1000);
            std.writeLine(first.startsWith('pbkdf2-sha256$1000$'));
            std.writeLine(first eq second);
            std.writeLine(std.verifyPassword('secret', first));
            std.writeLine(std.verifyPassword('Secret', first));
            std.writeLine(std.verifyPassword('secret', 'n importe quoi'));
            """);

        Assert.Equal("true\nfalse\ntrue\nfalse\nfalse\n", output);
    }
}
