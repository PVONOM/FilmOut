using System.ComponentModel;
using System.Globalization;
using FilmOut.Core;

namespace FilmOut.App;

/// <summary>One line in the image list.</summary>
public sealed class ImageRow(JobImage image) : INotifyPropertyChanged
{
    public JobImage Image { get; } = image;
    public int Position => Image.Position;
    public string FileName => Image.FileName;
    public string TimeText { get; private set; } = "";
    public string FramesText { get; private set; } = "";
    public string Flags { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(Job job, double refreshHz)
    {
        double t = job.TimeFor(Image);
        TimeText = t.ToString("0.000", CultureInfo.InvariantCulture);
        FramesText = ExposureMath.ToFrames(t, refreshHz).ToString(CultureInfo.InvariantCulture);

        var flags = new List<string>();
        if (Image.Settings.TimeSeconds is null) flags.Add("default time");
        if (!Image.Settings.Grade.IsIdentity) flags.Add("graded");
        if (Image.Settings.IgnoreFolderGrade) flags.Add("no folder grade");
        if (Math.Abs(ExposureMath.QuantizationErrorPoints(t, refreshHz)) > JobStatistics.CoarseThresholdPoints) flags.Add("coarse timing");
        Flags = string.Join(", ", flags);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
