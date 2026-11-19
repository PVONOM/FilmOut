using System.Text.Json;

namespace FilmOut.Core;

public enum PanelType { IpsLcd, VaLcd, MiniLedLcd, Oled, TnLcd }

/// <summary>The rectangle of the output screen, in pixels, that the camera frames.</summary>
public sealed record GateRect(int X, int Y, int Width, int Height)
{
    public GateRect ClampTo(int screenWidth, int screenHeight)
    {
        int x = Math.Clamp(X, 0, Math.Max(0, screenWidth - 1));
        int y = Math.Clamp(Y, 0, Math.Max(0, screenHeight - 1));
        int w = Math.Clamp(Width, 1, screenWidth - x);
        int h = Math.Clamp(Height, 1, screenHeight - y);
        return new GateRect(x, y, w, h);
    }
}

public enum DefectKind
{
    /// <summary>Never lights.</summary>
    Dead,
    /// <summary>Always lit at a fixed level.</summary>
    Stuck,
    /// <summary>Lights, but at a fraction of the intended level.</summary>
    Weak,
}

[Flags]
public enum ColorChannels { None = 0, Red = 1, Green = 2, Blue = 4, All = Red | Green | Blue }

public sealed class PixelDefect
{
    public int X { get; set; }
    public int Y { get; set; }
    public DefectKind Kind { get; set; } = DefectKind.Dead;
    public ColorChannels Channels { get; set; } = ColorChannels.All;
    /// <summary>Stuck: the emitted level (0–1, linear light). Weak: the fraction of intended light (0–1).</summary>
    public double Level { get; set; } = 1.0;
    public string? Note { get; set; }

    public PixelDefect Clone() => new() { X = X, Y = Y, Kind = Kind, Channels = Channels, Level = Level, Note = Note };

    public override string ToString()
    {
        string channels = Channels == ColorChannels.All ? "all channels" : Channels.ToString().ToLowerInvariant();
        string kind = Kind switch
        {
            DefectKind.Dead => "dead",
            DefectKind.Stuck => $"stuck at {Level:0.00}",
            _ => $"weak ({Level:P0})",
        };
        return $"({X}, {Y})  {kind}, {channels}";
    }
}

public sealed class SerialSettings
{
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 115200;
    public bool ConnectOnStart { get; set; }
}

/// <summary>
/// Settings that belong to the physical setup (monitor, camera position, light path), not to a job.
/// </summary>
public sealed class StationSettings
{
    public string? OutputDeviceName { get; set; }
    public PanelType Panel { get; set; } = PanelType.IpsLcd;
    /// <summary>The monitor's response: linear light = code ^ gamma.</summary>
    public double EotfGamma { get; set; } = 2.2;
    public int PanelBitDepth { get; set; } = 10;
    public double StaticContrast { get; set; } = 1000;
    /// <summary>Null means the whole screen.</summary>
    public GateRect? Gate { get; set; }
    public bool FlatFieldEnabled { get; set; }
    public string? FlatFieldMapPath { get; set; }
    public bool DefectsEnabled { get; set; } = true;
    public List<PixelDefect> Defects { get; set; } = [];
    public SerialSettings Serial { get; set; } = new();
    /// <summary>WPF key names, e.g. "Space", "Enter", "F13".</summary>
    public List<string> TriggerKeys { get; set; } = ["Space", "Enter"];
    public string AbortKey { get; set; } = "Escape";
    public string? LastJobFolder { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FilmOut");

    public static string DefaultPath => Path.Combine(Folder, "station.json");

    public static StationSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var s = JsonSerializer.Deserialize<StationSettings>(File.ReadAllText(DefaultPath), Job.JsonOptions);
                if (s is not null)
                {
                    s.Defects ??= [];
                    s.Serial ??= new SerialSettings();
                    s.TriggerKeys ??= ["Space"];
                    if (!(s.EotfGamma > 0.5 && s.EotfGamma < 4)) s.EotfGamma = 2.2;
                    if (s.PanelBitDepth is not (8 or 10)) s.PanelBitDepth = 10;
                    return s;
                }
            }
        }
        catch
        {
            // Fall through to defaults; the unreadable file is left in place for inspection.
        }
        return new StationSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string temp = DefaultPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Job.JsonOptions));
        File.Move(temp, DefaultPath, overwrite: true);
    }
}

