namespace FilmOut.Core;

public sealed class FlatFieldBuildOptions
{
    public CaptureEncoding Encoding { get; set; } = CaptureEncoding.Linear;
    public CaptureMedium Medium { get; set; } = CaptureMedium.DigitalCamera;
    /// <summary>Contrast of the film, used to turn density back into exposure for film captures.</summary>
    public double FilmGamma { get; set; } = 3.0;
    /// <summary>Width of the gain map in texels; the height follows the gate's aspect ratio.</summary>
    public int MapWidth { get; set; } = 480;
    /// <summary>The map is normalised so this percentile of the field gets a gain of 1.</summary>
    public double ReferencePercentile { get; set; } = 2.0;
    /// <summary>Largest gain allowed, for spots darker than the reference (dust shadows, dead areas).</summary>
    public double MaxBoost { get; set; } = 1.5;
    /// <summary>Gaussian smoothing of the map, in texels, to remove grain and sensor noise.</summary>
    public double SmoothingTexels { get; set; } = 1.0;
    /// <summary>Skip marker detection and assume the capture covers exactly the gate.</summary>
    public bool CaptureIsCroppedToGate { get; set; }
}

public sealed record FlatFieldBuildResult(FlatFieldMap Map, double MinGain, double MaxGain, double MeanLossStops, double BoostedFraction, string Summary);

/// <summary>
/// Builds a flat-field gain map from a capture of the flat-field target. The capture can be
/// a digital photo taken through the same lens, or a scan of film exposed with the target.
/// </summary>
public static class FlatFieldBuilder
{
    public static FlatFieldBuildResult Build(CaptureImage capture, GateRect gate, FlatFieldBuildOptions o, string sourceName)
    {
        int mw = Math.Clamp(o.MapWidth, 16, 4096);
        int mh = Math.Max(8, (int)Math.Round(mw * (double)gate.Height / gate.Width));

        double[] h = o.CaptureIsCroppedToGate ? CroppedHomography(capture) : DetectHomography(capture, gate, o.Medium);

        var markers = FlatFieldTarget.CentresNormalized(gate);
        double minSide = Math.Min(gate.Width, gate.Height);
        double exclude = FlatFieldTarget.Size * 1.5; // marker side plus margin, in shorter-side units

        var e = new float[mw * mh * 3];
        var valid = new bool[mw * mh];
        int validCount = 0;

        for (int j = 0; j < mh; j++)
        {
            for (int i = 0; i < mw; i++)
            {
                double u = (i + 0.5) / mw, v = (j + 0.5) / mh;
                if (!o.CaptureIsCroppedToGate && NearMarker(u, v, markers, exclude, gate, minSide)) continue;

                Project(h, u, v, out double x, out double y);
                if (!Sample(capture, x, y, out float r, out float g, out float b)) continue;

                double er = ToExposure(r, o), eg = ToExposure(g, o), eb = ToExposure(b, o);
                if (!(er > 0 && eg > 0 && eb > 0) || !double.IsFinite(er + eg + eb)) continue;

                int k = (j * mw + i) * 3;
                e[k] = (float)er; e[k + 1] = (float)eg; e[k + 2] = (float)eb;
                valid[j * mw + i] = true;
                validCount++;
            }
        }

        if (validCount < mw * mh / 2)
            throw new InvalidOperationException(
                "Less than half of the gate could be read from the capture. Make sure the whole gate is in the picture, " +
                "or, if the capture is cropped exactly to the gate, choose that option.");

        Inpaint(e, valid, mw, mh);
        Blur(e, mw, mh, o.SmoothingTexels);

        var gains = new float[e.Length];
        for (int c = 0; c < 3; c++)
        {
            var channel = new float[mw * mh];
            for (int t = 0; t < channel.Length; t++) channel[t] = e[t * 3 + c];
            double reference = Percentile(channel, o.ReferencePercentile);
            for (int t = 0; t < channel.Length; t++)
                gains[t * 3 + c] = (float)Math.Clamp(reference / channel[t], 0.05, o.MaxBoost);
        }

        var map = new FlatFieldMap(mw, mh, gains, gate, DateTime.Now, sourceName);
        var (min, max, mean) = map.Statistics();
        double boosted = gains.Count(g => g > 1.0001f) / (double)gains.Length;
        double lossStops = -Math.Log2(mean);

        string summary =
            $"Built a {mw} × {mh} map from {sourceName}.\n\n" +
            $"Gains run from {min:0.000} to {max:0.000}. Brighter areas are dimmed to match the dimmer ones, " +
            $"which costs about {lossStops:0.00} stops of light on average. Lengthen display times by that much, " +
            "or redo your exposure tests with the correction on.\n\n" +
            (boosted > 0.0005
                ? $"{boosted:P1} of the gate is darker than the reference (dust shadows or corners). Those areas are boosted, " +
                  "which only fully works where the image isn't already near full brightness."
                : "No part of the gate needed boosting.");

        return new FlatFieldBuildResult(map, min, max, lossStops, boosted, summary);
    }

