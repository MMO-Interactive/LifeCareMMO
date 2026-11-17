using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// Opt-in live frame profiler. Set MMOI_STORM_CHASE_PROFILE=1 to log, once per second,
/// how many milliseconds each UI-thread and render-thread phase consumed, plus UI and
/// completed render frame rates. MMOI_STORM_CHASE_PROFILE_SECONDS=N ends the session
/// after N seconds (for unattended benchmarks). Output: %LOCALAPPDATA%\MMOI\StormChase\Logs\profile-*.jsonl
/// </summary>
internal sealed class StormChaseLiveProfiler : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, double> totals = new(StringComparer.Ordinal);
    private readonly StreamWriter writer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double windowStart;
    private long uiFrames, lastCompleted;
    private long phaseStarted;
    private readonly List<double> uiFrameMs = [];

    public string Path { get; }
    public double AutoExitSeconds { get; }
    public bool ShouldExit => AutoExitSeconds > 0 && clock.Elapsed.TotalSeconds >= AutoExitSeconds;

    private StormChaseLiveProfiler(string path, double autoExitSeconds)
    {
        Path = path; AutoExitSeconds = autoExitSeconds;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        writer = new StreamWriter(path, false) { AutoFlush = true };
        Console.WriteLine("Live profiler writing " + path);
    }

    public static StormChaseLiveProfiler? FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_PROFILE") != "1") return null;
        double.TryParse(Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_PROFILE_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds);
        string directory = System.IO.Path.Combine(ChaseClientPreferences.DataRoot, "Logs");
        return new(System.IO.Path.Combine(directory, $"profile-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"), seconds);
    }

    /// <summary>Thread-safe accumulation, usable directly as an engine/session timing sink.</summary>
    public void Add(string phase, double milliseconds)
    {
        lock (gate) totals[phase] = totals.GetValueOrDefault(phase) + milliseconds;
    }

    public Action<string, double> Prefixed(string prefix) => (phase, ms) => Add(prefix + phase, ms);

    public void BeginUiFrame() => phaseStarted = Stopwatch.GetTimestamp();

    /// <summary>Closes the current UI phase and starts the next one.</summary>
    public void Mark(string phase)
    {
        long now = Stopwatch.GetTimestamp();
        Add("Ui." + phase, Stopwatch.GetElapsedTime(phaseStarted, now).TotalMilliseconds);
        phaseStarted = now;
    }

    public void EndUiFrame(double frameMilliseconds, long completedRenderFrames, double gpuMilliseconds, string context)
    {
        uiFrames++; uiFrameMs.Add(frameMilliseconds);
        double now = clock.Elapsed.TotalSeconds, span = now - windowStart;
        if (span < 1) return;
        Dictionary<string, double> snapshot;
        lock (gate) { snapshot = totals.ToDictionary(p => p.Key, p => Math.Round(p.Value / span, 2)); totals.Clear(); }
        uiFrameMs.Sort();
        var line = new
        {
            t = Math.Round(now, 1), uiFps = Math.Round(uiFrames / span, 1),
            renderFps = Math.Round((completedRenderFrames - lastCompleted) / span, 1),
            uiFrameP50 = Math.Round(uiFrameMs[uiFrameMs.Count / 2], 2), uiFrameP95 = Math.Round(uiFrameMs[(int)(uiFrameMs.Count * .95)], 2),
            gpuMs = Math.Round(gpuMilliseconds, 2), context, msPerSecond = snapshot,
        };
        writer.WriteLine(JsonSerializer.Serialize(line));
        windowStart = now; uiFrames = 0; lastCompleted = completedRenderFrames; uiFrameMs.Clear();
    }

    public void Dispose() => writer.Dispose();
}
