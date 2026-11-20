using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FilmOut.App.Controls;
using FilmOut.Core;
using FilmOut.Rendering;
using Microsoft.Win32;
using System.IO;

namespace FilmOut.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private enum Overlay { None, Framing, FlatTarget, DefectHunt }

    private readonly OutputEngine _engine = new();
    private readonly StationSettings _station = StationSettings.Load();
    private readonly ObservableCollection<ImageRow> _rows = [];
    private readonly GradeEditor _imageGrade = new();
    private readonly GradeEditor _folderGrade = new();
    private readonly DispatcherTimer _jobSaveTimer;
    private readonly DispatcherTimer _stationSaveTimer;

    private Job? _job;
    private FlatFieldMap? _flatMap;
    private SerialTrigger? _serial;
    private RunLog? _runLog;
    private Grade? _gradeClipboard;
    private bool _suppress = true;
    private bool _outputRunning;
    private bool _inRun;
    private int _outputWidth, _outputHeight;
    private double _measuredHz;
    private Overlay _overlay;
    private int _cursorX, _cursorY;
    private double[] _huntField = [1, 1, 1];
    private string _runPhaseWord = "IDLE";
    private int _runNumber;

    public MainWindow()
    {
        InitializeComponent();

        _jobSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
        _jobSaveTimer.Tick += (_, _) => { _jobSaveTimer.Stop(); SaveJob(); };
        _stationSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.8) };
        _stationSaveTimer.Tick += (_, _) => { _stationSaveTimer.Stop(); SaveStation(); };

        ImageList.ItemsSource = _rows;
        ImageGradeHost.Content = _imageGrade;
        FolderGradeHost.Content = _folderGrade;
        _imageGrade.GradeChanged += (_, _) =>
        {
            if (Selected is { } image) RowFor(image)?.Refresh(_job!, Hz);
            UpdateOffsetTotal();
            JobChanged();
            PushSetupFrame();
        };
        _folderGrade.GradeChanged += (_, _) =>
        {
            UpdateOffsetTotal(); // eweiojwefijowef
            JobChanged();
            PushSetupFrame();
        };

        Fill(EncodingCombo, (SourceEncoding.Srgb, "sRGB"), (SourceEncoding.Gamma22, "Gamma 2.2"),
            (SourceEncoding.Gamma24, "Gamma 2.4"), (SourceEncoding.Linear, "Linear"));
        Fill(RotateCombo, (0, "None"), (90, "90° clockwise"), (180, "180°"), (270, "90° anticlockwise"));
        Fill(FitCombo, (FitMode.Fit, "Fit inside the gate"), (FitMode.Fill, "Fill the gate"),
            (FitMode.Stretch, "Stretch to the gate"), (FitMode.Native, "Native pixels"));
        Fill(PanelCombo, Enum.GetValues<PanelType>().Select(p => (p, PanelAdvice.Describe(p))).ToArray());
        Fill(BitDepthCombo, (8, "8 bits"), (10, "10 bits"));
        Fill(DefectKindCombo, (DefectKind.Dead, "Dead"), (DefectKind.Stuck, "Stuck"), (DefectKind.Weak, "Weak"));
        Fill(BaudCombo, new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800 }.Select(b => (b, $"{b} baud")).ToArray());

        _engine.EventRaised += e => Dispatcher.InvokeAsync(() => OnEngineEvent(e));
        Loaded += OnLoaded;
    }

    // ---- Startup and shutdown -----------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadFlatMap();
        RefreshPorts();
        RefreshOutputs();
        RefreshStationFields();
        RefreshFolderFields();
        RefreshKeyFields();
        SetRunUi(false);
        _suppress = false;

        if (_station.LastJobFolder is { } folder && Directory.Exists(folder)) LoadJob(folder);

        if (OutputCombo.SelectedItem is OutputInfo output
            && string.Equals(output.DeviceName, _station.OutputDeviceName, StringComparison.OrdinalIgnoreCase)
            && !IsControlDisplay(output))
            StartOutput(output, testWindow: false);

        if (_station.Serial.ConnectOnStart && _station.Serial.PortName is not null) ConnectSerial();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_inRun && MessageBox.Show(this, "A run is in progress. Close FilmOut anyway?", "FilmOut",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CommitFocusedField();
        SaveJob();
        SaveStation();
        _serial?.Dispose();
        _engine.Dispose();
        _runLog?.Dispose();
        base.OnClosed(e);
    }

    // ---- Job ------------------------------------------------------------------------------------

    private JobImage? Selected => (ImageList.SelectedItem as ImageRow)?.Image;

    private List<JobImage> SelectedImages()
    {
        var images = ImageList.SelectedItems.OfType<ImageRow>().Select(r => r.Image).ToList();
        return images.Count > 0 ? images : Selected is { } s ? [s] : [];
    }

    private ImageRow? RowFor(JobImage image) => _rows.FirstOrDefault(r => ReferenceEquals(r.Image, image));

    private double Hz => _measuredHz > 1 ? _measuredHz : 60.0;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder of images for this job" };
        if (_job is not null) dialog.InitialDirectory = _job.Folder;
        if (dialog.ShowDialog(this) == true) LoadJob(dialog.FolderName);
    }

    private void LoadJob(string folder)
    {
        CommitFocusedField();
        SaveJob();
        Job job;
        try
        {
            job = Job.Open(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't open the job folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _job = job;
        _station.LastJobFolder = job.Folder;
        StationChanged();
        _rows.Clear();
        foreach (var image in job.Images) _rows.Add(new ImageRow(image));

        JobPathText.Text = job.Folder;
        WarningsText.Text = string.Join("\n", job.Warnings);
        WarningsText.Visibility = job.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _folderGrade.Grade = job.Settings.FolderGrade;
        StartAtBox.Text = "1";

        RefreshFolderFields();
        UpdateStats();
        ImageList.SelectedIndex = _rows.Count > 0 ? 0 : -1;
        UpdateImagePanel();
        PushSetupFrame();
        Status($"Opened {job.Images.Count} images from {Path.GetFileName(job.Folder)}.");
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null) LoadJob(_job.Folder);
        _engine.ReloadImages();
    }

    private void JobChanged()
    {
        _jobSaveTimer.Stop();
        _jobSaveTimer.Start();
    }

    private void SaveJob()
    {
        try { _job?.Save(); }
        catch (Exception ex) { Status($"Couldn't save the job file: {ex.Message}"); }
    }

    private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateImagePanel();
        if (!_inRun) PushSetupFrame();
    }

    private void UpdateStats()
    {
        if (_job is null || _job.Images.Count == 0)
        {
            MinTimeText.Text = MaxTimeText.Text = RangeText.Text = TotalText.Text = "—";
            MinTimeDetail.Text = MaxTimeDetail.Text = RangeDetail.Text = TotalDetail.Text = "";
            return;
        }

        var stats = JobStatistics.Compute(_job, Hz);
        MinTimeText.Text = ExposureMath.FormatSeconds(stats.MinSeconds);
        MinTimeDetail.Text = $"Image {stats.MinImage!.Position}, {stats.MinFrames} frames";
        MaxTimeText.Text = ExposureMath.FormatSeconds(stats.MaxSeconds);
        MaxTimeDetail.Text = $"Image {stats.MaxImage!.Position}, {stats.MaxFrames} frames";
        RangeText.Text = $"{stats.RangeStops.ToString("0.00", Invariant)} stops";
        RangeDetail.Text = $"{stats.RangePoints.ToString("0.0", Invariant)} printer points";
        TotalText.Text = ExposureMath.FormatDuration(stats.TotalSeconds);
        TotalDetail.Text = stats.CoarselyQuantizedCount > 0
            ? $"{stats.ImageCount} images. {stats.CoarselyQuantizedCount} round to whole refreshes by more than {JobStatistics.CoarseThresholdPoints} points."
            : $"{stats.ImageCount} images, {stats.TotalFrames:N0} refreshes of exposure";

        foreach (var row in _rows) row.Refresh(_job, Hz);
    }

    // ---- Image tab ------------------------------------------------------------------------------

    private void UpdateImagePanel()
    {
        using var _ = Suppress();
        if (_job is null || Selected is not { } image)
        {
            ImageTitle.Text = "No image selected";
            ImageSubtitle.Text = _job is null ? "Open a job folder to begin." : "";
            TimeBox.Text = "";
            TimeInfo.Text = "";
            ImagePanel.IsEnabled = false;
            _imageGrade.Grade = new Grade();
            OffsetTotalInfo.Text = "";
            return;
        }

        ImagePanel.IsEnabled = !_inRun;
        int count = ImageList.SelectedItems.Count;
        ImageTitle.Text = image.FileName;
        ImageSubtitle.Text = $"Image {image.Position} of {_job.Images.Count}" + (count > 1 ? $", {count} selected" : "");
        if (!TimeBox.IsKeyboardFocusWithin) TimeBox.Text = _job.TimeFor(image).ToString("0.000", Invariant);
        IgnoreFolderGradeBox.IsChecked = image.Settings.IgnoreFolderGrade;
        _imageGrade.Grade = image.Settings.Grade;
        UpdateOffsetTotal();
        UpdateTimeInfo(image);
    }

    private void UpdateOffsetTotal()
{
    if (_job is null || Selected is not { } image)
    {
        OffsetTotalInfo.Text = "";
        return;
    }

    bool ignored = image.Settings.IgnoreFolderGrade;
    var folder = ignored ? new Grade() : _job.Settings.FolderGrade;
    var own = image.Settings.Grade;

    static bool Neutral(double[] values, double neutral) => values.All(v => v == neutral);
    static string Stops(double v) => v.ToString("+0.00;-0.00;0.00", Invariant);
    static string Ratio(double v) => v.ToString("0.000", Invariant);
    static double GeoMean(double[] v) => Math.Pow(2, v.Select(x => Math.Log2(Math.Max(x, 1e-6))).Average());
    static string Channels(double[] v, Func<double, string> f) => $"R {f(v[0])}   G {f(v[1])}   B {f(v[2])}";

    // Offsets add; power and slope multiply (the editor's master for those is the geometric mean).
    var offset = folder.OffsetStops.Zip(own.OffsetStops, (a, b) => a + b).ToArray();
    var power = folder.Power.Zip(own.Power, (a, b) => a * b).ToArray();
    var slope = folder.Slope.Zip(own.Slope, (a, b) => a * b).ToArray();

    // Each total is exact only when nothing in between changes it.
    bool folderSatContrastNeutral = folder.Saturation == 1 && folder.Contrast == 1;
    bool offsetExact = folderSatContrastNeutral && Neutral(folder.Power, 1)
                       && (Neutral(own.Slope, 1) || Neutral(folder.OffsetStops, 0));
    bool slopeExact = folderSatContrastNeutral && Neutral(folder.Power, 1);
    bool powerExact = folderSatContrastNeutral && Neutral(own.Slope, 1) && Neutral(own.OffsetStops, 0);

    static string Mark(bool exact) => exact ? "" : "≈ ";

    var lines = new List<string>();
    if (ignored)
        lines.Add("The folder grade is ignored for this image, so these are the image's own values.");
    lines.Add($"{Mark(offsetExact)}Offset (stops): brightness {Stops(folder.OffsetStops.Average())} folder + {Stops(own.OffsetStops.Average())} image = {Stops(offset.Average())}   ({Channels(offset, Stops)})");
    lines.Add($"{Mark(powerExact)}Power: master {Ratio(GeoMean(folder.Power))} folder × {Ratio(GeoMean(own.Power))} image = {Ratio(GeoMean(power))}   ({Channels(power, Ratio)})");
    lines.Add($"{Mark(slopeExact)}Slope: master {Ratio(GeoMean(folder.Slope))} folder × {Ratio(GeoMean(own.Slope))} image = {Ratio(GeoMean(slope))}   ({Channels(slope, Ratio)})");
    if (!(offsetExact && powerExact && slopeExact))
        lines.Add("≈ means the two grades interact (for example the folder's power or contrast acts between them), so that total is a guide rather than an exact equivalent.");

    OffsetTotalInfo.Text = string.Join("\n", lines);
}



    private void UpdateTimeInfo(JobImage image)
    {
        double t = _job!.TimeFor(image);
        int frames = ExposureMath.ToFrames(t, Hz);
        double actual = ExposureMath.FramesToSeconds(frames, Hz);
        double error = ExposureMath.QuantizationErrorPoints(t, Hz);
        string rate = _measuredHz > 1 ? $"{_measuredHz.ToString("0.000", Invariant)} Hz" : "60 Hz (not measured yet)";
        string text = $"{frames} refreshes at {rate} = {actual.ToString("0.0000", Invariant)} s, " +
                      $"{error.ToString("+0.00;-0.00;0.00", Invariant)} points from the time entered" +
                      (image.Settings.TimeSeconds is null ? ". This is the folder default." : ".");
        if (Math.Abs(error) > JobStatistics.CoarseThresholdPoints)
            text += " That rounding is large enough to matter; a longer time or a higher refresh rate makes it smaller.";
        TimeInfo.Text = text;
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitTime();
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e) => CommitTime();

    private void CommitTime()
    {
        if (_suppress || _job is null || Selected is not { } image) return;
        if (!ExposureMath.TryParseSeconds(TimeBox.Text, out double t))
        {
            Status($"“{TimeBox.Text}” isn't a time. Enter seconds, such as 1.25.");
            TimeBox.Text = _job.TimeFor(image).ToString("0.000", Invariant);
            return;
        }
        t = Math.Round(t, 4);
        if (Math.Abs(t - _job.TimeFor(image)) < 1e-9 && image.Settings.TimeSeconds is not null) return;
        image.Settings.TimeSeconds = t;
        TimesChanged();
    }

    private void TimeStep_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        string step = (string)((FrameworkElement)sender).Tag;
        foreach (var image in SelectedImages())
        {
            double t = _job.TimeFor(image);
            t = step switch
            {
                "-stop" => ExposureMath.AddStops(t, -1),
                "+stop" => ExposureMath.AddStops(t, 1),
                "-pt" => ExposureMath.AddPoints(t, -1),
                _ => ExposureMath.AddPoints(t, 1),
            };
            image.Settings.TimeSeconds = Math.Round(t, 4);
        }
        TimesChanged();
    }

    private void ApplyTimeToSelected_Click(object sender, RoutedEventArgs e)
    {
        CommitTime();
        if (_job is null || Selected is not { } source) return;
        double t = _job.TimeFor(source);
        foreach (var image in SelectedImages()) image.Settings.TimeSeconds = t;
        TimesChanged();
    }

    private void UseDefaultTime_Click(object sender, RoutedEventArgs e)
    {
        foreach (var image in SelectedImages()) image.Settings.TimeSeconds = null;
        TimesChanged();
    }

    private void TimesChanged()
    {
        JobChanged();
        UpdateStats();
        UpdateImagePanel();
    }

    private void IgnoreFolderGrade_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || Selected is not { } image) return;
        image.Settings.IgnoreFolderGrade = IgnoreFolderGradeBox.IsChecked == true;
        RowFor(image)?.Refresh(_job!, Hz);
        UpdateOffsetTotal();
        JobChanged();
        PushSetupFrame();
    }

    private void CopyGrade_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } image) return;
        _gradeClipboard = image.Settings.Grade.Clone();
        Status($"Copied the grade of image {image.Position}.");
    }

    private void PasteGrade_Click(object sender, RoutedEventArgs e)
    {
        if (_gradeClipboard is null || _job is null)
        {
            Status("Copy a grade first.");
            return;
        }
        var targets = SelectedImages();
        foreach (var image in targets)
        {
            image.Settings.Grade.CopyFrom(_gradeClipboard);
            RowFor(image)?.Refresh(_job, Hz);
        }
        _imageGrade.Refresh();
        UpdateOffsetTotal();
        JobChanged();
        PushSetupFrame();
        Status($"Pasted the grade to {targets.Count} image(s).");
    }

    // ---- Folder tab -----------------------------------------------------------------------------

    private void RefreshFolderFields()
    {
        using var _ = Suppress();
        FolderPanel.IsEnabled = _job is not null && !_inRun;
        if (_job is null) return;
        var s = _job.Settings;
        PositiveRadio.IsChecked = s.Mode == ImageMode.Positive;
        NegativeRadio.IsChecked = s.Mode == ImageMode.Negative;
        NegativePanel.IsEnabled = s.Mode == ImageMode.Negative;
        Select(EncodingCombo, s.SourceEncoding);
        Select(RotateCombo, s.Orientation.RotateDegrees);
        FlipHBox.IsChecked = s.Orientation.FlipHorizontal;
        FlipVBox.IsChecked = s.Orientation.FlipVertical;
        Select(FitCombo, s.Fit);
        ScaleBox.Text = s.Scale.ToString("0.000", Invariant);
        OffsetXBox.Text = (s.OffsetX * 100).ToString("0.00", Invariant);
        OffsetYBox.Text = (s.OffsetY * 100).ToString("0.00", Invariant);
        DefaultTimeBox.Text = s.DefaultTimeSeconds.ToString("0.000", Invariant);
        NegGamma0.Text = s.Negative.PrintGamma[0].ToString("0.00", Invariant);
        NegGamma1.Text = s.Negative.PrintGamma[1].ToString("0.00", Invariant);
        NegGamma2.Text = s.Negative.PrintGamma[2].ToString("0.00", Invariant);
        NegPoints0.Text = s.Negative.PrinterPoints[0].ToString("0.0", Invariant);
        NegPoints1.Text = s.Negative.PrinterPoints[1].ToString("0.0", Invariant);
        NegPoints2.Text = s.Negative.PrinterPoints[2].ToString("0.0", Invariant);
        NegDensityBox.Text = s.Negative.DensityRange.ToString("0.00", Invariant);
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || _job is null) return;
        _job.Settings.Mode = NegativeRadio.IsChecked == true ? ImageMode.Negative : ImageMode.Positive;
        NegativePanel.IsEnabled = _job.Settings.Mode == ImageMode.Negative;
        JobChanged();
        PushSetupFrame();
    }

    private void FolderCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _job is null) return;
        var s = _job.Settings;
        if (sender == EncodingCombo && TryGet(EncodingCombo, out SourceEncoding encoding)) s.SourceEncoding = encoding;
        else if (sender == RotateCombo && TryGet(RotateCombo, out int rotation)) s.Orientation.RotateDegrees = rotation;
        else if (sender == FitCombo && TryGet(FitCombo, out FitMode fit)) s.Fit = fit;
        JobChanged();
        PushSetupFrame();
    }

    private void Flip_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || _job is null) return;
        _job.Settings.Orientation.FlipHorizontal = FlipHBox.IsChecked == true;
        _job.Settings.Orientation.FlipVertical = FlipVBox.IsChecked == true;
        JobChanged();
        PushSetupFrame();
    }

    // ---- Numeric fields (folder and station) ----------------------------------------------------

    private void Number_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitNumber((TextBox)sender);
    }

    private void Number_LostFocus(object sender, RoutedEventArgs e) => CommitNumber((TextBox)sender);

    private void CommitNumber(TextBox box)
    {
        if (_suppress) return;
        string tag = box.Tag as string ?? "";
        if (!TryParseNumber(box.Text, out double v))
        {
            Status($"“{box.Text}” isn't a number.");
            RefreshFolderFields();
            RefreshStationFields();
            return;
        }

        var s = _job?.Settings;
        switch (tag)
        {
            case "time.default" when s is not null && v > 0:
                s.DefaultTimeSeconds = Math.Round(v, 4);
                JobChanged();
                UpdateStats();
                UpdateImagePanel();
                break;
            case "frame.scale" when s is not null && v > 0:
                s.Scale = v;
                FolderEdited();
                break;
            case "frame.offx" when s is not null:
                s.OffsetX = v / 100;
                FolderEdited();
                break;
            case "frame.offy" when s is not null:
                s.OffsetY = v / 100;
                FolderEdited();
                break;
            case "neg.density" when s is not null && v > 0.3:
                s.Negative.DensityRange = v;
                FolderEdited();
                break;
            case var t when t.StartsWith("neg.gamma.") && s is not null && v > 0.1:
                s.Negative.PrintGamma[t[^1] - '0'] = v;
                FolderEdited();
                break;
            case var t when t.StartsWith("neg.points.") && s is not null:
                s.Negative.PrinterPoints[t[^1] - '0'] = v;
                FolderEdited();
                break;
            case "eotf" when v is > 1 and < 3.5:
                _station.EotfGamma = v;
                StationEdited();
                break;
            case "gate.x" or "gate.y" or "gate.w" or "gate.h":
                UpdateGate(tag, (int)Math.Round(v));
                break;
            case "cursor.x":
            case "cursor.y":
                if (tag == "cursor.x") _cursorX = (int)v; else _cursorY = (int)v;
                MoveCursor(0, 0);
                break;
            default:
                Status($"{v.ToString(Invariant)} is out of range for that setting.");
                break;
        }
        RefreshFolderFields();
        RefreshStationFields();
    }

    private void FolderEdited()
    {
        JobChanged();
        PushSetupFrame();
    }

    /// <summary>Commits a text box that still has focus (e.g. when a button is clicked or the window closes).</summary>
    private void CommitFocusedField()
    {
        if (Keyboard.FocusedElement is not TextBox box) return;
        if (box == TimeBox) CommitTime();
        else if (box.Style == (Style)Resources["NumberBox"]) CommitNumber(box);
        else if (box == TriggerKeysBox || box == AbortKeyBox) CommitKeys();
    }

    // ---- Station tab ----------------------------------------------------------------------------

    private void RefreshStationFields()
    {
        using var _ = Suppress();
        StationPanel.IsEnabled = !_inRun;
        Select(PanelCombo, _station.Panel);
        GammaBox.Text = _station.EotfGamma.ToString("0.00", Invariant);
        Select(BitDepthCombo, _station.PanelBitDepth);
        PanelAdviceText.Text = string.Join("\n\n", PanelAdvice.Notes(_station.Panel, _outputWidth));

        var gate = EffectiveGate();
        GateXBox.Text = gate?.X.ToString(Invariant) ?? "";
        GateYBox.Text = gate?.Y.ToString(Invariant) ?? "";
        GateWBox.Text = gate?.Width.ToString(Invariant) ?? "";
        GateHBox.Text = gate?.Height.ToString(Invariant) ?? "";

        FlatEnabledBox.IsChecked = _station.FlatFieldEnabled;
        FlatEnabledBox.IsEnabled = _flatMap is not null;
        RefreshFlatInfo();

        DefectsEnabledBox.IsChecked = _station.DefectsEnabled;
        CursorXBox.Text = _cursorX.ToString(Invariant);
        CursorYBox.Text = _cursorY.ToString(Invariant);
        if (DefectKindCombo.SelectedItem is null) DefectKindCombo.SelectedIndex = 0;
        RefreshDefectList();
    }

    private GateRect? EffectiveGate()
    {
        if (_station.Gate is { } gate) return _outputWidth > 0 ? gate.ClampTo(_outputWidth, _outputHeight) : gate;
        return _outputWidth > 0 ? new GateRect(0, 0, _outputWidth, _outputHeight) : null;
    }

    private void UpdateGate(string tag, int value)
    {
        var gate = EffectiveGate() ?? new GateRect(0, 0, 3840, 2160);
        gate = tag switch
        {
            "gate.x" => gate with { X = value },
            "gate.y" => gate with { Y = value },
            "gate.w" => gate with { Width = value },
            _ => gate with { Height = value },
        };
        if (_outputWidth > 0) gate = gate.ClampTo(_outputWidth, _outputHeight);
        _station.Gate = gate;
        StationEdited();
    }

    private void FullScreenGate_Click(object sender, RoutedEventArgs e)
    {
        _station.Gate = null;
        StationEdited();
        RefreshStationFields();
    }

    private void PanelCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !TryGet(PanelCombo, out PanelType panel)) return;
        _station.Panel = panel;
        (_station.EotfGamma, _station.StaticContrast) = PanelAdvice.Defaults(panel);
        StationEdited();
        RefreshStationFields();
    }

    private void BitDepth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !TryGet(BitDepthCombo, out int bits)) return;
        _station.PanelBitDepth = bits;
        StationEdited();
    }

    /// <summary>A station setting that affects the picture changed: save it, and update the output.</summary>
    private void StationEdited()
    {
        StationChanged();
        PushCalibration();
        PushSetupFrame();
        RefreshFlatInfo();
    }

    private void StationChanged()
    {
        _stationSaveTimer.Stop();
        _stationSaveTimer.Start();
    }

    private void SaveStation()
    {
        try { _station.Save(); }
        catch (Exception ex) { Status($"Couldn't save the station settings: {ex.Message}"); }
    }

    private OutputCalibration BuildCalibration(int width, int height)
    {
        var gate = (_station.Gate ?? new GateRect(0, 0, width, height)).ClampTo(width, height);
        var flat = _station.FlatFieldEnabled ? _flatMap : null;
        var defects = _station.DefectsEnabled ? _station.Defects.Select(d => d.Clone()).ToList() : new List<PixelDefect>();
        return new OutputCalibration(gate, _station.EotfGamma, _station.PanelBitDepth, flat, defects);
    }

    private void PushCalibration()
    {
        if (_outputRunning) _engine.UpdateCalibration(BuildCalibration(_outputWidth, _outputHeight));
    }

    // Overlays: framing chart, flat-field target, defect hunting.

    private void Overlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var toggle = (ToggleButton)sender;
        var which = toggle == FramingToggle ? Overlay.Framing : toggle == FlatTargetToggle ? Overlay.FlatTarget : Overlay.DefectHunt;
        using (Suppress())
        {
            if (toggle.IsChecked == true)
            {
                _overlay = which;
                foreach (var other in new[] { FramingToggle, FlatTargetToggle, HuntToggle })
                    if (other != toggle) other.IsChecked = false;
            }
            else if (_overlay == which)
            {
                _overlay = Overlay.None;
            }
        }
        if (_overlay == Overlay.DefectHunt) Keyboard.ClearFocus();
        PushSetupFrame();
    }

    private void ClearOverlays()
    {
        using var _ = Suppress();
        FramingToggle.IsChecked = FlatTargetToggle.IsChecked = HuntToggle.IsChecked = false;
        _overlay = Overlay.None;
    }

    private void Preview_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppress) PushSetupFrame();
    }

    private void FlatLevel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FlatLevelText is not null) FlatLevelText.Text = e.NewValue.ToString("0.00", Invariant);
        if (!_suppress) PushSetupFrame();
    }

    // Flat field.

    private void LoadFlatMap()
    {
        if (_station.FlatFieldMapPath is not { } path || !File.Exists(path)) return;
        try
        {
            _flatMap = FlatFieldMap.Load(path);
        }
        catch (Exception ex)
        {
            Status($"Couldn't read the flat-field map: {ex.Message}");
        }
    }

    private void RefreshFlatInfo()
    {
        if (_flatMap is null)
        {
            FlatInfoText.Text = "No map yet. Build one from a capture of the flat-field target.";
            return;
        }
        var (min, max, mean) = _flatMap.Statistics();
        var gate = EffectiveGate();
        string text = $"{_flatMap.Width} × {_flatMap.Height} map from {_flatMap.Source}, built {_flatMap.Created:d MMM yyyy, HH:mm}. " +
                      $"Gains run from {min.ToString("0.000", Invariant)} to {max.ToString("0.000", Invariant)}, " +
                      $"costing about {(-Math.Log2(mean)).ToString("0.00", Invariant)} stops on average.";
        if (gate is not null && gate != _flatMap.Gate)
            text += " It was built for a different gate, so it's off until you rebuild it.";
        FlatInfoText.Text = text;
    }

    private void FlatEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _station.FlatFieldEnabled = FlatEnabledBox.IsChecked == true;
        StationEdited();
    }

    private async void BuildFlat_Click(object sender, RoutedEventArgs e)
    {
        if (EffectiveGate() is not { } gate)
        {
            Status("Start the output first, so FilmOut knows the screen size and gate.");
            return;
        }

        var pick = new OpenFileDialog
        {
            Title = "Choose the capture of the flat-field target",
            Filter = "Images (*.tif, *.tiff, *.png, *.jpg)|*.tif;*.tiff;*.png;*.jpg;*.jpeg|All files|*.*",
        };
        if (pick.ShowDialog(this) != true) return;
        var optionsWindow = new FlatFieldOptionsWindow { Owner = this };
        if (optionsWindow.ShowDialog() != true) return;

        var options = optionsWindow.Options;
        string file = pick.FileName;
        Status("Building the flat-field map…");
        Cursor = Cursors.Wait;
        try
        {
            var result = await Task.Run(() =>
            {
                var image = ImageDecoder.Decode(file);
                var capture = CaptureImage.FromRgba64(image.Rgba64, image.Width, image.Height, options.MapWidth * 3, options.Encoding);
                return FlatFieldBuilder.Build(capture, gate, options, Path.GetFileName(file));
            });

            string path = Path.Combine(StationSettings.Folder, $"flatfield-{DateTime.Now:yyyyMMdd-HHmmss}.ffmap");
            result.Map.Save(path);
            _flatMap = result.Map;
            _station.FlatFieldMapPath = path;
            _station.FlatFieldEnabled = true;
            StationEdited();
            RefreshStationFields();
            Status("Built the flat-field map and switched it on.");
            MessageBox.Show(this, result.Summary, "Flat-field map built", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status("The flat-field map wasn't built.");
            MessageBox.Show(this, ex.Message, "Couldn't build the flat-field map", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void RemoveFlat_Click(object sender, RoutedEventArgs e)
    {
        _flatMap = null;
        _station.FlatFieldMapPath = null;
        _station.FlatFieldEnabled = false;
        StationEdited();
        RefreshStationFields();
    }

    // Pixel defects.

    private void DefectsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _station.DefectsEnabled = DefectsEnabledBox.IsChecked == true;
        StationEdited();
    }

    private void HuntField_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((FrameworkElement)sender).Tag;
        _huntField = tag.Split(',').Select(v => double.Parse(v, Invariant)).ToArray();
        if (_overlay != Overlay.DefectHunt) HuntToggle.IsChecked = true; // raises Overlay_Changed
        else PushSetupFrame();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_overlay == Overlay.DefectHunt && !_inRun && Keyboard.FocusedElement is not TextBox)
        {
            var mods = Keyboard.Modifiers;
            int step = mods.HasFlag(ModifierKeys.Control) ? 100 : mods.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            switch (e.Key)
            {
                case Key.Left: MoveCursor(-step, 0); e.Handled = true; return;
                case Key.Right: MoveCursor(step, 0); e.Handled = true; return;
                case Key.Up: MoveCursor(0, -step); e.Handled = true; return;
                case Key.Down: MoveCursor(0, step); e.Handled = true; return;
                case Key.M: MarkDefect(); e.Handled = true; return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void MoveCursor(int dx, int dy)
    {
        int maxX = Math.Max(0, _outputWidth - 1), maxY = Math.Max(0, _outputHeight - 1);
        _cursorX = Math.Clamp(_cursorX + dx, 0, maxX);
        _cursorY = Math.Clamp(_cursorY + dy, 0, maxY);
        using (Suppress())
        {
            CursorXBox.Text = _cursorX.ToString(Invariant);
            CursorYBox.Text = _cursorY.ToString(Invariant);
        }
        PushSetupFrame();
    }

    private void MarkDefect_Click(object sender, RoutedEventArgs e) => MarkDefect();

    private void MarkDefect()
    {
        if (!TryGet(DefectKindCombo, out DefectKind kind)) kind = DefectKind.Dead;
        var channels = (DefectRedBox.IsChecked == true ? ColorChannels.Red : ColorChannels.None)
                     | (DefectGreenBox.IsChecked == true ? ColorChannels.Green : ColorChannels.None)
                     | (DefectBlueBox.IsChecked == true ? ColorChannels.Blue : ColorChannels.None);
        if (channels == ColorChannels.None)
        {
            Status("Choose at least one colour channel for the defect.");
            return;
        }

        double level = 0;
        if (kind != DefectKind.Dead
            && (!TryParseNumber(DefectLevelBox.Text, out level) || level < 0 || level > 1 || (kind == DefectKind.Weak && level <= 0)))
        {
            Status(kind == DefectKind.Weak
                ? "A weak pixel's level is the fraction of light it still gives, above 0 and up to 1. Mark it dead if it gives none."
                : "A stuck pixel's level is how bright it's stuck, from 0 to 1.");
            return;
        }

        _station.Defects.RemoveAll(d => d.X == _cursorX && d.Y == _cursorY);
        _station.Defects.Add(new PixelDefect { X = _cursorX, Y = _cursorY, Kind = kind, Channels = channels, Level = level });
        RefreshDefectList();
        StationEdited();
        Status($"Marked a defect at ({_cursorX}, {_cursorY}).");
    }

    private void RemoveDefect_Click(object sender, RoutedEventArgs e)
    {
        if (DefectList.SelectedItem is not PixelDefect defect) return;
        _station.Defects.Remove(defect);
        RefreshDefectList();
        StationEdited();
    }

    private void RefreshDefectList()
    {
        DefectList.ItemsSource = null;
        DefectList.ItemsSource = _station.Defects.OrderBy(d => d.Y).ThenBy(d => d.X).ToList();
    }

    // ---- Output display -------------------------------------------------------------------------

    private void RefreshOutputs()
    {
        IReadOnlyList<OutputInfo> outputs;
        try
        {
            outputs = OutputEngine.EnumerateOutputs();
        }
        catch (Exception ex)
        {
            Status($"Couldn't list the displays: {ex.Message}");
            return;
        }
        OutputCombo.ItemsSource = outputs;
        OutputCombo.SelectedItem =
            outputs.FirstOrDefault(o => string.Equals(o.DeviceName, _station.OutputDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? outputs.FirstOrDefault(o => !IsControlDisplay(o))
            ?? outputs.FirstOrDefault();
    }

    private bool IsControlDisplay(OutputInfo output)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero
            && string.Equals(MonitorInterop.DeviceNameForWindow(hwnd), output.DeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private void StartOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_outputRunning)
        {
            _engine.Stop();
            return;
        }
        if (OutputCombo.SelectedItem is not OutputInfo output)
        {
            Status("Choose the display the camera faces.");
            return;
        }

        bool test = IsControlDisplay(output);
        if (test && MessageBox.Show(this,
                "This is the display FilmOut's controls are on, so the output will open as a half-size test window instead of covering it. " +
                "For shooting, use a second display.", "Test window", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;
        StartOutput(output, test);
    }

    private void StartOutput(OutputInfo output, bool testWindow)
    {
        _station.OutputDeviceName = output.DeviceName;
        StationChanged();
        try
        {
            int w = testWindow ? output.Width / 2 : output.Width, h = testWindow ? output.Height / 2 : output.Height;
            _engine.Start(output, BuildCalibration(w, h), testWindow);
            StartOutputButton.IsEnabled = false;
            Status("Starting the output…");
        }
        catch (Exception ex)
        {
            Status($"Couldn't start the output: {ex.Message}");
        }
    }

    /// <summary>Sends what the output should show in setup mode: the selected image or a test pattern.</summary>
    private void PushSetupFrame()
    {
        if (!_outputRunning || _inRun) return;

        double level = FlatLevelSlider.Value;
        FrameParameters frame = _overlay switch
        {
            Overlay.Framing => new FrameParameters { Content = OutputContent.FramingChart, ApplyCorrections = false },
            Overlay.FlatTarget => new FrameParameters
            {
                Content = OutputContent.FlatFieldTarget,
                FieldCode = [level, level, level],
                ApplyCorrections = FlatTargetCorrectedBox.IsChecked == true,
            },
            Overlay.DefectHunt => new FrameParameters
            {
                Content = OutputContent.DefectHunt,
                FieldCode = (double[])_huntField.Clone(),
                CursorX = _cursorX,
                CursorY = _cursorY,
                ApplyCorrections = HuntCorrectedBox.IsChecked == true,
            },
            _ when _job is not null && Selected is { } image => ImageParameters(_job, image),
            _ => new FrameParameters { Content = OutputContent.Black },
        };
        _engine.ShowSetup(frame);

        // Keep the neighbours decoded so stepping through the list is quick.
        if (_job is not null && ImageList.SelectedIndex >= 0)
        {
            int i = ImageList.SelectedIndex;
            var nearby = new[] { i - 1, i, i + 1 }
                .Where(j => j >= 0 && j < _job.Images.Count)
                .Select(j => _job.Images[j].FullPath);
            _engine.Prefetch(nearby);
        }
    }

    private static FrameParameters ImageParameters(Job job, JobImage image)
    {
        var s = job.Settings;
        return new FrameParameters
        {
            Content = OutputContent.Image,
            ImagePath = image.FullPath,
            Encoding = s.SourceEncoding,
            RotateDegrees = s.Orientation.RotateDegrees,
            FlipHorizontal = s.Orientation.FlipHorizontal,
            FlipVertical = s.Orientation.FlipVertical,
            Fit = s.Fit,
            Scale = s.Scale,
            OffsetX = s.OffsetX,
            OffsetY = s.OffsetY,
            FolderGrade = image.Settings.IgnoreFolderGrade ? new Grade() : s.FolderGrade.Clone(),
            ImageGrade = image.Settings.Grade.Clone(),
            Negative = s.Mode == ImageMode.Negative,
            NegativeSettings = s.Negative.Clone(),
        };
    }

    private void OnEngineEvent(EngineEvent e)
    {
        switch (e)
        {
            case OutputStarted started:
                _outputRunning = true;
                _outputWidth = started.Width;
                _outputHeight = started.Height;
                _cursorX = started.Width / 2;
                _cursorY = started.Height / 2;
                StartOutputButton.Content = "Stop output";
                StartOutputButton.IsEnabled = !_inRun;
                OutputCombo.IsEnabled = false;
                OutputStatusText.Text = $"{started.Output.DeviceName.Replace(@"\\.\", "")}, {started.Width} × {started.Height}" +
                                        (started.TestWindow ? ", test window" : "");
                RefreshText.Text = "Measuring the refresh rate…";
                ApplyTriggerKeys();
                RefreshStationFields();
                PushCalibration();
                PushSetupFrame();
                Status("Output started.");
                break;

            case OutputStopped stopped:
                _outputRunning = false;
                _measuredHz = 0;
                if (_inRun) SetRunUi(false);
                StartOutputButton.Content = "Start output";
                StartOutputButton.IsEnabled = true;
                OutputCombo.IsEnabled = true;
                OutputStatusText.Text = "Output not started";
                RefreshText.Text = "";
                UpdateStats();
                if (stopped.Error is { } error)
                    MessageBox.Show(this, error, "The output stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    Status("Output stopped.");
                break;

            case RefreshMeasured measured:
                _measuredHz = measured.Hz;
                RefreshText.Text = $"{measured.Hz.ToString("0.000", Invariant)} Hz, one refresh = {(1000 / measured.Hz).ToString("0.00", Invariant)} ms" +
                                   (measured.FromFrameStatistics ? "" : " (estimated; exposures can't be verified)");
                UpdateStats();
                if (Selected is { } image && _job is not null) UpdateTimeInfo(image);
                break;

            case ImageLoadFailed failed:
                Status($"Couldn't load {Path.GetFileName(failed.Path)}: {failed.Message}");
                break;

            case RunPreparing preparing:
                SetRunPhase("PREPARING", preparing.Number, $"Preparing image {preparing.Number}", "Loading and rendering. The screen is black.", false);
                SelectRow(preparing.Index);
                break;

            case RunArmed armed:
                SetRunPhase("ARMED", armed.Number, $"Armed: image {armed.Number}",
                    _job is null ? "" : $"{_job.Images[armed.Index].FileName}, {ExposureMath.FormatSeconds(_job.TimeFor(_job.Images[armed.Index]))}. Waiting for the trigger.", true);
                _serial?.Send(SerialProtocol.Armed(armed.Number));
                break;

            case RunItemFailed itemFailed:
                SetRunPhase("FAILED", itemFailed.Number, $"Image {itemFailed.Number} didn't load",
                    $"{itemFailed.Message} Fix the file and choose Reload images, or go to another image.", false);
                _serial?.Send(SerialProtocol.Error($"image {itemFailed.Number} failed to load"));
                AddLog($"Image {itemFailed.Number} failed to load: {itemFailed.Message}");
                break;

            case TriggerRejected rejected:
                AddLog($"Trigger from {rejected.Source.ToString().ToLowerInvariant()} ignored: {rejected.Reason}.");
                if (rejected.Source == TriggerSource.Serial) _serial?.Send(SerialProtocol.Error(rejected.Reason));
                break;

            case ExposureStarted exposing:
                SetRunPhase("EXPOSING", exposing.Number, $"Exposing image {exposing.Number}", "", true);
                _serial?.Send(SerialProtocol.On(exposing.Number));
                break;

            case ExposureCompleted done:
                OnExposureCompleted(done);
                break;

            case RunFinished:
                SetRunPhase("DONE", _runNumber, "Every image has been shown", "Exit run mode to make changes, or start again from another image.", false);
                _serial?.Send(SerialProtocol.Done());
                AddLog("Run complete.");
                _runLog?.Note("Run complete.");
                break;

            case RunEnded:
                SetRunUi(false);
                AddLog("Left run mode.");
                _runLog?.Note("Run mode ended.");
                _runLog?.Dispose();
                _runLog = null;
                PushSetupFrame();
                break;

            case StatusMessage message:
                Status(message.Text);
                if (_inRun) AddLog(message.Text);
                break;
        }
    }

    // ---- Run mode -------------------------------------------------------------------------------

    private void EnterRun_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedField();
        if (_job is null || _job.Images.Count == 0)
        {
            Status("Open a job with images first.");
            return;
        }
        if (!_outputRunning)
        {
            Status("Start the output first.");
            return;
        }
        if (!int.TryParse(StartAtBox.Text, NumberStyles.Integer, Invariant, out int startAt) || startAt < 1 || startAt > _job.Images.Count)
        {
            Status($"Start at an image between 1 and {_job.Images.Count}.");
            return;
        }
        if (_measuredHz <= 1 && MessageBox.Show(this,
                "The refresh rate hasn't been measured yet, so FilmOut will assume 60 Hz and exposure times may be slightly off. Enter run mode anyway?",
                "Refresh rate unknown", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SaveJob();
        ClearOverlays();
        var items = _job.Images
            .Select(image => new RunItem(image.Position, image.FileName, ImageParameters(_job, image), _job.TimeFor(image)))
            .ToList();

        _runLog?.Dispose();
        try
        {
            _runLog = new RunLog(_job.Folder);
            _runLog.Note($"FilmOut run of {_job.Folder}, starting at image {startAt}.");
            _runLog.Note($"Output {OutputStatusText.Text}, {Hz.ToString("0.0000", Invariant)} Hz, {PanelAdvice.Describe(_station.Panel)}, " +
                         $"{_job.Settings.Mode.ToString().ToLowerInvariant()} mode, gate {EffectiveGate()}, " +
                         $"flat field {(_station.FlatFieldEnabled && _flatMap is not null ? "on" : "off")}, {(_station.DefectsEnabled ? _station.Defects.Count : 0)} defects compensated.");
            RunLogPathText.Text = $"Logging every exposure to {_runLog.Path}";
        }
        catch (Exception ex)
        {
            _runLog = null;
            RunLogPathText.Text = $"Couldn't create the run log: {ex.Message}";
        }

        ApplyTriggerKeys();
        SetRunUi(true);
        _engine.BeginRun(items, startAt - 1);
        AddLog($"Entered run mode at image {startAt}; {items.Count - startAt + 1} images to go.");
    }

    private void ExitRun_Click(object sender, RoutedEventArgs e) => _engine.EndRun();

    private void Fire_Click(object sender, RoutedEventArgs e) => _engine.Trigger(TriggerSource.Ui);

    private void Abort_Click(object sender, RoutedEventArgs e) => _engine.Abort();

    private void OnExposureCompleted(ExposureCompleted done)
    {
        string result = $"Image {done.Number} ({done.FileName}): {done.MeasuredFrames} refreshes, {done.MeasuredSeconds.ToString("0.0000", Invariant)} s";
        if (done.Aborted) result += ", cut short; the same image is armed again";
        else if (done.MeasuredFrames != done.RequestedFrames) result += $", but {done.RequestedFrames} were requested. Check this frame";
        if (!done.Verified) result += " (not confirmed by the display)";
        AddLog(result + ".");

        _runLog?.Exposure(done.Time, done.Number, done.FileName, done.RequestedSeconds, done.RequestedFrames,
            done.MeasuredFrames, done.MeasuredSeconds, done.RefreshHz, done.Verified, done.Aborted,
            done.Source.ToString().ToLowerInvariant());
        _serial?.Send(SerialProtocol.Off(done.Number, done.MeasuredSeconds, done.MeasuredFrames, done.Verified));
    }

    private void SetRunUi(bool running)
    {
        _inRun = running;
        ImageList.IsEnabled = !running;
        OpenFolderButton.IsEnabled = ReloadButton.IsEnabled = !running;
        StartOutputButton.IsEnabled = !running;
        EnterRunButton.IsEnabled = !running;
        StartAtBox.IsEnabled = !running;
        ExitRunButton.IsEnabled = FireButton.IsEnabled = AbortButton.IsEnabled = running;
        ImagePanel.IsEnabled = !running && Selected is not null;
        FolderPanel.IsEnabled = !running && _job is not null;
        StationPanel.IsEnabled = !running;
        TriggerKeysBox.IsEnabled = AbortKeyBox.IsEnabled = !running;

        if (running)
        {
            Tabs.SelectedItem = RunTab;
        }
        else
        {
            SetRunPhase("IDLE", 0, "Not running",
                "In run mode the screen stays black until a trigger arrives, shows the image for its time, then goes black and loads the next one.", false);
        }
    }

    private void SetRunPhase(string word, int number, string title, string detail, bool lit)
    {
        _runPhaseWord = word;
        _runNumber = number;
        RunPhaseText.Text = title;
        RunDetailText.Text = detail;
        if (lit)
        {
            RunPhaseText.Foreground = (System.Windows.Media.Brush)FindResource("SafelightBrush");
            RunStateBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("SafelightBrush");
        }
        else
        {
            RunPhaseText.ClearValue(TextBlock.ForegroundProperty);
            RunStateBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("RuleBrush");
        }
    }

    private void SelectRow(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        ImageList.SelectedIndex = index;
        ImageList.ScrollIntoView(_rows[index]);
    }

    private void AddLog(string text)
    {
        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (LogList.Items.Count > 500) LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    // ---- Trigger keys ---------------------------------------------------------------------------

    private void RefreshKeyFields()
    {
        using var _ = Suppress();
        TriggerKeysBox.Text = string.Join(", ", _station.TriggerKeys);
        AbortKeyBox.Text = _station.AbortKey;
    }

    private void Keys_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitKeys();
    }

    private void Keys_LostFocus(object sender, RoutedEventArgs e) => CommitKeys();

    private void CommitKeys()
    {
        if (_suppress) return;
        var names = TriggerKeysBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var unknown = names.Concat([AbortKeyBox.Text.Trim()]).Where(n => ToVirtualKey(n) is null).ToList();
        if (unknown.Count > 0 || names.Count == 0)
        {
            Status(names.Count == 0
                ? "Enter at least one trigger key."
                : $"Unknown key name: {string.Join(", ", unknown)}. Use names such as Space, Enter, PageDown or F13.");
            RefreshKeyFields();
            return;
        }
        _station.TriggerKeys = names;
        _station.AbortKey = AbortKeyBox.Text.Trim();
        StationChanged();
        ApplyTriggerKeys();
        RefreshKeyFields();
    }

    private void ApplyTriggerKeys()
    {
        var triggers = _station.TriggerKeys.Select(ToVirtualKey).OfType<int>().ToList();
        _engine.SetTriggerKeys(triggers, ToVirtualKey(_station.AbortKey) ?? 0x1B);
    }

    private static int? ToVirtualKey(string name) =>
        Enum.TryParse(name.Trim(), ignoreCase: true, out Key key) && key != Key.None ? KeyInterop.VirtualKeyFromKey(key) : null;

    // ---- Serial ---------------------------------------------------------------------------------

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames().OrderBy(p => p, NaturalSortComparer.Instance).ToList();
        SerialPortCombo.ItemsSource = ports;
        SerialPortCombo.SelectedItem = ports.FirstOrDefault(p => p == _station.Serial.PortName) ?? ports.FirstOrDefault();
        using var _ = Suppress();
        Select(BaudCombo, _station.Serial.BaudRate);
        if (BaudCombo.SelectedItem is null) Select(BaudCombo, 115200);
        SerialAutoBox.IsChecked = _station.Serial.ConnectOnStart;
    }

    private void SerialConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_serial is not null) DisconnectSerial();
        else ConnectSerial();
    }

    private void ConnectSerial()
    {
        string? port = SerialPortCombo.SelectedItem as string ?? _station.Serial.PortName;
        if (port is null)
        {
            SerialStatusText.Text = "Choose a serial port.";
            return;
        }
        int baud = TryGet(BaudCombo, out int b) ? b : _station.Serial.BaudRate;
        try
        {
            var serial = new SerialTrigger(port, baud);
            serial.CommandReceived += OnSerialCommand;
            serial.Failed += message => Dispatcher.InvokeAsync(() => AddLog($"Serial: {message}"));
            _serial = serial;
            _station.Serial.PortName = port;
            _station.Serial.BaudRate = baud;
            StationChanged();
            SerialStatusText.Text = $"Connected to {port} at {baud} baud.";
            SerialConnectButton.Content = "Disconnect";
            SerialPortCombo.IsEnabled = BaudCombo.IsEnabled = false;
        }
        catch (Exception ex)
        {
            SerialStatusText.Text = $"Couldn't open {port}: {ex.Message}";
        }
    }

    private void DisconnectSerial()
    {
        _serial?.Dispose();
        _serial = null;
        SerialStatusText.Text = "Not connected";
        SerialConnectButton.Content = "Connect";
        SerialPortCombo.IsEnabled = BaudCombo.IsEnabled = true;
    }

    private void SerialAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _station.Serial.ConnectOnStart = SerialAutoBox.IsChecked == true;
        StationChanged();
    }

    /// <summary>Runs on the serial port's thread. Triggers go straight to the output engine.</summary>
    private void OnSerialCommand(SerialCommand command)
    {
        switch (command.Kind)
        {
            case SerialCommandKind.Next:
                _engine.Trigger(TriggerSource.Serial);
                break;
            case SerialCommandKind.Black:
                _engine.Abort();
                break;
            default:
                Dispatcher.InvokeAsync(() => HandleSerialCommand(command));
                break;
        }
    }

    private void HandleSerialCommand(SerialCommand command)
    {
        if (_serial is not { } serial) return;
        switch (command.Kind)
        {
            case SerialCommandKind.Goto when !_inRun || _job is null:
                serial.Send(SerialProtocol.Error("not in run mode"));
                break;
            case SerialCommandKind.Goto when command.Argument < 1 || command.Argument > _job!.Images.Count:
                serial.Send(SerialProtocol.Error($"there is no image {command.Argument}"));
                break;
            case SerialCommandKind.Goto:
                _engine.Goto(command.Argument - 1);
                AddLog($"Serial: go to image {command.Argument}.");
                break;
            case SerialCommandKind.Status:
                serial.Send(SerialProtocol.Status(_runPhaseWord, _runNumber));
                break;
            case SerialCommandKind.Ping:
                serial.Send(SerialProtocol.Pong());
                break;
            default:
                serial.Send(SerialProtocol.Error($"unknown command: {command.Raw}"));
                AddLog($"Serial: unknown command “{command.Raw}”.");
                break;
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private void Status(string text) => StatusText.Text = text;

    private static bool TryParseNumber(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, Invariant, out value)
         || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        && double.IsFinite(value);

    private static void Fill<T>(ComboBox combo, params (T Value, string Label)[] items)
    {
        combo.Items.Clear();
        foreach (var (value, label) in items) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
    }

    private static void Select<T>(ComboBox combo, T value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));

    private static bool TryGet<T>(ComboBox combo, out T value)
    {
        if (combo.SelectedItem is ComboBoxItem { Tag: T v })
        {
            value = v;
            return true;
        }
        value = default!;
        return false;
    }

    private SuppressScope Suppress() => new(this);

    /// <summary>Stops change handlers from reacting while fields are filled in from code.</summary>
    private sealed class SuppressScope : IDisposable
    {
        private readonly MainWindow _window;
        private readonly bool _previous;

        public SuppressScope(MainWindow window)
        {
            _window = window;
            _previous = window._suppress;
            window._suppress = true;
        }

        public void Dispose() => _window._suppress = _previous;
    }
}