    private static double ToExposure(double linear, FlatFieldBuildOptions o) =>
        o.Medium == CaptureMedium.DigitalCamera
            ? linear
            // Negative-working film: density = gamma·log10(E) + c, and a linear scan reads transmission
            // T = 10^-density, so relative exposure is T^(-1/gamma).
            : Math.Pow(Math.Max(linear, 1e-5), -1.0 / Math.Max(0.1, o.FilmGamma));

    private static bool NearMarker(double u, double v, (double U, double V)[] markers, double exclude, GateRect gate, double minSide)
    {
        foreach (var m in markers)
        {
            double du = Math.Abs(u - m.U) * gate.Width / minSide;
            double dv = Math.Abs(v - m.V) * gate.Height / minSide;
            if (du < exclude && dv < exclude) return true;
        }
        return false;
    }

    // ---- Geometry -----------------------------------------------------------------------

    /// <summary>Homography h0..h7 mapping gate-normalised (u, v) to capture pixel coordinates.</summary>
    private static void Project(double[] h, double u, double v, out double x, out double y)
    {
        double w = h[6] * u + h[7] * v + 1;
        x = (h[0] * u + h[1] * v + h[2]) / w;
        y = (h[3] * u + h[4] * v + h[5]) / w;
    }

    private static double[] CroppedHomography(CaptureImage c) => [c.Width, 0, 0, 0, c.Height, 0, 0, 0];

    private static double[] DetectHomography(CaptureImage capture, GateRect gate, CaptureMedium medium)
    {
        var blobs = FindMarkerBlobs(capture, medium);
        if (blobs.Count < 5)
            throw new InvalidOperationException(
                $"Found {blobs.Count} of the 5 target markers. Make sure the whole gate is in the capture and the markers aren't clipped. " +
                "On film, the field must stay well above clear base everywhere, including the corners; if the corners are nearly clear, " +
                "expose the target longer. If the capture is cropped exactly to the gate, choose that option instead.");

        var (tl, tr, br, bl) = IdentifyCorners(blobs.Take(5).ToList());
        var t = FlatFieldTarget.CentresNormalized(gate);
        return SolveHomography([t[0], t[1], t[2], t[3]], [tl, tr, br, bl]);
    }

    private readonly record struct Blob(double X, double Y, int Area);

