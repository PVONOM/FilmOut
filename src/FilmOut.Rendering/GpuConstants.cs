using System.Numerics;
using System.Runtime.InteropServices;
using FilmOut.Core;

namespace FilmOut.Rendering;

/// <summary>Must match the cbuffer in Pipeline.hlsl exactly (22 float4 + 1 uint4 = 368 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuConstants
{
    public Vector4 UvRow0, UvRow1, Gate;
    public Vector4 FolderSlope, FolderOffset, FolderPower, FolderMisc;
    public Vector4 ImageSlope, ImageOffset, ImagePower, ImageMisc;
    public Vector4 NegGamma, NegPoints, Field, Cursor, Output, Reserved;
    public Vector4 Marker0, Marker1, Marker2, Marker3, Marker4;
    public uint Content, Negative, Flags, SourceEncoding;
}

internal static class ConstantsBuilder
{
    private const float StopsToCct = 1f / 17.52f; // one stop in ACEScct units
    private const float DefectRingRadius = 14f;
    private const float DefectRingThickness = 2f;

    public static GpuConstants Build(FrameParameters f, GateRect gate, OutputCalibration cal,
        int sourceWidth, int sourceHeight, uint seed, bool flatAvailable, bool defectsAvailable)
    {
        var c = new GpuConstants
        {
            Gate = new Vector4(gate.X, gate.Y, gate.Width, gate.Height),
            Output = new Vector4((float)cal.EotfGamma, 1f / ((1 << cal.PanelBitDepth) - 1), seed, 0),
            Field = new Vector4(Channel(f.FieldCode, 0), Channel(f.FieldCode, 1), Channel(f.FieldCode, 2), DefectRingRadius),
            Cursor = new Vector4(f.CursorX, f.CursorY, DefectRingThickness, 0),
            Content = (uint)f.Content,
            Negative = f.Content == OutputContent.Image && f.Negative ? 1u : 0u,
            Flags = (f.ApplyCorrections && flatAvailable ? 1u : 0u) | (f.ApplyCorrections && defectsAvailable ? 2u : 0u),
            SourceEncoding = (uint)f.Encoding,
        };

        (c.FolderSlope, c.FolderOffset, c.FolderPower, c.FolderMisc) = Grade(f.FolderGrade);
        (c.ImageSlope, c.ImageOffset, c.ImagePower, c.ImageMisc) = Grade(f.ImageGrade);

        var n = f.NegativeSettings;
        c.NegGamma = new Vector4(Channel(n.PrintGamma, 0, 3), Channel(n.PrintGamma, 1, 3), Channel(n.PrintGamma, 2, 3), (float)n.DensityRange);
        c.NegPoints = new Vector4(Channel(n.PrinterPoints, 0), Channel(n.PrinterPoints, 1), Channel(n.PrinterPoints, 2), 0);

        var markers = FlatFieldTarget.CentresPx(gate);
        float half = (float)FlatFieldTarget.HalfSizePx(gate);
        c.Marker0 = new Vector4((float)markers[0].X, (float)markers[0].Y, half, 0);
        c.Marker1 = new Vector4((float)markers[1].X, (float)markers[1].Y, half, 0);
        c.Marker2 = new Vector4((float)markers[2].X, (float)markers[2].Y, half, 0);
        c.Marker3 = new Vector4((float)markers[3].X, (float)markers[3].Y, half, 0);
        c.Marker4 = new Vector4((float)markers[4].X, (float)markers[4].Y, half, 0);

        if (f.Content == OutputContent.Image && sourceWidth > 0 && sourceHeight > 0)
            (c.UvRow0, c.UvRow1) = SourceTransform(f, gate, sourceWidth, sourceHeight);

        return c;
    }

    private static (Vector4, Vector4, Vector4, Vector4) Grade(Core.Grade g) => (
        new Vector4(Channel(g.Slope, 0, 1), Channel(g.Slope, 1, 1), Channel(g.Slope, 2, 1), 0),
        new Vector4(Channel(g.OffsetStops, 0) * StopsToCct, Channel(g.OffsetStops, 1) * StopsToCct, Channel(g.OffsetStops, 2) * StopsToCct, 0),
        new Vector4(Channel(g.Power, 0, 1), Channel(g.Power, 1, 1), Channel(g.Power, 2, 1), 0),
        new Vector4((float)g.Saturation, 0, (float)g.Contrast, 0));

    private static float Channel(double[]? values, int index, double fallback = 0) =>
        (float)(values is not null && index < values.Length && double.IsFinite(values[index]) ? values[index] : fallback);

    /// <summary>
    /// Maps an output pixel centre to source texture coordinates. The image is rotated, then
    /// flipped, then scaled into the gate and offset. All of that is affine, so it's evaluated at
    /// three points and passed to the shader as a 2 × 3 matrix.
    /// </summary>
    private static (Vector4 Row0, Vector4 Row1) SourceTransform(FrameParameters f, GateRect gate, int srcW, int srcH)
    {
        int rotation = Orientation.Snap(f.RotateDegrees);
        bool swap = rotation is 90 or 270;
        double ow = swap ? srcH : srcW, oh = swap ? srcW : srcH;

        double sx, sy;
        switch (f.Fit)
        {
            case FitMode.Fill: sx = sy = Math.Max(gate.Width / ow, gate.Height / oh); break;
            case FitMode.Stretch: sx = gate.Width / ow; sy = gate.Height / oh; break;
            case FitMode.Native: sx = sy = 1; break;
            default: sx = sy = Math.Min(gate.Width / ow, gate.Height / oh); break;
        }
        double scale = f.Scale > 0 ? f.Scale : 1;
        double dw = ow * sx * scale, dh = oh * sy * scale;
        double cx = gate.X + gate.Width / 2.0 + f.OffsetX * gate.Width;
        double cy = gate.Y + gate.Height / 2.0 + f.OffsetY * gate.Height;
        double left = cx - dw / 2, top = cy - dh / 2;

        (double U, double V) Map(double x, double y)
        {
            double a = (x - left) / dw, b = (y - top) / dh;
            if (f.FlipHorizontal) a = 1 - a;
            if (f.FlipVertical) b = 1 - b;
            return rotation switch
            {
                90 => (b, 1 - a),
                180 => (1 - a, 1 - b),
                270 => (1 - b, a),
                _ => (a, b),
            };
        }

        var o = Map(0, 0);
        var ex = Map(1, 0);
        var ey = Map(0, 1);
        return (
            new Vector4((float)(ex.U - o.U), (float)(ey.U - o.U), (float)o.U, 0),
            new Vector4((float)(ex.V - o.V), (float)(ey.V - o.V), (float)o.V, 0));
    }
}
