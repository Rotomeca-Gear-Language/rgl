using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>AES-GCM, écrit à la main dans la std : les vecteurs d'essai officiels, puis la vérification de la balise.</summary>
public class StdAesTests
{
    [ZigFact]
    public void Les_vecteurs_officiels_d_AES_GCM_sont_retrouves()
    {
        var output = StdHarness.Main("""
            use <Crypto> only {aesGcmSeal};
            use <Text> only {hexEncode, hexDecode};

            gear bin(string hex): bytes => hexDecode(hex) ?? ('' as bytes);
            gear seal(string key, string iv, string plain, string aad): string => hexEncode(aesGcmSeal(bin(key), bin(iv), bin(plain), bin(aad)) ?? ('' as bytes));

            gear Test(): void {
                const string zeros16: '00000000000000000000000000000000';
                const string zeros24: '000000000000000000000000000000000000000000000000';
                const string zeros32: '0000000000000000000000000000000000000000000000000000000000000000';
                const string iv0: '000000000000000000000000';

                std.writeLine(seal(zeros16, iv0, '', ''));
                std.writeLine(seal(zeros16, iv0, zeros16, ''));
                std.writeLine(seal(zeros24, iv0, '', ''));
                std.writeLine(seal(zeros24, iv0, zeros16, ''));
                std.writeLine(seal(zeros32, iv0, '', ''));
                std.writeLine(seal(zeros32, iv0, zeros16, ''));

                const string key: 'feffe9928665731c6d6a8f9467308308';
                const string iv: 'cafebabefacedbaddecaf888';
                const string plain: 'd9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39';
                const string aad: 'feedfacedeadbeeffeedfacedeadbeefabaddad2';
                std.writeLine(seal(key, iv, plain, aad));
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "58e2fccefa7e3061367f1d57a4e7455a\n"
            + "0388dace60b6a392f328c2b971b2fe78ab6e47d42cec13bdf53a67b21257bddf\n"
            + "cd33b28ac773f74ba00ed1f312572435\n"
            + "98e7247c07f0fe411c267e4384b0f6002ff58d80033927ab8ef4d4587514f0fb\n"
            + "530f8afbc74536b9a963b4f1c4cb738b\n"
            + "cea7403d4d606b6e074ec5d3baf39d18d0d1c8a799996bf0265b98b5d48ab919\n"
            + "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e0915bc94fbc3221a5db94fae95ae7121a47\n",
            output);
    }

    [ZigFact]
    public void Le_dechiffrement_verifie_la_balise_et_aesEncrypt_fait_l_aller_retour()
    {
        var output = StdHarness.Main("""
            use <Crypto> only {aesGcmSeal, aesGcmOpen, aesEncrypt, aesDecrypt, randomBytes};
            use <Text> only {hexEncode, hexDecode};

            gear bin(string hex): bytes => hexDecode(hex) ?? ('' as bytes);
            gear text(bytes? data): string {
                if (data eq null) { return 'refusé'; }
                const string? decoded: (data ?? ('' as bytes)) as? string;
                return decoded ?? 'invalide';
            }

            gear Test(): void {
                const bytes key: bin('feffe9928665731c6d6a8f9467308308');
                const bytes iv: bin('cafebabefacedbaddecaf888');
                const bytes aad: bin('feedfacedeadbeeffeedfacedeadbeefabaddad2');
                const bytes secret: 'rendez-vous à minuit' as bytes;

                const bytes sealed: aesGcmSeal(key, iv, secret, aad) ?? ('' as bytes);
                std.writeLine(sealed.length);
                std.writeLine(text(aesGcmOpen(key, iv, sealed, aad)));
                std.writeLine(aesGcmOpen(key, iv, sealed, bin('00')) eq null);
                std.writeLine(aesGcmOpen(bin('00feffe9928665731c6d6a8f94673083'), iv, sealed, aad) eq null);
                std.writeLine(aesGcmOpen(key, bin('cafebabefacedbaddecaf889'), sealed, aad) eq null);
                std.writeLine(aesGcmOpen(key, iv, sealed.slice(0, sealed.length - 1), aad) eq null);
                std.writeLine(aesGcmSeal(bin('0102'), iv, secret, aad) eq null);
                std.writeLine(aesGcmSeal(key, bin('0102'), secret, aad) eq null);

                const bytes strong: randomBytes(32);
                const bytes first: aesEncrypt(strong, secret) ?? ('' as bytes);
                const bytes second: aesEncrypt(strong, secret) ?? ('' as bytes);
                std.writeLine(first.length);
                std.writeLine(hexEncode(first) eq hexEncode(second));
                std.writeLine(text(aesDecrypt(strong, first)));
                std.writeLine(text(aesDecrypt(strong, second)));
                std.writeLine(aesDecrypt(randomBytes(32), first) eq null);
                std.writeLine(aesDecrypt(strong, first.slice(0, 20)) eq null);
                const bytes empty: aesEncrypt(strong, '' as bytes) ?? ('' as bytes);
                std.writeLine(`{empty.length} {text(aesDecrypt(strong, empty)).length}`);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        // « rendez-vous à minuit » : 20 caractères, 21 octets (le « à » en prend 2) ; plus la balise de 16 octets, et 12 d'un nonce.
        Assert.Equal("37\nrendez-vous à minuit\ntrue\ntrue\ntrue\ntrue\ntrue\ntrue\n49\nfalse\nrendez-vous à minuit\nrendez-vous à minuit\ntrue\ntrue\n28 0\n", output);
    }
}