    /// <summary>
    /// Markers and the area outside the gate are both unexposed: dark in a digital capture, clear
    /// (bright) on negative-working film. In log units with that polarity folded in, markers are
    /// small holes in the field. A morphological closing fills holes smaller than its window, so
    /// pixels well below the closed image are marker (or dust) pixels. Large unexposed regions and
    /// smooth light falloff survive the closing unchanged and are not flagged.
    /// </summary>
    private static List<Blob> FindMarkerBlobs(CaptureImage c, CaptureMedium medium)
    {
        int w = c.Width, h = c.Height;
        var s = new float[w * h];
        for (int i = 0; i < s.Length; i++)
        {
            float lum = Math.Max((c.Rgb[i * 3] + c.Rgb[i * 3 + 1] + c.Rgb[i * 3 + 2]) / 3f, 1e-5f);
            float l = MathF.Log10(lum);
            s[i] = medium == CaptureMedium.NegativeWorkingFilm ? -l : l;
        }

        int window = Math.Max(5, (int)Math.Round(Math.Min(w, h) * 0.04) | 1);
        var closed = Closing(s, w, h, window / 2);
        var mask = new bool[s.Length];
        for (int i = 0; i < s.Length; i++) mask[i] = closed[i] - s[i] > 0.15f;

        var seen = new bool[s.Length];
        var blobs = new List<Blob>();
        var stack = new Stack<int>();
        int maxArea = (int)(w * h * 0.01);

        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            long sx = 0, sy = 0;
            int area = 0, minX = w, maxX = 0, minY = h, maxY = 0;
            bool touchesEdge = false;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                area++; sx += x; sy += y;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) touchesEdge = true;
                if (x > 0) Visit(p - 1);
                if (x < w - 1) Visit(p + 1);
                if (y > 0) Visit(p - w);
                if (y < h - 1) Visit(p + w);
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            double aspect = bw / (double)bh, fill = area / (double)(bw * bh);
            if (!touchesEdge && area >= 4 && area <= maxArea && aspect is > 0.5 and < 2.0 && fill > 0.5)
                blobs.Add(new Blob(sx / (double)area + 0.5, sy / (double)area + 0.5, area));
        }

        return blobs.OrderByDescending(b => b.Area).ToList();

        void Visit(int q)
        {
            if (mask[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
        }
    }

    private static float[] Closing(float[] s, int w, int h, int radius)
    {
        var a = Extremum(s, w, h, radius, horizontal: true, max: true);
        a = Extremum(a, w, h, radius, horizontal: false, max: true);
        a = Extremum(a, w, h, radius, horizontal: true, max: false);
        return Extremum(a, w, h, radius, horizontal: false, max: false);
    }

    private static float[] Extremum(float[] src, int w, int h, int radius, bool horizontal, bool max)
    {
        var dst = new float[src.Length];
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        for (int line = 0; line < lines; line++)
        {
            for (int k = 0; k < length; k++)
            {
                float best = max ? float.MinValue : float.MaxValue;
                int lo = Math.Max(0, k - radius), hi = Math.Min(length - 1, k + radius);
                for (int q = lo; q <= hi; q++)
                {
                    float v = src[horizontal ? line * w + q : q * w + line];
                    best = max ? Math.Max(best, v) : Math.Min(best, v);
                }
                dst[horizontal ? line * w + k : k * w + line] = best;
            }
        }
        return dst;
    }

    /// <summary>
    /// The key marker is much closer to the top-left marker than any other pair, which fixes
    /// the top-left corner and the direction of the top edge, whatever the capture's rotation or mirroring.
    /// </summary>
    private static ((double, double) TL, (double, double) TR, (double, double) BR, (double, double) BL) IdentifyCorners(List<Blob> five)
    {
        int a = 0, b = 1;
        double best = double.MaxValue;
        for (int i = 0; i < 5; i++)
            for (int j = i + 1; j < 5; j++)
            {
                double d = Dist(five[i], five[j]);
                if (d < best) { best = d; a = i; b = j; }
            }

        double cx = five.Average(p => p.X), cy = five.Average(p => p.Y);
        var center = new Blob(cx, cy, 0);
        int tlIndex = Dist(five[a], center) > Dist(five[b], center) ? a : b;
        int keyIndex = tlIndex == a ? b : a;
        var tl = five[tlIndex];
        var key = five[keyIndex];
        var rest = Enumerable.Range(0, 5).Where(i => i != a && i != b).ToList();

        double kx = key.X - tl.X, ky = key.Y - tl.Y, kl = Math.Sqrt(kx * kx + ky * ky);
        int trIndex = rest.MaxBy(i =>
        {
            double dx = five[i].X - tl.X, dy = five[i].Y - tl.Y, l = Math.Sqrt(dx * dx + dy * dy);
            return (dx * kx + dy * ky) / (l * kl);
        });
        var others = rest.Where(i => i != trIndex).ToList();
        int blIndex = Dist(five[others[0]], tl) < Dist(five[others[1]], tl) ? others[0] : others[1];
        int brIndex = blIndex == others[0] ? others[1] : others[0];
        var tr = five[trIndex];
        var bl = five[blIndex];
        var br = five[brIndex];

        return ((tl.X, tl.Y), (tr.X, tr.Y), (br.X, br.Y), (bl.X, bl.Y));

        static double Dist(Blob p, Blob q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    }

    private static double[] SolveHomography((double U, double V)[] from, (double X, double Y)[] to)
    {
        var m = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            var (u, v) = from[i];
            var (x, y) = to[i];
            int r = i * 2;
            m[r, 0] = u; m[r, 1] = v; m[r, 2] = 1; m[r, 6] = -u * x; m[r, 7] = -v * x; m[r, 8] = x;
            m[r + 1, 3] = u; m[r + 1, 4] = v; m[r + 1, 5] = 1; m[r + 1, 6] = -u * y; m[r + 1, 7] = -v * y; m[r + 1, 8] = y;
        }

        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12)
                throw new InvalidOperationException("The target markers are in a degenerate arrangement; the capture can't be mapped to the gate.");
            if (pivot != col)
                for (int k = 0; k < 9; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            for (int r = 0; r < 8; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (int k = col; k < 9; k++) m[r, k] -= f * m[col, k];
            }
        }

        var hResult = new double[8];
        for (int i = 0; i < 8; i++) hResult[i] = m[i, 8] / m[i, i];
        return hResult;
    }

    // ---- Image helpers --------------------------------------------------------------------

    /// <summary>Bilinear sample at continuous coordinates where pixel i spans [i, i+1).</summary>
    private static bool Sample(CaptureImage c, double x, double y, out float r, out float g, out float b)
    {
        r = g = b = 0;
        double fx = x - 0.5, fy = y - 0.5;
        if (fx < 0 || fy < 0 || fx > c.Width - 1 || fy > c.Height - 1) return false;
        int x0 = Math.Min((int)fx, c.Width - 2), y0 = Math.Min((int)fy, c.Height - 2);
        if (x0 < 0 || y0 < 0) return false;
        float ax = (float)(fx - x0), ay = (float)(fy - y0);
        var d = c.Rgb;
        int i00 = (y0 * c.Width + x0) * 3, i10 = i00 + 3, i01 = i00 + c.Width * 3, i11 = i01 + 3;
        r = Lerp(Lerp(d[i00], d[i10], ax), Lerp(d[i01], d[i11], ax), ay);
        g = Lerp(Lerp(d[i00 + 1], d[i10 + 1], ax), Lerp(d[i01 + 1], d[i11 + 1], ax), ay);
        b = Lerp(Lerp(d[i00 + 2], d[i10 + 2], ax), Lerp(d[i01 + 2], d[i11 + 2], ax), ay);
        return true;

        static float Lerp(float p, float q, float t) => p + (q - p) * t;
    }

    /// <summary>Fills invalid texels (under markers, outside the capture) from their valid neighbours.</summary>
    private static void Inpaint(float[] e, bool[] valid, int w, int h)
    {
        var next = (bool[])valid.Clone();
        for (int pass = 0; pass < w + h; pass++)
        {
            bool changed = false, anyMissing = false;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (valid[p]) continue;
                    anyMissing = true;
                    float r = 0, g = 0, b = 0;
                    int n = 0;
                    Add(x - 1, y); Add(x + 1, y); Add(x, y - 1); Add(x, y + 1);
                    if (n == 0) continue;
                    e[p * 3] = r / n; e[p * 3 + 1] = g / n; e[p * 3 + 2] = b / n;
                    next[p] = true;
                    changed = true;

                    void Add(int qx, int qy)
                    {
                        if (qx < 0 || qy < 0 || qx >= w || qy >= h) return;
                        int q = qy * w + qx;
                        if (!valid[q]) return;
                        r += e[q * 3]; g += e[q * 3 + 1]; b += e[q * 3 + 2];
                        n++;
                    }
                }
            }
            Array.Copy(next, valid, valid.Length);
            if (!anyMissing || !changed) break;
        }
    }

    private static void Blur(float[] e, int w, int h, double sigma)
    {
        if (sigma < 0.3) return;
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
            sum += kernel[i + radius];
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var tmp = new float[e.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float acc = 0;
                    for (int k = -radius; k <= radius; k++)
                        acc += kernel[k + radius] * e[(y * w + Math.Clamp(x + k, 0, w - 1)) * 3 + c];
                    tmp[(y * w + x) * 3 + c] = acc;
                }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float acc = 0;
                    for (int k = -radius; k <= radius; k++)
                        acc += kernel[k + radius] * tmp[(Math.Clamp(y + k, 0, h - 1) * w + x) * 3 + c];
                    e[(y * w + x) * 3 + c] = acc;
                }
    }

    private static double Percentile(float[] values, double percentile)
    {
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Clamp(Math.Round(percentile / 100.0 * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }
}
