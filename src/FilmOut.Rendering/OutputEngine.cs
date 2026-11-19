using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using FilmOut.Core;
using Vortice.DXGI;

namespace FilmOut.Rendering;

/// <summary>
/// Drives the output display from a dedicated, high-priority render thread.
///
/// The thread presents a frame on every refresh. In setup mode that's the current image (or a
/// test pattern), re-rendered whenever something changes. In run mode the screen is black
/// until a trigger arrives; the image is then presented for exactly N refreshes, where N is the
/// display time rounded to whole refreshes, and the screen goes black again. DXGI frame
/// statistics report the refresh on which each present reached the screen, which gives the
/// measured exposure length in refreshes.
///
/// All public methods are thread-safe. Events are raised on a background thread.
/// </summary>
public sealed class OutputEngine : IDisposable
{
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Channel<EngineEvent> _events = Channel.CreateUnbounded<EngineEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly DecodeCache _decodes = new();
    private readonly RefreshMeter _meter = new();

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile KeyBindings _keys = new(new HashSet<int> { 0x20 }, 0x1B);
    private double _publishedHz;
    private RunSnapshot _snapshot = new(RunPhase.Idle, -1, 0);

    // Render-thread state.
    private Renderer? _renderer;
    private OutputWindow? _window;
    private OutputCalibration _calibration = new(null, 2.2, 10, null, []);
    private GateRect _gate = new(0, 0, 1, 1);
    private FrameParameters? _setup;
    private bool _setupDirty;
    private bool _preparedValid;
    private string _lastLoadError = "";
    private uint _seed;
    private RunState? _run;
    private int _framesSinceHzEvent;
    private double _lastRaisedHz;

    public OutputEngine() => _ = Task.Run(DispatchEventsAsync);

    public event Action<EngineEvent>? EventRaised;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>The measured refresh rate, or 0 until it has been measured.</summary>
    public double MeasuredRefreshHz => Volatile.Read(ref _publishedHz);

