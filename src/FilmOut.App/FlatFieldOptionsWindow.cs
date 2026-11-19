using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FilmOut.Core;

namespace FilmOut.App;

/// <summary>Asks how the flat-field capture was made, so it can be turned back into exposure.</summary>
public sealed class FlatFieldOptionsWindow : Window
{
    private readonly ComboBox _medium = new() { Width = 300 };
    private readonly ComboBox _encoding = new() { Width = 300 };
    private readonly TextBox _filmGamma = new() { Width = 80, Text = "3.0" };
    private readonly TextBox _mapWidth = new() { Width = 80, Text = "480" };
    private readonly TextBox _percentile = new() { Width = 80, Text = "2" };
    private readonly TextBox _maxBoost = new() { Width = 80, Text = "1.5" };
    private readonly TextBox _smoothing = new() { Width = 80, Text = "1.0" };
    private readonly CheckBox _cropped = new() { Content = "The capture is cropped exactly to the gate (skip marker detection)" };
    private readonly TextBlock _error = new() { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["WarningBrush"], TextWrapping = TextWrapping.Wrap };

    public FlatFieldOptionsWindow()
    {
        Title = "Flat-field capture";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _medium.Items.Add(new ComboBoxItem { Content = "Digital photo of the screen", Tag = CaptureMedium.DigitalCamera });
        _medium.Items.Add(new ComboBoxItem { Content = "Scan of negative-working film (such as print stock)", Tag = CaptureMedium.NegativeWorkingFilm });
        _medium.SelectedIndex = 0;
        _medium.SelectionChanged += (_, _) => _filmGamma.IsEnabled = SelectedMedium == CaptureMedium.NegativeWorkingFilm;
        _filmGamma.IsEnabled = false;

        _encoding.Items.Add(new ComboBoxItem { Content = "Linear (raw conversion or linear scan)", Tag = CaptureEncoding.Linear });
        _encoding.Items.Add(new ComboBoxItem { Content = "sRGB (camera JPEG or ordinary TIFF)", Tag = CaptureEncoding.Srgb });
        _encoding.Items.Add(new ComboBoxItem { Content = "Gamma 2.2", Tag = CaptureEncoding.Gamma22 });
        _encoding.SelectedIndex = 0;

        var form = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddRow(form, "Captured as", _medium);
        AddRow(form, "File encoding", _encoding);
        AddRow(form, "Film contrast (gamma)", _filmGamma);
        AddRow(form, "Map width (texels)", _mapWidth);
        AddRow(form, "Reference percentile", _percentile);
        AddRow(form, "Largest boost", _maxBoost);
        AddRow(form, "Smoothing (texels)", _smoothing);

        var build = new Button { Content = "Build map", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 100 };
        build.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(build);
        buttons.Children.Add(cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            MaxWidth = 560,
            Children =
            {
                new TextBlock
                {
                    Text = "Linear captures give the most accurate map. For film, enter the contrast of the stock so density can be turned back into exposure. "
                         + "The reference percentile sets which part of the field keeps full brightness; darker spots below it are boosted up to the largest boost.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                    Margin = new Thickness(0, 0, 0, 12),
                },
                form,
                new Border { Height = 10 },
                _cropped,
                new Border { Height = 8 },
                _error,
                buttons,
            },
        };
    }

    public FlatFieldBuildOptions Options { get; private set; } = new();

    private CaptureMedium SelectedMedium => _medium.SelectedItem is ComboBoxItem { Tag: CaptureMedium m } ? m : CaptureMedium.DigitalCamera;

    private static void AddRow(Grid form, string label, FrameworkElement field)
    {
        int row = form.RowDefinitions.Count;
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 16, 4) };
        field.Margin = new Thickness(0, 4, 0, 4);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        form.Children.Add(text);
        form.Children.Add(field);
    }

    private void Accept()
    {
        if (!Read(_filmGamma, 0.3, 10, out double gamma, "Film contrast should be between 0.3 and 10.")) return;
        if (!Read(_mapWidth, 16, 4096, out double width, "Map width should be between 16 and 4096.")) return;
        if (!Read(_percentile, 0, 50, out double percentile, "The reference percentile should be between 0 and 50.")) return;
        if (!Read(_maxBoost, 1, 4, out double boost, "The largest boost should be between 1 and 4.")) return;
        if (!Read(_smoothing, 0, 20, out double smoothing, "Smoothing should be between 0 and 20 texels.")) return;

        Options = new FlatFieldBuildOptions
        {
            Medium = SelectedMedium,
            Encoding = _encoding.SelectedItem is ComboBoxItem { Tag: CaptureEncoding e } ? e : CaptureEncoding.Linear,
            FilmGamma = gamma,
            MapWidth = (int)Math.Round(width),
            ReferencePercentile = percentile,
            MaxBoost = boost,
            SmoothingTexels = smoothing,
            CaptureIsCroppedToGate = _cropped.IsChecked == true,
        };
        DialogResult = true;
    }

    private bool Read(TextBox box, double min, double max, out double value, string message)
    {
        bool ok = (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   || double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                  && value >= min && value <= max;
        _error.Text = ok ? "" : message;
        if (!ok) box.Focus();
        return ok;
    }
}
