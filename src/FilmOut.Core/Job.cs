using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FilmOut.Core;

public enum ImageMode { Positive, Negative }

public enum FitMode { Fit, Fill, Stretch, Native }

public enum SourceEncoding { Srgb, Gamma22, Gamma24, Linear }

public sealed class Orientation
{
    /// <summary>Clockwise rotation: 0, 90, 180 or 270.</summary>
    public int RotateDegrees { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }

    public Orientation Clone() => new()
    {
        RotateDegrees = RotateDegrees, FlipHorizontal = FlipHorizontal, FlipVertical = FlipVertical,
    };

    public static int Snap(int degrees) => (((int)Math.Round(degrees / 90.0) * 90) % 360 + 360) % 360;
}

/// <summary>
/// Parameters for the placeholder negative transform. A fitted calibration profile
/// (display → film → lab → scanner) will replace this model in a later phase.
/// </summary>
public sealed class NegativeSettings
{
    /// <summary>Contrast (gamma) of the print stock per channel. Print stocks are typically 2.5–3.</summary>
    public double[] PrintGamma { get; set; } = [3.0, 3.0, 3.0];

    /// <summary>Density range of the print from white to deepest black (log10).</summary>
    public double DensityRange { get; set; } = 3.0;

    /// <summary>Printer lights per channel, in printer points (1 point = 0.025 log exposure).</summary>
    public double[] PrinterPoints { get; set; } = [0, 0, 0];

    public NegativeSettings Clone() => new()
    {
        PrintGamma = (double[])PrintGamma.Clone(),
        DensityRange = DensityRange,
        PrinterPoints = (double[])PrinterPoints.Clone(),
    };
}

