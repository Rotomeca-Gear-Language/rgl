using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>Ed25519 : les vecteurs d'essai de la spécification, puis le rejet de ce qui est modifié.</summary>
public class StdEd25519Tests
{
    [ZigFact]
    public void Les_vecteurs_de_la_specification_sont_retrouves_et_une_modification_est_refusee()
    {
        var output = StdHarness.Main("""
            use <Ed25519> only {ed25519Seed, ed25519PublicKey, ed25519Sign, ed25519Verify};
            use <Text> only {hexEncode, hexDecode};

            gear bin(string hex): bytes => hexDecode(hex) ?? ('' as bytes);
            gear hex(bytes? data): string => hexEncode(data ?? ('' as bytes));

            gear Test(): void {
                const bytes seed1: bin('9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60');
                std.writeLine(hex(ed25519PublicKey(seed1)));
                const bytes? sig1: ed25519Sign(seed1, '' as bytes);
                std.writeLine(hex(sig1));
                std.writeLine(ed25519Verify(bin('d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a'), '' as bytes, sig1 ?? ('' as bytes)));

                const bytes seed2: bin('4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb');
                const bytes message2: bin('72');
                std.writeLine(hex(ed25519PublicKey(seed2)));
                const bytes? sig2: ed25519Sign(seed2, message2);
                std.writeLine(hex(sig2));
                const bytes publicKey2: ed25519PublicKey(seed2) ?? ('' as bytes);
                std.writeLine(ed25519Verify(publicKey2, message2, sig2 ?? ('' as bytes)));
                std.writeLine(ed25519Verify(publicKey2, bin('73'), sig2 ?? ('' as bytes)));
                std.writeLine(ed25519Verify(bin('d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a'), message2, sig2 ?? ('' as bytes)));
                const bytes broken: [0 as ubyte, ...(sig2 ?? ('' as bytes)).slice(1, 64)];
                std.writeLine(ed25519Verify(publicKey2, message2, broken));
                std.writeLine(ed25519Verify(publicKey2, message2, bin('00')));
                std.writeLine(ed25519Verify(bin('00'), message2, sig2 ?? ('' as bytes)));

                const bytes fresh: ed25519Seed();
                const bytes freshKey: ed25519PublicKey(fresh) ?? ('' as bytes);
                const bytes text: 'un message plus long que 64 octets pour passer plusieurs blocs de hachage, sans problème' as bytes;
                const bytes freshSig: ed25519Sign(fresh, text) ?? ('' as bytes);
                std.writeLine(`{fresh.length} {freshKey.length} {freshSig.length} {ed25519Verify(freshKey, text, freshSig)} {ed25519Sign(bin('01'), text) eq null} {ed25519PublicKey(bin('01')) eq null}`);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);

        Assert.Equal(
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a\n"
            + "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b\n"
            + "true\n"
            + "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c\n"
            + "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00\n"
            + "true\nfalse\nfalse\nfalse\nfalse\nfalse\n"
            + "32 32 64 true true true\n", output);
    }
}