    public RunSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public static IReadOnlyList<OutputInfo> EnumerateOutputs()
    {
        var outputs = new List<OutputInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                var ad = adapter!.Description1;
                if ((ad.Flags & AdapterFlags.Software) != 0) continue;
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        var od = output!.Description;
                        var r = od.DesktopCoordinates;
                        outputs.Add(new OutputInfo((int)a, ad.Description, (int)o, od.DeviceName,
                            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                    }
                }
            }
        }
        return outputs;
    }

    /// <param name="testWindow">Open a half-size, normal window instead of covering the display (for single-monitor testing).</param>
    public void Start(OutputInfo output, OutputCalibration calibration, bool testWindow)
    {
        if (IsRunning) throw new InvalidOperationException("The output is already running.");
        while (_commands.TryDequeue(out _)) { }
        _stopRequested = false;
        _thread = new Thread(() => RenderThread(output, calibration, testWindow))
        {
            Name = "FilmOut output",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _events.Writer.TryComplete();
    }

    public void UpdateCalibration(OutputCalibration calibration) => Post(() => ApplyCalibration(calibration));

    public void ShowSetup(FrameParameters parameters) => Post(() =>
    {
        _setup = parameters;
        _setupDirty = true;
    });

    /// <summary>Decode these images in the background and keep them ready (e.g. the neighbours of the selection).</summary>
    public void Prefetch(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        Post(() => _decodes.SetRetained(list));
    }

    /// <summary>Discard decoded images so changed files on disk are read again.</summary>
    public void ReloadImages() => Post(() =>
    {
        _decodes.Clear();
        _renderer?.ForgetSource();
        _setupDirty = true;
        if (_run is { Phase: RunPhase.Armed or RunPhase.Failed } run) SetPhase(run, RunPhase.Preparing);
    });

    public void SetTriggerKeys(IEnumerable<int> triggerVirtualKeys, int abortVirtualKey) =>
        _keys = new KeyBindings(triggerVirtualKeys.ToHashSet(), abortVirtualKey);

    public void BeginRun(IReadOnlyList<RunItem> items, int startIndex) => Post(() => StartRun(items, startIndex));

    public void EndRun() => Post(RequestEndRun);

    public void Trigger(TriggerSource source) => Post(() => HandleTrigger(source));

    public void Abort() => Post(() => AbortExposure("abort requested"));

    public void Goto(int index) => Post(() => GotoItem(index));

    private void Post(Action command) => _commands.Enqueue(command);

    private void Raise(EngineEvent e) => _events.Writer.TryWrite(e);

    private async Task DispatchEventsAsync()
    {
        await foreach (var e in _events.Reader.ReadAllAsync())
        {
            try { EventRaised?.Invoke(e); }
            catch { /* a failing handler mustn't stop event delivery */ }
        }
    }

    // ---- Render thread --------------------------------------------------------------------

    private void RenderThread(OutputInfo output, OutputCalibration calibration, bool testWindow)
    {
        IntPtr mmcss = IntPtr.Zero;
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED);
        try
        {
            uint taskIndex = 0;
            mmcss = Native.AvSetMmThreadCharacteristicsW("Games", ref taskIndex);

            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            if (factory.EnumAdapters1((uint)output.AdapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
                throw new InvalidOperationException("The graphics adapter for this display is no longer available.");

            using (adapter)
            {
                int x = output.X, y = output.Y, w = output.Width, h = output.Height;
                if (testWindow)
                {
                    w /= 2;
                    h /= 2;
                    x += w / 2;
                    y += h / 2;
                }

                using var window = new OutputWindow(x, y, w, h, topmost: !testWindow);
                using var renderer = new Renderer(factory, adapter, window.Handle, w, h);
                _window = window;
                _renderer = renderer;
                window.KeyPressed += OnKeyPressed;
                _meter.Reset();
                ApplyCalibration(calibration);
                Raise(new OutputStarted(output, w, h, testWindow));

                while (!_stopRequested)
                {
                    renderer.WaitForFrame(250);
                    window.PumpMessages();
                    while (_commands.TryDequeue(out var command))
                    {
                        try { command(); }
                        catch (Exception ex) { Raise(new StatusMessage($"Output: {ex.Message}")); }
                    }
                    Frame();
                }
            }
            Raise(new OutputStopped(null));
        }
        catch (Exception ex)
        {
            Raise(new OutputStopped(ex.Message));
        }
        finally
        {
            _renderer = null;
            _window = null;
            _run = null;
            _setup = null;
            _preparedValid = false;
            _decodes.Clear();
            Volatile.Write(ref _publishedHz, 0);
            Publish(RunPhase.Idle, -1, 0);
            if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        }
    }

    private double RefreshHz => _meter.Hz > 1 ? _meter.Hz : 60.0;

    private void Frame()
    {
        var r = _renderer!;
        bool showImage = _run is not null ? RunFrame(_run) : SetupFrame();
        if (showImage) r.ShowPrepared();
        else r.ShowBlack();
        r.Present();
        _meter.CountFrame();

        uint presentId = r.LastPresentCount;
        if (_run is { } run)
        {
            if (run.MarkStart) { run.StartPresentId = presentId; run.MarkStart = false; }
            if (run.MarkEnd)
            {
                run.EndPresentId = presentId;
                run.MarkEnd = false;
                run.SettleDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency; // 1 s to confirm
            }
        }

        // Without frame statistics there's nothing to wait for: report the presents we counted.
        if (_run is { Phase: RunPhase.Settling, EndPresentId: not 0 } unconfirmed && !_meter.FromStatistics)
        {
            CompleteExposure(unconfirmed.FramesShown, verified: false);
            return;
        }

        PollStatistics();

        if (_run is { Phase: RunPhase.Settling } settling && Stopwatch.GetTimestamp() > settling.SettleDeadline)
            CompleteExposure(settling.FramesShown, verified: false); // statistics never confirmed it

        if (++_framesSinceHzEvent >= 60)
        {
            _framesSinceHzEvent = 0;
            double hz = _meter.Hz;
            if (hz > 1)
            {
                Volatile.Write(ref _publishedHz, hz);
                if (Math.Abs(hz - _lastRaisedHz) > 0.0005)
                {
                    _lastRaisedHz = hz;
                    Raise(new RefreshMeasured(hz, _meter.FromStatistics));
                }
            }
        }
    }

    private bool SetupFrame()
    {
        if (_setupDirty && _setup is { } p)
        {
            switch (p.Content)
            {
                case OutputContent.Black:
                    _preparedValid = false;
                    _setupDirty = false;
                    break;
                case OutputContent.Image when p.ImagePath is null:
                    _preparedValid = false;
                    _setupDirty = false;
                    break;
                case OutputContent.Image:
                    switch (EnsureSource(p.ImagePath!))
                    {
                        case SourceStatus.Ready:
                            Render(p);
                            _preparedValid = true;
                            _setupDirty = false;
                            Raise(new SetupFrameReady(p.ImagePath));
                            break;
                        case SourceStatus.Failed:
                            _preparedValid = false;
                            _setupDirty = false;
                            Raise(new ImageLoadFailed(p.ImagePath, _lastLoadError));
                            break;
                        // Still decoding: keep showing the previous frame.
                    }
                    break;
                default:
                    Render(p);
                    _preparedValid = true;
                    _setupDirty = false;
                    break;
            }
        }
        return _preparedValid;
    }

    private bool RunFrame(RunState run)
    {
        switch (run.Phase)
        {
            case RunPhase.Preparing:
                Prepare(run);
                return false;
            case RunPhase.Exposing:
                if (run.FramesLeft > 0)
                {
                    if (run.FramesShown == 0) run.MarkStart = true;
                    run.FramesLeft--;
                    run.FramesShown++;
                    return true;
                }
                SetPhase(run, RunPhase.Settling);
                run.MarkEnd = true;
                return false;
            default:
                return false;
        }
    }

    private void Prepare(RunState run)
    {
        var item = run.Items[run.Index];
        string path = item.Parameters.ImagePath!;
        switch (EnsureSource(path))
        {
            case SourceStatus.Ready:
                Render(item.Parameters);
                SetPhase(run, RunPhase.Armed);
                Raise(new RunArmed(run.Index, item.Number));
                var keep = new List<string> { path };
                if (run.Index + 1 < run.Items.Count) keep.Add(run.Items[run.Index + 1].Parameters.ImagePath!);
                _decodes.SetRetained(keep);
                break;
            case SourceStatus.Failed:
                SetPhase(run, RunPhase.Failed);
                Raise(new RunItemFailed(run.Index, item.Number, _lastLoadError));
                break;
        }
    }

    private void HandleTrigger(TriggerSource source)
    {
        if (_run is not { } run)
        {
            if (source != TriggerSource.Keyboard) Raise(new TriggerRejected(source, "not in run mode"));
            return;
        }
        if (run.Phase != RunPhase.Armed)
        {
            Raise(new TriggerRejected(source, run.Phase switch
            {
                RunPhase.Preparing => "still preparing the next image",
                RunPhase.Exposing or RunPhase.Settling => "an exposure is in progress",
                RunPhase.Finished => "every image has been shown",
                RunPhase.Failed => "the current image couldn't be loaded",
                _ => "not armed",
            }));
            return;
        }

        var item = run.Items[run.Index];
        run.FramesTotal = ExposureMath.ToFrames(item.Seconds, RefreshHz);
        run.FramesLeft = run.FramesTotal;
        run.FramesShown = 0;
        run.Source = source;
        run.Aborted = false;
        run.StartPresentId = run.EndPresentId = 0;
        run.StartRefresh = null;
        run.StartExact = false;
        run.StartReported = false;
        SetPhase(run, RunPhase.Exposing);
    }

    private void AbortExposure(string reason)
    {
        if (_run is not { Phase: RunPhase.Exposing } run) return;
        run.Aborted = true;
        run.FramesLeft = 0;
        Raise(new StatusMessage($"Exposure of image {run.Items[run.Index].Number} cut short ({reason})."));
    }

    private void OnKeyPressed(int vk)
    {
        var keys = _keys;
        if (vk == keys.Abort) AbortExposure("abort key");
        else if (keys.Triggers.Contains(vk)) HandleTrigger(TriggerSource.Keyboard);
    }

    private void PollStatistics()
    {
        if (!_renderer!.TryGetFrameStatistics(out var s) || s.SyncQPCTime == 0) return;
        _meter.AddStatistics(s.SyncRefreshCount, s.SyncQPCTime);

        if (_run is not { } run) return;
        var item = run.Items[run.Index];

        if (run.StartPresentId != 0 && run.StartRefresh is null && s.PresentCount >= run.StartPresentId)
        {
            // Exact when this sample is for the first image present itself; otherwise inferred.
            run.StartRefresh = s.PresentRefreshCount - (s.PresentCount - run.StartPresentId);
            run.StartExact = s.PresentCount == run.StartPresentId;
            run.StartReported = true;
            Raise(new ExposureStarted(run.Index, item.Number, run.Source));
        }

        if (run.Phase == RunPhase.Settling && run.EndPresentId != 0 && run.StartRefresh is uint start
            && s.PresentCount >= run.EndPresentId)
        {
            uint end = s.PresentRefreshCount - (s.PresentCount - run.EndPresentId);
            CompleteExposure((int)(end - start), run.StartExact && s.PresentCount == run.EndPresentId);
        }
    }

    private void CompleteExposure(int measuredFrames, bool verified)
    {
        var run = _run!;
        var item = run.Items[run.Index];
        if (!run.StartReported) Raise(new ExposureStarted(run.Index, item.Number, run.Source));

        double hz = RefreshHz;
        Raise(new ExposureCompleted(run.Index, item.Number, item.FileName, item.Seconds, run.FramesTotal,
            measuredFrames, measuredFrames / hz, hz, verified, run.Aborted, run.Source, DateTime.Now));

        bool aborted = run.Aborted;
        run.StartPresentId = run.EndPresentId = 0;
        run.FramesLeft = run.FramesShown = 0;

        if (run.EndRequested)
        {
            EndRunNow();
            return;
        }
        if (run.PendingGoto is int target)
        {
            run.PendingGoto = null;
            run.Index = target;
        }
        else if (!aborted)
        {
            run.Index++; // an aborted exposure re-arms the same image
        }

        if (run.Index >= run.Items.Count)
        {
            run.Index = run.Items.Count - 1;
            SetPhase(run, RunPhase.Finished);
            Raise(new RunFinished());
        }
        else
        {
            SetPhase(run, RunPhase.Preparing);
            Raise(new RunPreparing(run.Index, run.Items[run.Index].Number));
        }
    }

    private void StartRun(IReadOnlyList<RunItem> items, int startIndex)
    {
        if (items.Count == 0)
        {
            Raise(new StatusMessage("There are no images to show."));
            return;
        }
        var run = new RunState(items) { Index = Math.Clamp(startIndex, 0, items.Count - 1) };
        _run = run;
        _preparedValid = false;
        SetPhase(run, RunPhase.Preparing);
        Raise(new RunPreparing(run.Index, items[run.Index].Number));
    }

    private void GotoItem(int index)
    {
        if (_run is not { } run || index < 0 || index >= run.Items.Count) return;
        if (run.Phase is RunPhase.Exposing or RunPhase.Settling)
        {
            run.PendingGoto = index;
            AbortExposure("moved to another image");
            return;
        }
        run.Index = index;
        SetPhase(run, RunPhase.Preparing);
        Raise(new RunPreparing(index, run.Items[index].Number));
    }

    private void RequestEndRun()
    {
        if (_run is not { } run) return;
        if (run.Phase is RunPhase.Exposing or RunPhase.Settling)
        {
            run.EndRequested = true;
            AbortExposure("run ended");
            return;
        }
        EndRunNow();
    }

    private void EndRunNow()
    {
        _run = null;
        _preparedValid = false;
        _setupDirty = true;
        Publish(RunPhase.Idle, -1, 0);
        Raise(new RunEnded());
    }

    private void ApplyCalibration(OutputCalibration calibration)
    {
        var r = _renderer!;
        _calibration = calibration;
        _gate = (calibration.Gate ?? new GateRect(0, 0, r.Width, r.Height)).ClampTo(r.Width, r.Height);

        var flat = calibration.FlatField;
        if (flat is not null && flat.Gate != _gate)
        {
            flat = null;
            Raise(new StatusMessage("The flat-field map was built for a different gate, so it's off. Rebuild it for the current gate."));
        }
        r.UploadFlatField(flat);
        r.UploadDefects(calibration.Defects);

        _setupDirty = true;
        if (_run is { Phase: RunPhase.Armed } run) SetPhase(run, RunPhase.Preparing);
    }

    private void Render(FrameParameters p)
    {
        var r = _renderer!;
        var constants = ConstantsBuilder.Build(p, _gate, _calibration, r.SourceWidth, r.SourceHeight,
            ++_seed, r.HasFlatField, r.HasDefects);
        r.RenderPrepared(constants);
    }

    private enum SourceStatus { Loading, Ready, Failed }

    private SourceStatus EnsureSource(string path)
    {
        var r = _renderer!;
        if (string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase)) return SourceStatus.Ready;

        var task = _decodes.Request(path);
        if (!task.IsCompleted) return SourceStatus.Loading;
        if (!task.IsCompletedSuccessfully)
        {
            _lastLoadError = task.Exception?.GetBaseException().Message ?? "the decoder was cancelled";
            _decodes.Forget(path);
            return SourceStatus.Failed;
        }

        r.UploadSource(task.Result);
        _decodes.Uploaded(path);
        return SourceStatus.Ready;
    }

    private void SetPhase(RunState run, RunPhase phase)
    {
        run.Phase = phase;
        Publish(phase, run.Index, run.Items[run.Index].Number);
    }

    private void Publish(RunPhase phase, int index, int number) =>
        Volatile.Write(ref _snapshot, new RunSnapshot(phase, index, number));

    // ---- Helpers ----------------------------------------------------------------------------

    private sealed record KeyBindings(HashSet<int> Triggers, int Abort);

    private sealed class RunState(IReadOnlyList<RunItem> items)
    {
        public IReadOnlyList<RunItem> Items { get; } = items;
        public int Index;
        public RunPhase Phase;
        public int FramesTotal, FramesLeft, FramesShown;
        public TriggerSource Source;
        public bool Aborted, EndRequested;
        public int? PendingGoto;
        public bool MarkStart, MarkEnd;
        public uint StartPresentId, EndPresentId;
        public uint? StartRefresh;
        public bool StartExact, StartReported;
        public long SettleDeadline;
    }

    /// <summary>Decoded images, kept for the current and neighbouring images and decoded in the background.</summary>
    private sealed class DecodeCache
    {
        private readonly Dictionary<string, Task<DecodedImage>> _tasks = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _retained = new(StringComparer.OrdinalIgnoreCase);
        private string? _lastRequested;

        public Task<DecodedImage> Request(string path)
        {
            _lastRequested = path;
            if (!_tasks.TryGetValue(path, out var task))
            {
                task = Task.Run(() => ImageDecoder.Decode(path));
                _tasks[path] = task;
            }
            return task;
        }

        public void SetRetained(IEnumerable<string> paths)
        {
            _retained = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var path in _retained)
                if (!_tasks.ContainsKey(path)) _tasks[path] = Task.Run(() => ImageDecoder.Decode(path));
            Trim();
        }

        /// <summary>The image is on the GPU now; keep the CPU copy only if it's still wanted nearby.</summary>
        public void Uploaded(string path)
        {
            if (!_retained.Contains(path)) _tasks.Remove(path);
            Trim();
        }

        public void Forget(string path) => _tasks.Remove(path);

        public void Clear()
        {
            _tasks.Clear();
            _retained.Clear();
            _lastRequested = null;
        }

        private void Trim()
        {
            foreach (var key in _tasks.Keys.ToList())
            {
                bool wanted = _retained.Contains(key) || string.Equals(key, _lastRequested, StringComparison.OrdinalIgnoreCase);
                if (!wanted && _tasks[key].IsCompleted) _tasks.Remove(key);
            }
        }
    }

    /// <summary>
    /// Measures the refresh rate from DXGI's vblank counter and timestamps. If frame statistics
    /// aren't available, falls back to timing the render loop, which the waitable swap chain paces
    /// at one iteration per refresh.
    /// </summary>
    private sealed class RefreshMeter
    {
        private bool _hasBase;
        private uint _baseRefresh;
        private long _baseQpc;
        private double _statisticsHz;
        private long _lastStatisticsQpc;
        private long _loopStart;
        private long _loopFrames;

        public double Hz => StatisticsCurrent ? _statisticsHz : LoopHz;
        public bool FromStatistics => StatisticsCurrent;

        private bool StatisticsCurrent =>
            _statisticsHz > 1 && Stopwatch.GetTimestamp() - _lastStatisticsQpc < Stopwatch.Frequency * 2;

        private double LoopHz
        {
            get
            {
                double seconds = (Stopwatch.GetTimestamp() - _loopStart) / (double)Stopwatch.Frequency;
                return _loopFrames > 120 && seconds > 1 ? _loopFrames / seconds : 0;
            }
        }

        public void Reset()
        {
            _hasBase = false;
            _statisticsHz = 0;
            _loopStart = Stopwatch.GetTimestamp();
            _loopFrames = 0;
        }

        public void CountFrame()
        {
            if (_loopFrames == 0) _loopStart = Stopwatch.GetTimestamp();
            _loopFrames++;
        }

        public void AddStatistics(uint syncRefreshCount, long syncQpc)
        {
            _lastStatisticsQpc = Stopwatch.GetTimestamp();
            if (!_hasBase || syncRefreshCount < _baseRefresh || syncQpc <= _baseQpc)
            {
                _hasBase = true;
                _baseRefresh = syncRefreshCount;
                _baseQpc = syncQpc;
                return;
            }
            double seconds = (syncQpc - _baseQpc) / (double)Stopwatch.Frequency;
            if (seconds >= 1.0) _statisticsHz = (syncRefreshCount - _baseRefresh) / seconds;
        }
    }
}
