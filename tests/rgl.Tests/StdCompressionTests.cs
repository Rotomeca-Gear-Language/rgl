using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>DEFLATE et gzip.</summary>
public class StdCompressionTests
{
    [ZigFact]
    public void Aller_retour_et_interoperabilite()
    {
        var output = StdHarness.Body("""
            const string text: 'abcabcabcabc hello hello hello hello world ' .repeat(50);
            const bytes original: text as bytes;
            const bytes packed: std.gzipCompress(original);
            std.writeLine(packed.length < original.length);
            const bytes? back: std.gzipDecompress(packed);
            std.writeLine((back ?? original) eq original);
            const bytes raw: std.deflateRaw(original);
            std.writeLine((std.inflateRaw(raw) ?? packed) eq original);
            std.writeLine(std.gzipDecompress('pas du gzip' as bytes) eq null);
            std.writeLine(std.hexEncode(std.gzipCompress('hello' as bytes)));
            const bytes empty: '' as bytes;
            std.writeLine((std.gzipDecompress(std.gzipCompress(empty)) ?? packed).length);
            """);

        Assert.Equal("true\ntrue\ntrue\ntrue\n1f8b0800000000000" + "0ff" + "cb48cdc9c90700" + "86a6103605000000\n0\n", output);
    }

    [ZigFact]
    public void Lit_un_flux_gzip_a_bloc_dynamique_produit_par_zlib()
    {
        var output = StdHarness.Body("""
            const bytes? packed: std.hexDecode('1f8b080000000000020a5d55417223210cfc0a4f18102078cea4e24a526b6f5cb5cec5af5fa16e8ded5cec19109a56774bdc4fb73dcf64bf43d27ebe7eee337d7ddb5a49775bd48ac55ed3fbe97cdb6b7ab3d5d6fc009672f6038d915a3ca48c74de2f6feffbd8d29ffd7add957f751ded38daf0294da7ebbfaff3f7df26e963bf5cf63e3d874a6c6866b622d8a9c83632612996b774f9192fdf2b3ddd3e17580d94abac36b1db86bf0a4bb1ec979f39591781dbe63ad9c543a7209f74ec026977424af610a3d3431a091dcd63261028f2d4f5a9ca322c77c05991c628ebee5e10ceeb7a9cb1330a19d0038fe59ba4a9520cabf30e601e9c2b821515ce11d9f23a2d7915d136c458f9ae45d3046623f4d7fbdcd67b6df1e57005882905594a20b307188af8cba1b155e03b79b843a08c52cd4a34036268a41b0b38259eb1a851087e5da03050cbf84a8193c31e05619542747fa301f26b478c6813af531d6d8e4f0b83d4ff3ab206ed8dc6e887be41727637ac1fa1bfac50b4025931a00b44ed2f3c087ea3d7cc389ee139d29471a01d27268aa925f420c1939e10676db2dd4bfbe57dc7224ff6e493d179f961d54d61ed3b5c043b0851f0bdc63b9d19ac1e3ad979d6b4c583f7d8ccc741d8f3301ee6ce2c094cafe7164bf0208f70c86823d3f14e26faf680808d0dfa873f571f620cf555291aa290b1839b724cb5989d9323a93edadb6146e2c9d9146ada90f39125f179ff837e3670d0e054d9a8478ec95a5973a78727d851babf06cd1d596d3d5ae8394c48b6f06b13f3a9c71861d8c4f4352c6c104f8971670e324b92ea4e3be1c690c5a40178cc785c0a4a65c171cca38a5e1ab47363ff6a341ea045fd330a1cf4e1789a5fe8f8b1b473b7749215b9cb9aba0d581b667aa35fad9bee98d06671ea562190e9060e372e63f6765e14023e00b380e77cf48fd457fd84de6bd106d6d5a767f5e3de3018d973854631384850241e8ce2fdc96e7fb0428550adfa1010de4ee14f77bb40867220a7014de7b8991e5b315348798f62cc9d0c898bcae6269b0b348052e5b4e84427b4a94d13aaebac8cfa1f74bd89e3c6080000');
            const bytes? text: std.gzipDecompress(packed ?? ('' as bytes));
            std.writeLine((text ?? ('' as bytes)).length);
            std.writeLine(std.sha256(text ?? ('' as bytes)));
            """);

        Assert.Equal("2246\n3668db2a946574504d0f6048ef453cc473c14321c3a32e325a5e46894bc14729\n", output);
    }
}
