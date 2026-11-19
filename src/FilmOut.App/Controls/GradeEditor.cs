using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FilmOut.Core;

namespace FilmOut.App.Controls;

/// <summary>
/// Edits a <see cref="Core.Grade"/> in place. Each CDL control (offset, power, slope) has a wheel
/// for colour, a master slider, and red, green and blue sliders. The wheel and sliders are two
/// views of the same three numbers.
///
/// The maths works in an additive "domain": stops for offset, log2 for power and slope. A
/// channel's value is master + chroma, where the master is the mean of the three and the chroma
/// (which sums to zero) is what the wheel shows.
/// </summary>
public sealed class GradeEditor : UserControl
{
    /// <summary>Wheel directions of red, green and blue, in degrees anticlockwise from the right.</summary>
    public static readonly double[] ChannelAngles = [100, 220, 340];

    private static readonly string[] ChannelNames = ["Red", "Green", "Blue"];
    private static readonly double[] WheelRange = [1.0, 0.5, 0.5]; // domain units at the rim
    private static readonly double[] WheelSign = [1, -1, 1];        // lower power brightens a channel

    private readonly ColorWheel[] _wheels = new ColorWheel[3];
    private readonly List<Row> _rows = [];
    private Grade _grade = new();
    private bool _updating;

    public GradeEditor()
    {
        var columns = new Grid();
        for (int i = 0; i < 3; i++)
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddColumn(columns, 0, "Offset", "Colour balance, in stops. The master is brightness, and works like a printer light.",
            "Brightness", -3, 3, log: false, "+0.00;-0.00;0.00", reset: 0);
        AddColumn(columns, 1, "Power", "Midtones. Push toward a colour to bring it up in the mids.",
            "Master", -1, 1, log: true, "0.000", reset: 1);
        AddColumn(columns, 2, "Slope", "Highlights and overall contrast in log.",
            "Master", -2, 2, log: true, "0.000", reset: 1);

        var global = new StackPanel { Margin = new Thickness(0, 18, 0, 0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        global.Children.Add(MakeRow("Contrast", -1, 1, log: true, "0.000", reset: 1, () => _grade.Contrast, v => _grade.Contrast = v));
        global.Children.Add(MakeRow("Saturation", 0, 2, log: false, "0.000", reset: 1, () => _grade.Saturation, v => _grade.Saturation = v));
        var resetButton = new Button { Content = "Reset this grade", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        resetButton.Click += (_, _) =>
        {
            _grade.Reset();
            Edited();
        };
        global.Children.Add(resetButton);

        Content = new StackPanel { Children = { columns, global } };
        Refresh();
    }

    public event EventHandler? GradeChanged;

    /// <summary>The grade being edited. Edits change this object directly.</summary>
    public Grade Grade
    {
        get => _grade;
        set
        {
            _grade = value ?? new Grade();
            Refresh();
        }
    }

    public void Refresh()
    {
        _updating = true;
        try
        {
            foreach (var row in _rows)
            {
                double v = row.Get();
                double sliderValue = row.Log ? Math.Log2(Math.Max(v, 1e-6)) : v;
                row.Slider.Value = Math.Clamp(sliderValue, row.Slider.Minimum, row.Slider.Maximum);
                if (!row.Box.IsKeyboardFocusWithin)
                    row.Box.Text = v.ToString(row.Format, CultureInfo.InvariantCulture);
            }
            for (int k = 0; k < 3; k++) _wheels[k].Puck = PuckFromChroma(k, Split(k).Chroma);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Edited()
    {
        Refresh();
        GradeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddColumn(Grid grid, int k, string title, string hint, string masterLabel,
        double min, double max, bool log, string format, double reset)
    {
        var panel = new StackPanel { Margin = new Thickness(k == 0 ? 0 : 10, 0, k == 2 ? 0 : 10, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        panel.Children.Add(new TextBlock
        {
            Text = hint,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.65,
            Margin = new Thickness(0, 2, 0, 10),
            MinHeight = 34,
        });

        var wheel = new ColorWheel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        wheel.PuckDragged += (_, _) =>
        {
            if (_updating) return;
            Compose(k, Split(k).Master, ChromaFromPuck(k, wheel.Puck));
            Edited();
        };
        _wheels[k] = wheel;
        panel.Children.Add(wheel);

        panel.Children.Add(MakeRow(masterLabel, min, max, log, format, reset,
            () => FromDomain(k, Split(k).Master),
            v => Compose(k, ToDomain(k, v), Split(k).Chroma)));
        for (int c = 0; c < 3; c++)
        {
            int channel = c;
            panel.Children.Add(MakeRow(ChannelNames[c], min, max, log, format, reset,
                () => Values(k)[channel],
                v => Values(k)[channel] = v));
        }

        Grid.SetColumn(panel, k);
        grid.Children.Add(panel);
    }

    private FrameworkElement MakeRow(string label, double min, double max, bool log, string format, double reset,
        Func<double> get, Action<double> set)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = (max - min) / 200,
            LargeChange = (max - min) / 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
            ToolTip = "Double-click to reset",
        };
        var box = new TextBox { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(box, 2);
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(box);

        var row = new Row(get, set, log, format, slider, box);
        _rows.Add(row);

        slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            row.Set(log ? Math.Pow(2, e.NewValue) : e.NewValue);
            Edited();
        };
        slider.MouseDoubleClick += (_, _) =>
        {
            row.Set(reset);
            Edited();
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostFocus += (_, _) => Commit();
        return grid;

        void Commit()
        {
            if (_updating) return;
            bool parsed = double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                       || double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
            if (!parsed || !double.IsFinite(v) || (log && v <= 0))
            {
                box.Text = row.Get().ToString(row.Format, CultureInfo.InvariantCulture);
                return;
            }
            if (Math.Abs(v - row.Get()) < 1e-9) return;
            row.Set(v);
            Edited();
            box.Text = row.Get().ToString(row.Format, CultureInfo.InvariantCulture);
        }
    }

    private double[] Values(int k) => k switch { 0 => _grade.OffsetStops, 1 => _grade.Power, _ => _grade.Slope };

    private static double ToDomain(int k, double v) => k == 0 ? v : Math.Log2(Math.Max(v, 1e-6));

    private static double FromDomain(int k, double d) => k == 0 ? d : Math.Pow(2, d);

    private (double Master, double[] Chroma) Split(int k)
    {
        var d = Values(k).Select(v => ToDomain(k, v)).ToArray();
        double master = d.Average();
        return (master, d.Select(x => x - master).ToArray());
    }

    private void Compose(int k, double master, double[] chroma)
    {
        var values = Values(k);
        for (int c = 0; c < 3; c++) values[c] = FromDomain(k, master + chroma[c]);
    }

    private static double[] ChromaFromPuck(int k, Vector puck)
    {
        var chroma = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double a = ChannelAngles[c] * Math.PI / 180;
            chroma[c] = WheelRange[k] * WheelSign[k] * (puck.X * Math.Cos(a) + puck.Y * Math.Sin(a));
        }
        return chroma;
    }

    private static Vector PuckFromChroma(int k, double[] chroma)
    {
        double x = 0, y = 0;
        for (int c = 0; c < 3; c++)
        {
            double a = ChannelAngles[c] * Math.PI / 180;
            x += chroma[c] * Math.Cos(a);
            y += chroma[c] * Math.Sin(a);
        }
        double scale = 2.0 / 3.0 / (WheelRange[k] * WheelSign[k]);
        return new Vector(x * scale, y * scale);
    }

    private sealed record Row(Func<double> Get, Action<double> Set, bool Log, string Format, Slider Slider, TextBox Box);
}