/// <summary>What the panel type means for shooting film off the screen.</summary>
public static class PanelAdvice
{
    public static (double Gamma, double StaticContrast) Defaults(PanelType type) => type switch
    {
        PanelType.VaLcd => (2.2, 3000),
        PanelType.MiniLedLcd => (2.2, 1000),
        PanelType.Oled => (2.2, 1_000_000),
        PanelType.TnLcd => (2.2, 800),
        _ => (2.2, 1000),
    };

    public static string Describe(PanelType type) => type switch
    {
        PanelType.IpsLcd => "IPS LCD",
        PanelType.VaLcd => "VA LCD",
        PanelType.MiniLedLcd => "Mini-LED LCD",
        PanelType.Oled => "OLED",
        PanelType.TnLcd => "TN LCD",
        _ => type.ToString(),
    };

    public static IReadOnlyList<string> Notes(PanelType type, int screenWidth = 0)
    {
        var notes = new List<string>();
        switch (type)
        {
            case PanelType.IpsLcd:
                notes.Add("Black isn't black: the backlight glows at roughly 1/1000 of white, more toward the corners. Whenever the shutter is open, that glow exposes the film. In negative mode, screen black becomes print white, so glow shows up as grey highlights. Open the shutter only around each exposure (the serial ON/OFF messages are for this).");
                notes.Add("Keep the camera square to the screen and centred. IPS glow and colour shift grow off-axis. Flat-field correction fixes the white level across the gate, not the glow.");
                notes.Add("Use one fixed brightness setting and confirm the backlight doesn't flicker at it (many are flicker-free only near 100%). Control exposure with display time, aperture or ND instead.");
                break;
            case PanelType.VaLcd:
                notes.Add("VA panels have deeper black than IPS, but gamma and colour shift noticeably off-axis. Keep the camera square to the screen and use a long enough lens that the corners are seen nearly head-on.");
                notes.Add("Use one fixed brightness setting and confirm the backlight doesn't flicker at it.");
                break;
            case PanelType.MiniLedLcd:
                notes.Add("Turn local dimming off. With it on, the backlight changes with image content and no calibration can hold. With it off, treat the panel like the IPS or VA panel underneath.");
                break;
            case PanelType.Oled:
                notes.Add("True black, so no glow between or around exposures.");
                notes.Add("Most OLEDs dim the whole screen as more of it gets bright (automatic brightness limiting). That makes exposure depend on image content. Turn it off if the monitor allows, and check by exposing a small white patch and a full white field for the same time.");
                notes.Add("Static patterns can burn in. Don't leave the framing chart or flat-field target up for long.");
                break;
            case PanelType.TnLcd:
                notes.Add("TN panels shift strongly in brightness and colour with viewing angle, even across the screen at normal distances. Expect large flat-field corrections and avoid them for colour work if you can.");
                break;
        }
        notes.Add("Turn off dynamic contrast, eco modes, ambient light sensors, sharpening and any picture enhancement, and keep the picture mode fixed. Calibration can only absorb a response that doesn't change.");
        if (screenWidth >= 7680)
            notes.Add("8K at 60 Hz needs DisplayPort 1.4 with DSC, HDMI 2.1, or two cables on tiled monitors. If yours uses two cables, confirm Windows shows one 7680 × 4320 display, not two halves. A 16-bit 8K source uses about 350 MB of graphics memory, so plan on a card with at least 4 GB.");
        return notes;
    }
}
