namespace FilmOut.Rendering;

public sealed record OutputInfo(int AdapterIndex, string AdapterName, int OutputIndex, string DeviceName, int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{DeviceName.Replace(@"\\.\", "")}   {Width} × {Height}   {AdapterName}";
}

public enum TriggerSource { Keyboard, Serial, Ui }

public enum RunPhase { Idle, Preparing, Armed, Exposing, Settling, Finished, Failed }

public sealed record RunSnapshot(RunPhase Phase, int Index, int Number);

public abstract record EngineEvent;

public sealed record OutputStarted(OutputInfo Output, int Width, int Height, bool TestWindow) : EngineEvent;

/// <summary>The output closed. Error is null when it was stopped on purpose.</summary>
public sealed record OutputStopped(string? Error) : EngineEvent;

public sealed record RefreshMeasured(double Hz, bool FromFrameStatistics) : EngineEvent;

public sealed record SetupFrameReady(string? ImagePath) : EngineEvent;

public sealed record ImageLoadFailed(string Path, string Message) : EngineEvent;

public sealed record RunPreparing(int Index, int Number) : EngineEvent;

public sealed record RunArmed(int Index, int Number) : EngineEvent;

public sealed record RunItemFailed(int Index, int Number, string Message) : EngineEvent;

public sealed record TriggerRejected(TriggerSource Source, string Reason) : EngineEvent;

/// <summary>The first refresh of the image reached the screen (confirmed from frame statistics when available).</summary>
public sealed record ExposureStarted(int Index, int Number, TriggerSource Source) : EngineEvent;

public sealed record ExposureCompleted(
    int Index,
    int Number,
    string FileName,
    double RequestedSeconds,
    int RequestedFrames,
    int MeasuredFrames,
    double MeasuredSeconds,
    double RefreshHz,
    bool Verified,
    bool Aborted,
    TriggerSource Source,
    DateTime Time) : EngineEvent;

public sealed record RunFinished : EngineEvent;

public sealed record RunEnded : EngineEvent;

public sealed record StatusMessage(string Text) : EngineEvent;
