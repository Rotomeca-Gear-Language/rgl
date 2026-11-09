namespace Rgl.Build.Icons;

/// <summary>
/// Les icônes Windows (<c>.ico</c>) des programmes compilés. Un <c>.ico</c> contient une image par
/// taille ; chacune est ici un PNG (Windows Vista et après).
/// </summary>
internal static class WindowsIcon
{
    /// <summary>Tailles d'une icône, de la plus grande à la plus petite : l'explorateur, puis les listes et la barre des tâches.</summary>
    public static readonly int[] Sizes = [256, 48, 32, 16];

    /// <summary>
    /// L'icône par défaut des programmes : <c>assets/rgl-item.ico</c>, intégrée au compilateur,
    /// dont chaque taille est rendue depuis le SVG (voir <c>assets/generate-icons.ps1</c>).
    /// </summary>
    public static byte[] Default()
    {
        using var stream = typeof(WindowsIcon).Assembly.GetManifestResourceStream("Rgl.Assets.rgl-item.ico")
            ?? throw new InvalidOperationException("Ressource Rgl.Assets.rgl-item.ico absente du compilateur.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Une icône faite d'une image : réduite à chaque taille de <see cref="Sizes"/>.</summary>
    public static byte[] FromImage(PngImage image) => Pack([.. Sizes.Select(size => (size, image.FitSquare(size).Encode()))]);

    /// <summary>Assemble des images PNG carrées en un fichier <c>.ico</c> : en-tête, une entrée par image, puis les images.</summary>
    /// <param name="images">Côté de chaque image, en pixels, et son PNG.</param>
    public static byte[] Pack(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        // BinaryWriter écrit en petit-boutiste, l'ordre des octets du format .ico.
        using var ico = new MemoryStream();
        using var writer = new BinaryWriter(ico);

        writer.Write((ushort)0);                 // réservé
        writer.Write((ushort)1);                 // type : icône
        writer.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            var side = (byte)(size >= 256 ? 0 : size);   // 0 signifie 256
            writer.Write(side);
            writer.Write(side);
            writer.Write((byte)0);               // pas de palette
            writer.Write((byte)0);
            writer.Write((ushort)1);             // plans
            writer.Write((ushort)32);            // bits par pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images) writer.Write(png);
        writer.Flush();
        return ico.ToArray();
    }
}