public sealed class ImageSettings
{
    /// <summary>Display (exposure) time. Null means the folder's default time.</summary>
    public double? TimeSeconds { get; set; }
    public bool IgnoreFolderGrade { get; set; }
    public Grade Grade { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class JobSettings
{
    public int Version { get; set; } = 1;
    public ImageMode Mode { get; set; } = ImageMode.Positive;
    public SourceEncoding SourceEncoding { get; set; } = SourceEncoding.Srgb;
    public Orientation Orientation { get; set; } = new();
    public FitMode Fit { get; set; } = FitMode.Fit;
    public double Scale { get; set; } = 1.0;
    /// <summary>Horizontal offset of the image within the gate, as a fraction of gate width.</summary>
    public double OffsetX { get; set; }
    /// <summary>Vertical offset of the image within the gate, as a fraction of gate height.</summary>
    public double OffsetY { get; set; }
    public double DefaultTimeSeconds { get; set; } = 1.0;
    public Grade FolderGrade { get; set; } = new();
    public NegativeSettings Negative { get; set; } = new();
    public Dictionary<string, ImageSettings> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class JobImage
{
    /// <summary>1-based position in display order.</summary>
    public required int Position { get; init; }
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required ImageSettings Settings { get; init; }
    /// <summary>The trailing number in the file name, if any (e.g. 12 for "shot_0012.tif").</summary>
    public long? SequenceNumber { get; init; }
}

/// <summary>
/// A folder of numbered images and the settings that go with them. Settings live in a JSON
/// file inside the folder; source images are never modified.
/// </summary>
public sealed partial class Job
{
    public const string FileName = "filmout.job.json";

    public static readonly string[] ImageExtensions = [".tif", ".tiff", ".png", ".jpg", ".jpeg", ".bmp"];

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private Job(string folder, JobSettings settings, List<JobImage> images, List<string> warnings)
    {
        Folder = folder;
        Settings = settings;
        Images = images;
        Warnings = warnings;
    }

    public string Folder { get; }
    public JobSettings Settings { get; }
    public IReadOnlyList<JobImage> Images { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string FilePath => Path.Combine(Folder, FileName);

    public double TimeFor(JobImage image) => image.Settings.TimeSeconds ?? Settings.DefaultTimeSeconds;

    public static Job Open(string folder)
    {
        folder = Path.GetFullPath(folder);
        var warnings = new List<string>();
        var settings = LoadSettings(Path.Combine(folder, FileName), warnings);

        var files = Directory.EnumerateFiles(folder)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, NaturalSortComparer.Instance)
            .ToList();

        var images = new List<JobImage>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            string name = files[i];
            if (!settings.Images.TryGetValue(name, out var imageSettings))
            {
                imageSettings = new ImageSettings();
                settings.Images[name] = imageSettings;
            }
            imageSettings.Grade ??= new Grade();
            imageSettings.Grade.Normalize();
            images.Add(new JobImage
            {
                Position = i + 1,
                FileName = name,
                FullPath = Path.Combine(folder, name),
                Settings = imageSettings,
                SequenceNumber = ParseSequenceNumber(name),
            });
        }

        CheckNumbering(images, warnings);

        int missing = settings.Images.Keys.Count(k => !files.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (missing > 0)
            warnings.Add($"{missing} image(s) in the job file are no longer in the folder. Their settings are kept in case they come back.");
        if (images.Count == 0)
            warnings.Add("This folder has no TIFF, PNG, JPEG or BMP images.");

        return new Job(folder, settings, images, warnings);
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(Settings, JsonOptions);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    private static JobSettings LoadSettings(string path, List<string> warnings)
    {
        if (!File.Exists(path)) return new JobSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<JobSettings>(File.ReadAllText(path), JsonOptions) ?? new JobSettings();
            // The deserializer creates a case-sensitive dictionary; file names on Windows are not.
            loaded.Images = new Dictionary<string, ImageSettings>(loaded.Images ?? new Dictionary<string, ImageSettings>(), StringComparer.OrdinalIgnoreCase);
            loaded.FolderGrade ??= new Grade();
            loaded.FolderGrade.Normalize();
            loaded.Orientation ??= new Orientation();
            loaded.Orientation.RotateDegrees = Orientation.Snap(loaded.Orientation.RotateDegrees);
            loaded.Negative ??= new NegativeSettings();
            if (!(loaded.DefaultTimeSeconds > 0)) loaded.DefaultTimeSeconds = 1.0;
            if (!(loaded.Scale > 0)) loaded.Scale = 1.0;
            return loaded;
        }
        catch (Exception ex)
        {
            string backup = path + ".unreadable";
            File.Copy(path, backup, overwrite: true);
            warnings.Add($"The job file couldn't be read ({ex.Message}). It was copied to {Path.GetFileName(backup)} and a new one was started.");
            return new JobSettings();
        }
    }

    [GeneratedRegex(@"(\d+)(?!.*\d)")]
    private static partial Regex TrailingNumber();

    private static long? ParseSequenceNumber(string fileName)
    {
        var match = TrailingNumber().Match(Path.GetFileNameWithoutExtension(fileName));
        return match.Success && long.TryParse(match.Value, out long n) ? n : null;
    }

    private static void CheckNumbering(List<JobImage> images, List<string> warnings)
    {
        var numbered = images.Where(i => i.SequenceNumber.HasValue).ToList();
        if (numbered.Count < images.Count && numbered.Count > 0)
            warnings.Add($"{images.Count - numbered.Count} image(s) have no number in their name; they are ordered alphabetically.");

        var duplicates = numbered.GroupBy(i => i.SequenceNumber!.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            warnings.Add($"Duplicate frame numbers: {string.Join(", ", duplicates.Take(10))}{(duplicates.Count > 10 ? "…" : "")}.");

        var sorted = numbered.Select(i => i.SequenceNumber!.Value).Distinct().Order().ToList();
        var gaps = new List<string>();
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i] - sorted[i - 1] > 1)
                gaps.Add(sorted[i] - sorted[i - 1] == 2 ? $"{sorted[i - 1] + 1}" : $"{sorted[i - 1] + 1}–{sorted[i] - 1}");
        if (gaps.Count > 0)
            warnings.Add($"Missing frame numbers: {string.Join(", ", gaps.Take(10))}{(gaps.Count > 10 ? "…" : "")}.");
    }
}
