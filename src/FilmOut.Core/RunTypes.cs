using System.Globalization;
using System.Text;

namespace FilmOut.Core;

/// <summary>What the output shows.</summary>
public enum OutputContent { Black, Image, Solid, FramingChart, FlatFieldTarget, DefectHunt }

/// <summary>
/// Everything needed to draw one frame. Instances are immutable snapshots handed to the render
/// thread, so grades and settings are copies.
/// </summary>
public sealed class FrameParameters
{
    public OutputContent Content { get; init; } = OutputContent.Black;
    public string? ImagePath { get; init; }
    public SourceEncoding Encoding { get; init; } = SourceEncoding.Srgb;
    public int RotateDegrees { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public FitMode Fit { get; init; } = FitMode.Fit;
    public double Scale { get; init; } = 1;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public Grade FolderGrade { get; init; } = new();
    public Grade ImageGrade { get; init; } = new();
    public bool Negative { get; init; }
    public NegativeSettings NegativeSettings { get; init; } = new();
    /// <summary>Display code values (0–1) for solid fields, the flat-field target and defect hunting.</summary>
    public double[] FieldCode { get; init; } = [0, 0, 0];
    public int CursorX { get; init; }
    public int CursorY { get; init; }
    /// <summary>Apply the station's flat-field and defect corrections to this frame.</summary>
    public bool ApplyCorrections { get; init; } = true;
}

/// <summary>Station-level corrections and display characteristics, as the renderer needs them.</summary>
public sealed record OutputCalibration(
    GateRect? Gate,
    double EotfGamma,
    int PanelBitDepth,
    FlatFieldMap? FlatField,
    IReadOnlyList<PixelDefect> Defects);

public sealed record RunItem(int Number, string FileName, FrameParameters Parameters, double Seconds);

public enum SerialCommandKind { Next, Goto, Black, Status, Ping, Unknown }

public readonly record struct SerialCommand(SerialCommandKind Kind, int Argument, string Raw);

/// <summary>
/// A line-based text protocol. Commands from the controller: NEXT, GOTO n, BLACK, STATUS?, PING.
/// Replies from FilmOut: ARMED n, ON n, OFF n seconds frames [UNVERIFIED], DONE, STATUS …, PONG, ERR text.
/// Image numbers are positions in the job (1-based).
/// </summary>
public static class SerialProtocol
{
    public static SerialCommand Parse(string line)
    {
        string raw = line.Trim();
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return new(SerialCommandKind.Unknown, 0, raw);

        string verb = parts[0].ToUpperInvariant();
        return verb switch
        {
            "NEXT" or "N" or "FIRE" => new(SerialCommandKind.Next, 0, raw),
            "GOTO" when parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                => new(SerialCommandKind.Goto, n, raw),
            "BLACK" or "ABORT" => new(SerialCommandKind.Black, 0, raw),
            "STATUS?" or "STATUS" => new(SerialCommandKind.Status, 0, raw),
            "PING" => new(SerialCommandKind.Ping, 0, raw),
            _ => new(SerialCommandKind.Unknown, 0, raw),
        };
    }

    public static string Armed(int number) => $"ARMED {number}";
    public static string On(int number) => $"ON {number}";
    public static string Off(int number, double seconds, int frames, bool verified) =>
        string.Create(CultureInfo.InvariantCulture, $"OFF {number} {seconds:0.0000} {frames}{(verified ? "" : " UNVERIFIED")}");
    public static string Done() => "DONE";
    public static string Pong() => "PONG";
    public static string Status(string phase, int number) => $"STATUS {phase} {number}";
    public static string Error(string message) => $"ERR {message.Replace('\n', ' ')}";
}

/// <summary>One CSV row per exposure, written as it happens, in the job folder.</summary>
public sealed class RunLog : IDisposable
{
    private readonly StreamWriter _writer;

    public RunLog(string folder)
    {
        Path = System.IO.Path.Combine(folder, $"filmout-run-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _writer = new StreamWriter(Path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine("time,number,file,requested_s,requested_frames,measured_frames,measured_s,refresh_hz,verified,aborted,trigger");
    }

    public string Path { get; }

    public void Exposure(DateTime time, int number, string file, double requestedSeconds, int requestedFrames,
        int measuredFrames, double measuredSeconds, double refreshHz, bool verified, bool aborted, string trigger)
    {
        var ci = CultureInfo.InvariantCulture;
        _writer.WriteLine(string.Join(",",
            time.ToString("yyyy-MM-dd HH:mm:ss.fff", ci),
            number.ToString(ci),
            Quote(file),
            requestedSeconds.ToString("0.0000", ci),
            requestedFrames.ToString(ci),
            measuredFrames.ToString(ci),
            measuredSeconds.ToString("0.0000", ci),
            refreshHz.ToString("0.0000", ci),
            verified ? "yes" : "no",
            aborted ? "yes" : "no",
            trigger));
    }

    public void Note(string text) => _writer.WriteLine("# " + text.Replace('\n', ' '));

    public void Dispose() => _writer.Dispose();

    private static string Quote(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
