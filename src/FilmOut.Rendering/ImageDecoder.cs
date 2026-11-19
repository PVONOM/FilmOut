using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilmOut.Rendering;

public sealed class DecodedImage
{
    public required string Path { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>16-bit RGBA, 8 bytes per pixel, rows tightly packed.</summary>
    public required byte[] Rgba64 { get; init; }
}

public static class ImageDecoder
{
    /// <summary>
    /// Decodes any WIC-supported file (TIFF, PNG, JPEG, BMP) to 16-bit RGBA without colour
    /// management, so the pixel values reach the pipeline exactly as stored. 8-bit images are
    /// widened to 16 bits. Safe to call from any thread.
    /// </summary>
    public static DecodedImage Decode(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Rgba64)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Rgba64, null, 0);

        int width = frame.PixelWidth, height = frame.PixelHeight;
        long size = (long)width * height * 8;
        if (size > Array.MaxLength)
            throw new NotSupportedException($"{System.IO.Path.GetFileName(path)} is {width} × {height}, which is larger than FilmOut can load.");

        var data = GC.AllocateUninitializedArray<byte>((int)size);
        frame.CopyPixels(data, width * 8, 0);
        return new DecodedImage { Path = path, Width = width, Height = height, Rgba64 = data };
    }
}
