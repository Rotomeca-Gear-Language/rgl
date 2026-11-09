using System.IO.Compression;
using Rgl.Build.Icons;
using Rgl.Tests.Runtime;

namespace Rgl.Tests;

/// <summary>PNG : lire ce qu'écrit le compilateur et des fichiers filtrés à la main, écrire ce que le compilateur relit.</summary>
public class StdPngTests
{
    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var length = new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
        var crc = Crc(body);
        return [.. length, .. body, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Un PNG fait à la main : chaque ligne reçoit le filtre (numéro de ligne modulo 5), les canaux sont ceux du type de couleur.</summary>
    private static byte[] Handmade(int width, int height, int colorType, int channels, byte[] pixels, byte[]? palette = null, byte[]? transparency = null)
    {
        var stride = width * channels;
        var raw = new List<byte>();
        for (var y = 0; y < height; ++y)
        {
            var filter = y % 5;
            raw.Add((byte)filter);
            for (var x = 0; x < stride; ++x)
            {
                int Value(int row, int column) => row < 0 || column < 0 ? 0 : pixels[row * stride + column];
                var current = Value(y, x);
                var a = Value(y, x - channels);
                var b = Value(y - 1, x);
                var c = Value(y - 1, x - channels);
                var predicted = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
                raw.Add((byte)(current - predicted));
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw.ToArray());
        byte[] Be(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        byte[] header = [.. Be(width), .. Be(height), 8, (byte)colorType, 0, 0, 0];
        List<byte> file = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        file.AddRange(Chunk("IHDR", header));
        if (palette is not null) file.AddRange(Chunk("PLTE", palette));
        if (transparency is not null) file.AddRange(Chunk("tRNS", transparency));
        file.AddRange(Chunk("IDAT", compressed.ToArray()));
        file.AddRange(Chunk("IEND", []));
        return [.. file];
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    private const string Reader = """
        use <Png> only {Image, decodePng, encodePng, newImage, imageFromRgba, pixelAt};
        use <Text> only {hexEncode, hexDecode};

        gear Test(): void {
            const string line: std.readLine();
            const Image? image: decodePng(hexDecode(line) ?? ('' as bytes));
            if (image eq null) { std.writeLine('refusé'); return; }
            const Image found: image ?? newImage(1, 1);
            std.writeLine(`{found.width}x{found.height} {hexEncode(found.pixels)}`);
        }
        emit Fin(): uint => 0;
        !train |> Test |> Fin;
        """;

    [ZigFact]
    public void Un_png_ecrit_par_le_compilateur_se_relit()
    {
        var pixels = new byte[3 * 2 * 4];
        for (var i = 0; i < pixels.Length; ++i) pixels[i] = (byte)(i * 11 + 3);
        var png = new PngImage(3, 2, pixels).Encode();
        var output = StdHarness.Main(Reader, Hex(png) + "\n");
        Assert.Equal($"3x2 {Hex(pixels)}\n", output);
    }

    [ZigFact]
    public void Les_types_de_couleur_et_les_cinq_filtres_sont_lus()
    {
        // 4 colonnes, 6 lignes : chaque ligne a un filtre différent (0 à 4, puis 0).
        var rgba = Enumerable.Range(0, 4 * 6 * 4).Select(i => (byte)(i * 7 + 1)).ToArray();
        var rgb = Enumerable.Range(0, 4 * 6 * 3).Select(i => (byte)(i * 5 + 2)).ToArray();
        var gray = Enumerable.Range(0, 4 * 6).Select(i => (byte)(i * 9 + 4)).ToArray();
        var grayAlpha = Enumerable.Range(0, 4 * 6 * 2).Select(i => (byte)(i * 3 + 6)).ToArray();
        var indexes = Enumerable.Range(0, 4 * 6).Select(i => (byte)(i % 3)).ToArray();
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        byte[] transparency = [128, 64];

        string Run(byte[] png) => StdHarness.Main(Reader, Hex(png) + "\n").TrimEnd('\n');

        Assert.Equal($"4x6 {Hex(rgba)}", Run(Handmade(4, 6, 6, 4, rgba)));

        var fromRgb = new List<byte>();
        for (var p = 0; p < 24; ++p) fromRgb.AddRange([rgb[p * 3], rgb[p * 3 + 1], rgb[p * 3 + 2], 255]);
        Assert.Equal($"4x6 {Hex([.. fromRgb])}", Run(Handmade(4, 6, 2, 3, rgb)));

        var fromGray = new List<byte>();
        foreach (var v in gray) fromGray.AddRange([v, v, v, 255]);
        Assert.Equal($"4x6 {Hex([.. fromGray])}", Run(Handmade(4, 6, 0, 1, gray)));

        var fromGrayAlpha = new List<byte>();
        for (var p = 0; p < 24; ++p) fromGrayAlpha.AddRange([grayAlpha[p * 2], grayAlpha[p * 2], grayAlpha[p * 2], grayAlpha[p * 2 + 1]]);
        Assert.Equal($"4x6 {Hex([.. fromGrayAlpha])}", Run(Handmade(4, 6, 4, 2, grayAlpha)));

        var fromPalette = new List<byte>();
        foreach (var index in indexes)
        {
            fromPalette.AddRange([palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]]);
            fromPalette.Add(index < transparency.Length ? transparency[index] : (byte)255);
        }
        Assert.Equal($"4x6 {Hex([.. fromPalette])}", Run(Handmade(4, 6, 3, 1, indexes, palette, transparency)));
    }

    [ZigFact]
    public void Les_fichiers_invalides_sont_refuses_et_un_png_ecrit_se_relit_par_le_compilateur()
    {
        var png = new PngImage(2, 2, new byte[16]).Encode();
        var damaged = (byte[])png.Clone();
        damaged[40] ^= 0xFF;
        Assert.Equal("refusé", StdHarness.Main(Reader, Hex(damaged) + "\n").TrimEnd('\n'));
        Assert.Equal("refusé", StdHarness.Main(Reader, Hex(png[..30]) + "\n").TrimEnd('\n'));
        Assert.Equal("refusé", StdHarness.Main(Reader, "00ff\n").TrimEnd('\n'));

        var output = StdHarness.Main("""
            use <Png> only {Image, encodePng, newImage, imageFromRgba, pixelAt};
            use <Text> only {hexEncode, hexDecode};

            gear Test(): void {
                const Image? image: imageFromRgba(3, 2, hexDecode('ff00008000ff00ff0000ffff112233445566778899aabbcc') ?? ('' as bytes));
                const Image found: image ?? newImage(1, 1);
                std.writeLine(hexEncode(encodePng(found)));
                std.writeLine(hexEncode(pixelAt(found, 1, 1) ?? ('' as bytes)));
                std.writeLine(pixelAt(found, 3, 0) eq null);
                std.writeLine(imageFromRgba(2, 2, hexDecode('00') ?? ('' as bytes)) eq null);
                std.writeLine(newImage(2, 1).pixels.length);
            }
            emit Fin(): uint => 0;
            !train |> Test |> Fin;
            """);
        var lines = output.Split('\n');
        var decoded = PngImage.Decode(Convert.FromHexString(lines[0]), out var error);
        Assert.True(decoded is not null, error);
        Assert.Equal((3, 2), (decoded!.Width, decoded.Height));
        Assert.Equal("ff00008000ff00ff0000ffff112233445566778899aabbcc", Hex(decoded.Rgba));
        Assert.Equal("55667788", lines[1]);
        Assert.Equal("true", lines[2]);
        Assert.Equal("true", lines[3]);
        Assert.Equal("8", lines[4]);
    }
}
