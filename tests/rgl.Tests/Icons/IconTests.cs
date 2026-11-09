using System.Buffers.Binary;
using Rgl.Build.Icons;

namespace Rgl.Tests.Icons;

/// <summary>Tests des icônes des programmes : lecture et écriture PNG, réduction, fichiers .ico.</summary>
public class IconTests
{
    /// <summary>Une image de test : un dégradé, avec une transparence qui varie.</summary>
    private static PngImage Gradient(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = ((byte)(x * 7), (byte)(y * 5), (byte)(x + y), (byte)(255 - x));
            }
        return new PngImage(width, height, rgba);
    }

    [Fact]
    public void Une_image_ecrite_en_png_se_relit_a_l_identique()
    {
        var image = Gradient(37, 21);
        var read = PngImage.Decode(image.Encode(), out var error);

        Assert.Null(error);
        Assert.NotNull(read);
        Assert.Equal((37, 21), (read.Width, read.Height));
        Assert.Equal(image.Rgba, read.Rgba);
    }

    [Fact]
    public void Les_icones_de_rgl_se_lisent()
    {
        foreach (var file in new[] { "rgl.png", "rgl-item.png" })
        {
            var image = PngImage.Decode(File.ReadAllBytes(Path.Combine(_Assets(), file)), out var error);
            Assert.True(image is not null, $"{file} : {error}");
            Assert.Equal((680, 680), (image.Width, image.Height));
        }
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 }, "ce n'est pas un fichier PNG")]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, "format de PNG non pris en charge")]
    public void Un_fichier_illisible_est_refuse_avec_sa_raison(byte[] data, string reason)
    {
        Assert.Null(PngImage.Decode(data, out var error));
        Assert.StartsWith(reason, error);
    }

    [Fact]
    public void Une_image_rectangulaire_est_centree_dans_un_carre_transparent()
    {
        var square = Gradient(40, 20).FitSquare(16);

        Assert.Equal((16, 16), (square.Width, square.Height));
        Assert.Equal(0, square.Rgba[3]);                          // coin haut : fond transparent
        Assert.NotEqual(0, square.Rgba[(8 * 16 + 8) * 4 + 3]);    // centre : l'image
    }

    [Fact]
    public void Une_reduction_moyenne_les_pixels_couverts()
    {
        // 2 × 2 pixels opaques, deux noirs et deux blancs : un seul pixel gris.
        var image = new PngImage(2, 2, [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255]);
        Assert.Equal([128, 128, 128, 255], image.FitSquare(1).Rgba);
    }

    /// <summary>
    /// Vérifie un fichier .ico : son en-tête, puis chaque entrée, dont la taille annoncée et
    /// l'emplacement doivent désigner un PNG lisible de la bonne dimension, dans le fichier.
    /// </summary>
    private static void AssertValidIcon(byte[] ico)
    {
        Assert.Equal((0, 1, WindowsIcon.Sizes.Length),
            (BinaryPrimitives.ReadUInt16LittleEndian(ico), BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)), BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4))));

        var end = 6 + 16 * WindowsIcon.Sizes.Length;
        for (var i = 0; i < WindowsIcon.Sizes.Length; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i);
            var size = WindowsIcon.Sizes[i];
            Assert.Equal(size >= 256 ? 0 : size, entry[0]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            Assert.Equal(end, offset);                                 // les images se suivent, sans trou
            Assert.True(offset + length <= ico.Length);
            var png = PngImage.Decode(ico.AsSpan(offset, length).ToArray(), out var error);
            Assert.True(png is not null, $"image {size} : {error}");
            Assert.Equal((size, size), (png.Width, png.Height));
            end = offset + length;
        }
        Assert.Equal(ico.Length, end);
    }

    [Fact]
    public void Un_ico_contient_une_image_png_par_taille() => AssertValidIcon(WindowsIcon.FromImage(Gradient(64, 64)));

    [Theory]
    [InlineData("rgl.ico")]          // icône de rgl.exe
    [InlineData("rgl-item.ico")]     // icône par défaut des programmes
    public void Les_icones_generees_depuis_les_svg_sont_valides(string file) =>
        AssertValidIcon(File.ReadAllBytes(Path.Combine(_Assets(), file)));

    [Fact]
    public void L_icone_par_defaut_est_integree_au_compilateur() =>
        Assert.Equal(File.ReadAllBytes(Path.Combine(_Assets(), "rgl-item.ico")), WindowsIcon.Default());

    private static string _Assets([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", "assets"));
}
