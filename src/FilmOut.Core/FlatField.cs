using System.Runtime.InteropServices;
using System.Text;

namespace FilmOut.Core;

/// <summary>
/// The flat-field target is a uniform field across the gate with five small black squares:
/// one inset from each corner, plus a "key" next to the top-left one. The key lets the builder
/// work out which corner is which even if the capture is rotated or mirrored.
/// The renderer draws the target from these numbers and the builder looks for them, so they
/// live in one place.
/// </summary>
public static class FlatFieldTarget
{
    /// <summary>Distance from a gate corner to a marker centre, as a fraction of the gate's shorter side.</summary>
    public const double Inset = 0.05;
    /// <summary>Marker side length, as a fraction of the gate's shorter side.</summary>
    public const double Size = 0.02;
    /// <summary>Key marker offset from the top-left marker along the top edge, as a fraction of the shorter side.</summary>
    public const double KeyOffset = 0.04;

    /// <summary>Marker centres in screen pixels, in the order top-left, top-right, bottom-right, bottom-left, key.</summary>
    public static (double X, double Y)[] CentresPx(GateRect g)
    {
        double m = Math.Min(g.Width, g.Height);
        double i = Inset * m;
        double l = g.X + i, r = g.X + g.Width - i, t = g.Y + i, b = g.Y + g.Height - i;
        return [(l, t), (r, t), (r, b), (l, b), (l + KeyOffset * m, t)];
    }

    public static double HalfSizePx(GateRect g) => Size * Math.Min(g.Width, g.Height) / 2;

    /// <summary>Marker centres in gate-normalised coordinates (0–1 across the gate).</summary>
    public static (double U, double V)[] CentresNormalized(GateRect g) =>
        CentresPx(g).Select(p => ((p.X - g.X) / g.Width, (p.Y - g.Y) / g.Height)).ToArray();
}

/// <summary>
/// Per-channel gain across the gate, applied to linear light just before output.
/// Gains are mostly at or below 1: brighter regions are pulled down to match the dimmer ones,
/// because a screen can't be made brighter than full white.
/// </summary>
public sealed class FlatFieldMap
{
    private const string Magic = "FOFF";
    private const int FormatVersion = 1;

    public FlatFieldMap(int width, int height, float[] gains, GateRect gate, DateTime created, string source)
    {
        if (gains.Length != width * height * 3) throw new ArgumentException("Gain array size doesn't match the map size.", nameof(gains));
        Width = width;
        Height = height;
        Gains = gains;
        Gate = gate;
        Created = created;
        Source = source;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>RGB interleaved, row-major, Width × Height × 3.</summary>
    public float[] Gains { get; }
    /// <summary>The gate the map was built for. A map is only valid for the same gate.</summary>
    public GateRect Gate { get; }
    public DateTime Created { get; }
    public string Source { get; }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(FormatVersion);
        w.Write(Width);
        w.Write(Height);
        w.Write(Gate.X); w.Write(Gate.Y); w.Write(Gate.Width); w.Write(Gate.Height);
        w.Write(Created.ToBinary());
        w.Write(Source);
        w.Write(MemoryMarshal.AsBytes(Gains.AsSpan()));
    }

    public static FlatFieldMap Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != Magic) throw new InvalidDataException("Not a FilmOut flat-field map.");
        int version = r.ReadInt32();
        if (version != FormatVersion) throw new InvalidDataException($"Unsupported flat-field map version {version}.");
        int w = r.ReadInt32(), h = r.ReadInt32();
        var gate = new GateRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        var created = DateTime.FromBinary(r.ReadInt64());
        string source = r.ReadString();
        var gains = new float[w * h * 3];
        var bytes = r.ReadBytes(gains.Length * sizeof(float));
        if (bytes.Length != gains.Length * sizeof(float)) throw new InvalidDataException("The flat-field map file is truncated.");
        bytes.AsSpan().CopyTo(MemoryMarshal.AsBytes(gains.AsSpan()));
        return new FlatFieldMap(w, h, gains, gate, created, source);
    }

    public (float Min, float Max, double Mean) Statistics()
    {
        float min = float.MaxValue, max = float.MinValue;
        double sum = 0;
        foreach (float g in Gains)
        {
            min = Math.Min(min, g);
            max = Math.Max(max, g);
            sum += g;
        }
        return (min, max, sum / Gains.Length);
    }
}

public enum CaptureEncoding { Linear, Srgb, Gamma22 }

public enum CaptureMedium
{
    /// <summary>A digital photo of the screen: more light, higher value.</summary>
    DigitalCamera,
    /// <summary>A scan of negative-working film (such as print stock): more light, denser film.</summary>
    NegativeWorkingFilm,
}

/// <summary>A linear-light RGB image, downsampled for analysis.</summary>
public sealed class CaptureImage
{
    public CaptureImage(int width, int height, float[] rgb)
    {
        Width = width;
        Height = height;
        Rgb = rgb;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>RGB interleaved, linear light, 0–1.</summary>
    public float[] Rgb { get; }

    /// <summary>
    /// Builds a linear, box-downsampled image from 16-bit RGBA pixels. Pixels are linearised
    /// before averaging, so the downsampled values are true light averages.
    /// </summary>
    public static CaptureImage FromRgba64(ReadOnlySpan<byte> rgba64, int width, int height, int targetWidth, CaptureEncoding encoding)
    {
        var src = MemoryMarshal.Cast<byte, ushort>(rgba64);
        int f = Math.Max(1, width / Math.Max(1, targetWidth));
        int w = width / f, h = height / f;
        var lut = LinearizationTable(encoding);
        var rgb = new float[w * h * 3];
        float norm = 1f / (f * f);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int dy = 0; dy < f; dy++)
                {
                    int row = (y * f + dy) * width;
                    for (int dx = 0; dx < f; dx++)
                    {
                        int i = (row + x * f + dx) * 4;
                        r += lut[src[i]];
                        g += lut[src[i + 1]];
                        b += lut[src[i + 2]];
                    }
                }
                int o = (y * w + x) * 3;
                rgb[o] = r * norm;
                rgb[o + 1] = g * norm;
                rgb[o + 2] = b * norm;
            }
        }
        return new CaptureImage(w, h, rgb);
    }

    private static float[] LinearizationTable(CaptureEncoding encoding)
    {
        var lut = new float[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            double v = i / 65535.0;
            lut[i] = (float)(encoding switch
            {
                CaptureEncoding.Srgb => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4),
                CaptureEncoding.Gamma22 => Math.Pow(v, 2.2),
                _ => v,
            });
        }
        return lut;
    }
}
