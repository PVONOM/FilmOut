using System.Text.Json.Serialization;

namespace FilmOut.Core;

/// <summary>
/// A non-destructive grade. The ASC CDL (slope, offset, power) is applied in ACEScct log space,
/// followed by saturation and contrast about 18% grey.
/// Offsets are stored in stops, so they read photographically: +1.0 is one stop more light in
/// that channel. The mean of the three offsets is the image's brightness.
/// </summary>
public sealed class Grade
{
    public double[] Slope { get; set; } = [1, 1, 1];
    public double[] OffsetStops { get; set; } = [0, 0, 0];
    public double[] Power { get; set; } = [1, 1, 1];
    public double Saturation { get; set; } = 1;
    public double Contrast { get; set; } = 1;

    [JsonIgnore]
    public bool IsIdentity =>
        Slope.All(v => v == 1) && OffsetStops.All(v => v == 0) && Power.All(v => v == 1)
        && Saturation == 1 && Contrast == 1;

    public Grade Clone() => new()
    {
        Slope = (double[])Slope.Clone(),
        OffsetStops = (double[])OffsetStops.Clone(),
        Power = (double[])Power.Clone(),
        Saturation = Saturation,
        Contrast = Contrast,
    };

    public void CopyFrom(Grade other)
    {
        Slope = (double[])other.Slope.Clone();
        OffsetStops = (double[])other.OffsetStops.Clone();
        Power = (double[])other.Power.Clone();
        Saturation = other.Saturation;
        Contrast = other.Contrast;
    }

    public void Reset() => CopyFrom(new Grade());

    /// <summary>Repairs missing or invalid values, e.g. from a hand-edited job file.</summary>
    public void Normalize()
    {
        Slope = Fix(Slope, 1, positive: true);
        OffsetStops = Fix(OffsetStops, 0, positive: false);
        Power = Fix(Power, 1, positive: true);
        if (!double.IsFinite(Saturation) || Saturation < 0) Saturation = 1;
        if (!double.IsFinite(Contrast) || Contrast <= 0) Contrast = 1;
    }

    private static double[] Fix(double[]? values, double fallback, bool positive)
    {
        var result = new[] { fallback, fallback, fallback };
        if (values is null) return result;
        for (int i = 0; i < Math.Min(3, values.Length); i++)
        {
            double v = values[i];
            if (double.IsFinite(v) && (!positive || v > 0)) result[i] = v;
        }
        return result;
    }
}
