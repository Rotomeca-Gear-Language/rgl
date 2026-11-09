using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Rgl.Build.Icons;

/// <summary>
/// Une image en mémoire, en pixels RGBA de 8 bits, lue depuis un PNG ou écrite en PNG. Sert aux
/// icônes des programmes : la lecture couvre les PNG courants (gris, RGB, palette, avec ou sans
/// transparence, 8 ou 16 bits), sans entrelacement.
/// </summary>
/// <param name="Width">Largeur, en pixels.</param>
/// <param name="Height">Hauteur, en pixels.</param>
/// <param name="Rgba">Les pixels, ligne par ligne, 4 octets chacun (rouge, vert, bleu, opacité).</param>
internal sealed record PngImage(int Width, int Height, byte[] Rgba)
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Lit un fichier PNG.</summary>
    /// <param name="data">Le fichier.</param>
    /// <param name="error">Pourquoi il n'a pas pu être lu ; <see langword="null"/> en cas de succès.</param>
    /// <returns>L'image, ou <see langword="null"/> si le fichier n'est pas un PNG lisible.</returns>
    public static PngImage? Decode(byte[] data, out string? error)
    {
        error = null;
        if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            error = "ce n'est pas un fichier PNG";
            return null;
        }

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[] palette = [];
        byte[] transparency = [];
        using var compressed = new MemoryStream();

        try
        {
            for (var offset = Signature.Length; offset + 8 <= data.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
                var type = Encoding.ASCII.GetString(data, offset + 4, 4);
                var chunk = data.AsSpan(offset + 8, length);
                offset += 12 + length;

                switch (type)
                {
                    case "IHDR":
                        width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                        height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                        bitDepth = chunk[8];
                        colorType = chunk[9];
                        interlace = chunk[12];
                        break;
                    case "PLTE": palette = chunk.ToArray(); break;
                    case "tRNS": transparency = chunk.ToArray(); break;
                    case "IDAT": compressed.Write(chunk); break;
                    case "IEND": offset = data.Length; break;
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "fichier PNG tronqué";
            return null;
        }

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        var supported = channels > 0 && interlace == 0 && width > 0 && height > 0
            && (colorType == 3 ? bitDepth is 1 or 2 or 4 or 8 : bitDepth is 8 or 16);
        if (!supported)
        {
            error = interlace != 0 ? "PNG entrelacé, non pris en charge" : $"format de PNG non pris en charge (couleur {colorType}, {bitDepth} bits)";
            return null;
        }

        byte[] raw;
        try
        {
            compressed.Position = 0;
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            raw = output.ToArray();
        }
        catch (InvalidDataException)
        {
            error = "données PNG corrompues";
            return null;
        }

        var bitsPerPixel = channels * bitDepth;
        var stride = (width * bitsPerPixel + 7) / 8;
        if (raw.Length < (stride + 1) * height)
        {
            error = "données PNG incomplètes";
            return null;
        }

        var pixels = _Unfilter(raw, height, stride, Math.Max(1, bitsPerPixel / 8));
        return new PngImage(width, height, _ToRgba(pixels, width, height, stride, colorType, bitDepth, palette, transparency));
    }

    /// <summary>Retire les filtres PNG de chaque ligne (aucun, gauche, haut, moyenne, Paeth).</summary>
    private static byte[] _Unfilter(byte[] raw, int height, int stride, int bytesPerPixel)
    {
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = y * (stride + 1) + 1;
            var line = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int left = x >= bytesPerPixel ? pixels[line + x - bytesPerPixel] : 0;
                int up = y > 0 ? pixels[line - stride + x] : 0;
                int upLeft = x >= bytesPerPixel && y > 0 ? pixels[line - stride + x - bytesPerPixel] : 0;
                int predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => _Paeth(left, up, upLeft),
                    _ => 0,
                };
                pixels[line + x] = (byte)(raw[source + x] + predictor);
            }
        }
        return pixels;
    }

    private static int _Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Convertit les pixels décodés en RGBA de 8 bits.</summary>
    private static byte[] _ToRgba(byte[] pixels, int width, int height, int stride, int colorType, int bitDepth, byte[] palette, byte[] transparency)
    {
        var rgba = new byte[width * height * 4];
        var bytesPerSample = bitDepth / 8;

        // Échantillon n° index d'une ligne, ramené sur 8 bits (on garde l'octet de poids fort en 16 bits).
        int Sample(int line, int index) => bytesPerSample == 2 ? pixels[line + index * 2] : pixels[line + index];

        for (var y = 0; y < height; y++)
        {
            var line = y * stride;
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0:
                        r = g = b = (byte)Sample(line, x);
                        break;
                    case 4:
                        r = g = b = (byte)Sample(line, x * 2);
                        a = (byte)Sample(line, x * 2 + 1);
                        break;
                    case 2:
                        (r, g, b) = ((byte)Sample(line, x * 3), (byte)Sample(line, x * 3 + 1), (byte)Sample(line, x * 3 + 2));
                        break;
                    case 6:
                        (r, g, b, a) = ((byte)Sample(line, x * 4), (byte)Sample(line, x * 4 + 1), (byte)Sample(line, x * 4 + 2), (byte)Sample(line, x * 4 + 3));
                        break;
                    default:
                        // Palette : index sur bitDepth bits, du bit de poids fort au plus faible.
                        var bit = x * bitDepth;
                        var index = (pixels[line + bit / 8] >> (8 - bitDepth - bit % 8)) & ((1 << bitDepth) - 1);
                        (r, g, b) = index * 3 + 2 < palette.Length ? (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]) : ((byte)0, (byte)0, (byte)0);
                        a = index < transparency.Length ? transparency[index] : (byte)255;
                        break;
                }
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (r, g, b, a);
            }
        }
        return rgba;
    }

    /// <summary>
    /// L'image dans un carré de <paramref name="size"/> pixels : réduite (ou agrandie) en gardant
    /// ses proportions, centrée sur un fond transparent. Chaque pixel est la moyenne des pixels
    /// sources qu'il couvre, pondérée par leur opacité, pour une réduction nette et sans halo.
    /// </summary>
    public PngImage FitSquare(int size)
    {
        var scale = (double)size / Math.Max(Width, Height);
        var targetWidth = Math.Max(1, (int)Math.Round(Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(Height * scale));
        var left = (size - targetWidth) / 2;
        var top = (size - targetHeight) / 2;
        var result = new byte[size * size * 4];

        for (var ty = 0; ty < targetHeight; ty++)
        {
            double y0 = ty / scale, y1 = Math.Min(Height, (ty + 1) / scale);
            for (var tx = 0; tx < targetWidth; tx++)
            {
                double x0 = tx / scale, x1 = Math.Min(Width, (tx + 1) / scale);
                double r = 0, g = 0, b = 0, a = 0, area = 0;

                for (var sy = (int)y0; sy < y1; sy++)
                {
                    var wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (var sx = (int)x0; sx < x1; sx++)
                    {
                        var weight = wy * (Math.Min(sx + 1, x1) - Math.Max(sx, x0));
                        var o = (sy * Width + sx) * 4;
                        var alpha = Rgba[o + 3] / 255.0 * weight;
                        r += Rgba[o] * alpha;
                        g += Rgba[o + 1] * alpha;
                        b += Rgba[o + 2] * alpha;
                        a += alpha;
                        area += weight;
                    }
                }

                var t = ((top + ty) * size + left + tx) * 4;
                if (a > 0)
                {
                    result[t] = (byte)Math.Round(r / a);
                    result[t + 1] = (byte)Math.Round(g / a);
                    result[t + 2] = (byte)Math.Round(b / a);
                }
                result[t + 3] = area > 0 ? (byte)Math.Round(255 * a / area) : (byte)0;
            }
        }

        return new PngImage(size, size, result);
    }

    /// <summary>Écrit l'image en PNG : RGBA de 8 bits, sans filtre ni entrelacement.</summary>
    public byte[] Encode()
    {
        var stride = 1 + Width * 4;
        var raw = new byte[stride * Height];
        for (var y = 0; y < Height; y++)
            Array.Copy(Rgba, y * Width * 4, raw, y * stride + 1, Width * 4);

        using var png = new MemoryStream();
        png.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8;  // bits par canal
        header[9] = 6;  // RGBA
        _WriteChunk(png, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var deflater = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                deflater.Write(raw);
            _WriteChunk(png, "IDAT", compressed.ToArray());
        }

        _WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Écrit un bloc PNG : longueur, type sur 4 lettres, données, puis CRC-32.</summary>
    private static void _WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        BinaryPrimitives.WriteUInt32BigEndian(buffer, _Crc32(typeBytes, data));
        stream.Write(buffer);
    }

    /// <summary>CRC-32 de la norme PNG (polynôme 0xEDB88320), calculé sur le type puis les données.</summary>
    private static uint _Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var part in new[] { type, data })
        {
            foreach (var b in part)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}
