using System.Globalization;

namespace FilmOut.Core;

/// <summary>Orders "shot_2" before "shot_10".</summary>
public sealed class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = a.CompareTo(b, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}

/// <summary>
/// Display time is the exposure. A monitor can only change its picture on a refresh, so every
/// time is rounded to a whole number of refreshes.
/// </summary>
public static class ExposureMath
{
    /// <summary>One printer point is 0.025 log10 exposure (about 1/12 stop).</summary>
    public const double LogEPerPrinterPoint = 0.025;

    public static int ToFrames(double seconds, double refreshHz) =>
        Math.Max(1, (int)Math.Round(seconds * refreshHz, MidpointRounding.AwayFromZero));

    public static double FramesToSeconds(int frames, double refreshHz) => frames / refreshHz;

    public static double QuantizedSeconds(double seconds, double refreshHz) =>
        FramesToSeconds(ToFrames(seconds, refreshHz), refreshHz);

    public static double PointsBetween(double fromSeconds, double toSeconds) =>
        Math.Log10(toSeconds / fromSeconds) / LogEPerPrinterPoint;

    public static double StopsBetween(double fromSeconds, double toSeconds) => Math.Log2(toSeconds / fromSeconds);

    public static double AddPoints(double seconds, double points) =>
        seconds * Math.Pow(10, points * LogEPerPrinterPoint);

    public static double AddStops(double seconds, double stops) => seconds * Math.Pow(2, stops);

    /// <summary>How far the achievable time is from the requested one, in printer points.</summary>
    public static double QuantizationErrorPoints(double seconds, double refreshHz) =>
        PointsBetween(seconds, QuantizedSeconds(seconds, refreshHz));

    public static string FormatSeconds(double seconds) =>
        seconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";

    public static string FormatDuration(double seconds)
    {
        if (seconds < 60) return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : $"{t.Minutes} min {t.Seconds} s";
    }

    public static bool TryParseSeconds(string text, out double seconds)
    {
        text = text.Trim().TrimEnd('s', 'S').Trim();
        if ((double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
             || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds))
            && double.IsFinite(seconds) && seconds > 0)
            return true;
        seconds = 0;
        return false;
    }
}

public sealed record JobStatistics(
    int ImageCount,
    double MinSeconds,
    JobImage? MinImage,
    int MinFrames,
    double MaxSeconds,
    JobImage? MaxImage,
    int MaxFrames,
    double RangeStops,
    double RangePoints,
    double TotalSeconds,
    long TotalFrames,
    int CoarselyQuantizedCount)
{
    /// <summary>Quantization error, in printer points, above which an image is flagged.</summary>
    public const double CoarseThresholdPoints = 0.25;

    public static JobStatistics Compute(Job job, double refreshHz)
    {
        if (job.Images.Count == 0)
            return new JobStatistics(0, 0, null, 0, 0, null, 0, 0, 0, 0, 0, 0);

        JobImage? minImage = null, maxImage = null;
        double min = double.MaxValue, max = double.MinValue;
        long totalFrames = 0;
        int coarse = 0;

        foreach (var image in job.Images)
        {
            double t = ExposureMath.QuantizedSeconds(job.TimeFor(image), refreshHz);
            if (t < min) { min = t; minImage = image; }
            if (t > max) { max = t; maxImage = image; }
            totalFrames += ExposureMath.ToFrames(job.TimeFor(image), refreshHz);
            if (Math.Abs(ExposureMath.QuantizationErrorPoints(job.TimeFor(image), refreshHz)) > CoarseThresholdPoints)
                coarse++;
        }

        return new JobStatistics(
            job.Images.Count,
            min, minImage, ExposureMath.ToFrames(min, refreshHz),
            max, maxImage, ExposureMath.ToFrames(max, refreshHz),
            ExposureMath.StopsBetween(min, max),
            ExposureMath.PointsBetween(min, max),
            totalFrames / refreshHz,
            totalFrames,
            coarse);
    }
}
