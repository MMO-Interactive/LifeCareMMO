using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Shared.Contracts.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Vortice.DXGI;

internal static class WeatherPerformanceProbe
{
    private static readonly string[] Scenarios = ["mature-wide", "mature-tornado", "sunset-tornado"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };
    private static readonly FieldInfo ExportCompleted = Field("frameExportCompleted");
    private static readonly FieldInfo Options = Field("options");

    // The factory supplies a fixed physical scene once per case. Only FrameNumber
    // changes thereafter, keeping animation, lighting, particles and camera fixed.
    public static async Task<int> RunAsync(
        Dx12RenderBackend backend, NativeSurface surface,
        Func<string, long, RenderFrame> createFrame, string output, string staging,
        long firstFrameNumber = 0, int warmupFrames = 24, int measurementFrames = 48,
        CancellationToken cancellationToken = default)
    {
        await MeasureAsync(backend, surface, createFrame, output, staging,
            firstFrameNumber, warmupFrames, measurementFrames, cancellationToken);
        return 0;
    }

    public static async Task<WeatherPerformanceProbeResult> MeasureAsync(
        Dx12RenderBackend backend, NativeSurface surface,
        Func<string, long, RenderFrame> createFrame, string output, string staging,
        long firstFrameNumber = 0, int warmupFrames = 24, int measurementFrames = 48,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(createFrame);
        if (firstFrameNumber < 0 || warmupFrames < 8 || measurementFrames < 2)
            throw new ArgumentOutOfRangeException(nameof(firstFrameNumber), "Use nonnegative frame IDs, at least eight warmups and two measurements.");
        var options = Options.GetValue(backend) as RendererOptions
            ?? throw new InvalidOperationException("Unable to inspect native renderer options.");
        output = Path.GetFullPath(output);
        staging = Path.GetFullPath(staging);
        if (!options.EnableGpuTimings || options.AllowSoftwareAdapter ||
            string.IsNullOrWhiteSpace(options.FrameExportPath) ||
            !string.Equals(Path.GetFullPath(options.FrameExportPath), staging, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The performance probe requires hardware DX12, EnableGpuTimings=true, and FrameExportPath matching staging.");
        if ((backend.LastGpuTiming?.FrameNumber ?? -1) >= firstFrameNumber ||
            (backend.LastFrameStatistics.BatchCount > 0 && backend.LastFrameStatistics.FrameNumber >= firstFrameNumber))
            throw new InvalidOperationException("firstFrameNumber must follow every frame already submitted to this backend.");
        Directory.CreateDirectory(output);
        int fixedWidth = surface.Width, fixedHeight = surface.Height;
        var hardware = Hardware(backend);
        var otherShowcases = OtherShowcaseProcesses();
        if (otherShowcases.Length != 0)
            throw new InvalidOperationException("Close the other PMWeather showcase GPU workload before measuring. Process IDs: " + string.Join(", ", otherShowcases));

        long nextFrameNumber = firstFrameNumber;
        var cases = new List<WeatherPerformanceCase>();
        // Disable native PNG readbacks before even the first warmup. The single
        // capture after each measured case is deliberately outside its sample set.
        ExportCompleted.SetValue(backend, true);
        try
        {
            foreach (string name in Scenarios)
            {
                var fixedScene = createFrame(name, nextFrameNumber);
                if (fixedScene.FrameNumber != nextFrameNumber || fixedScene.AnimationTimeSeconds is not { } time || !float.IsFinite(time))
                    throw new InvalidOperationException($"{name}: factory must use the supplied frame ID and a finite fixed AnimationTimeSeconds.");
                for (int frame = 0; frame < warmupFrames; frame++)
                    await SubmitAsync(fixedScene with { FrameNumber = nextFrameNumber++ });

                long measurementStart = nextFrameNumber;
                var submitted = new Dictionary<long, WeatherPerformanceSubmission>();
                var samples = new List<WeatherPerformanceMeasurement>(measurementFrames);
                var observed = new HashSet<long>();
                int repeated = 0, preceding = 0, missing = 0;
                ulong frequency = 0;
                // LastGpuTiming is delayed and exposes only the newest completed
                // slot. Extra ordinary frames cover latency/coalesced publications;
                // never count the same GPU frame twice or insert profiling waits.
                int submissionLimit = checked(measurementFrames * 4 + 24);
                while (samples.Count < measurementFrames && submitted.Count < submissionLimit)
                {
                    var frame = fixedScene with { FrameNumber = nextFrameNumber++ };
                    long started = Stopwatch.GetTimestamp();
                    await SubmitAsync(frame);
                    var statistics = backend.LastFrameStatistics;
                    if (statistics.FrameNumber != frame.FrameNumber || !statistics.RequiredResourcesResident || statistics.OpaqueBatchCount == 0)
                        throw new InvalidOperationException($"{name}: frame {frame.FrameNumber} did not submit resident native opaque geometry.");
                    submitted.Add(frame.FrameNumber, new(statistics, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                    var timing = backend.LastGpuTiming;
                    if (timing is null) { missing++; continue; }
                    if (timing.FrameNumber < measurementStart) { preceding++; continue; }
                    if (!observed.Add(timing.FrameNumber)) { repeated++; continue; }
                    if (!submitted.TryGetValue(timing.FrameNumber, out var submission))
                        throw new InvalidOperationException($"{name}: GPU timing refers to unsubmitted frame {timing.FrameNumber}.");
                    ValidateTiming(timing);
                    if (frequency != 0 && frequency != timing.TimestampFrequency)
                        throw new InvalidOperationException($"{name}: GPU timestamp frequency changed during measurement.");
                    frequency = timing.TimestampFrequency;
                    samples.Add(new(timing, submission.Statistics, submission.HostRenderMilliseconds));
                }
                if (samples.Count != measurementFrames)
                    throw new InvalidOperationException($"{name}: obtained only {samples.Count}/{measurementFrames} unique completed GPU timings in {submitted.Count} frames.");

                var total = Metric(samples.Select(sample => sample.GpuTiming.TotalMilliseconds));
                var passMetrics = Enum.GetValues<RenderGpuPass>().ToDictionary(pass => pass.ToString(),
                    pass => Metric(samples.Select(sample => sample.GpuTiming.PassTimings.Single(timing => timing.Pass == pass).Milliseconds)));
                string capturePath = Path.Combine(output, "performance-" + name + ".png");
                ExportCompleted.SetValue(backend, false);
                int captureFrames = 0;
                RenderFrame captureFrame;
                // The renderer waits for eight resident export candidates. Since
                // export was disabled while timing, these candidates are untimed.
                do
                {
                    captureFrame = fixedScene with { FrameNumber = nextFrameNumber++ };
                    await SubmitAsync(captureFrame);
                    captureFrames++;
                } while (!(bool)ExportCompleted.GetValue(backend)! && captureFrames < 24);
                if (!(bool)ExportCompleted.GetValue(backend)! || !File.Exists(staging))
                    throw new InvalidOperationException($"{name}: native final PNG capture failed.");
                if (!string.Equals(staging, capturePath, StringComparison.OrdinalIgnoreCase))
                    File.Copy(staging, capturePath, overwrite: true);
                var capture = InspectCapture(capturePath, captureFrame.FrameNumber, captureFrames, surface, backend.LastFrameStatistics);
                var notes = new List<string>();
                if (total.P90Milliseconds > total.MedianMilliseconds * 1.25)
                    notes.Add("GPU frame p90 exceeds median by 25%; inspect raw samples for contention or sustained clock changes before comparing runs.");
                cases.Add(new(name, fixedScene, warmupFrames, measurementStart, nextFrameNumber - captureFrames - 1,
                    submitted.Count, measurementFrames, frequency, new(missing, preceding, repeated),
                    total, passMetrics, Metric(samples.Select(sample => sample.HostRenderMilliseconds)), samples, capture, notes));
                Console.WriteLine($"GPU {name}: frame median {total.MedianMilliseconds:0.00} ms, p90 {total.P90Milliseconds:0.00} ms; SkyOpaque median {passMetrics[nameof(RenderGpuPass.SkyOpaque)].MedianMilliseconds:0.00} ms ({samples.Count} samples).");
            }
        }
        finally
        {
            ExportCompleted.SetValue(backend, true);
        }

        string assembly = typeof(Dx12RenderBackend).Assembly.Location;
        string reportPath = Path.Combine(output, "weather-performance.json");
        var result = new WeatherPerformanceProbeResult(reportPath, nextFrameNumber, cases);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            Schema = 1, StartedFrameNumber = firstFrameNumber, NextFrameNumber = nextFrameNumber,
            Backend = backend.Info, Hardware = hardware, RendererOptions = options,
            Runtime = RuntimeInformation.FrameworkDescription, OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            EngineAssembly = assembly, EngineAssemblySha256 = Sha256(File.ReadAllBytes(assembly)),
            ShaderSha256 = new { Sky = Sha256(Encoding.UTF8.GetBytes(BuiltInShaderLibrary.SkyGradient.PixelShaderSource)),
                Vortex = Sha256(Encoding.UTF8.GetBytes(BuiltInShaderLibrary.WeatherVortex.PixelShaderSource)) },
            TimingContract = "Completed graphics-queue timestamps; presentation, native PNG copy/readback and PNG encoding excluded. Host RenderAsync durations are separately labeled and include normal fence/presentation overhead.",
            Sampling = "Fixed physical time per case; 24 default warmups, 48 default unique completed GPU frame IDs. Extra submissions cover delayed/coalesced timing publications. Median is central interpolation; p90 uses nearest rank. No profiling GPU flush is added.",
            Contention = new { OtherShowcaseProcessesAtStart = otherShowcases, OtherShowcaseProcessesAtEnd = OtherShowcaseProcesses(),
                Note = "Same-showcase workloads checked. Other applications, GPU clocks and external GPU contention cannot be established from renderer timestamps alone." },
            Cases = cases,
        }, JsonOptions));
        Console.WriteLine("Weather performance report: " + reportPath);
        return result;

        async Task SubmitAsync(RenderFrame frame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            surface.PumpMessages();
            if (surface.IsCloseRequested || surface.IsMinimized || surface.Width != fixedWidth || surface.Height != fixedHeight)
                throw new InvalidOperationException("Performance probe interrupted by close, minimize or resize.");
            await backend.RenderAsync(frame, cancellationToken);
        }
    }

    private static FieldInfo Field(string name) => typeof(Dx12RenderBackend).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("MMOI performance probe interface changed: " + name);

    private static void ValidateTiming(RenderGpuTimingSample sample)
    {
        var expected = Enum.GetValues<RenderGpuPass>();
        if (sample.TimestampFrequency == 0 || sample.PassTimings.Count != expected.Length ||
            sample.PassTimings.Select(pass => pass.Pass).Distinct().Count() != expected.Length ||
            sample.PassTimings.Any(pass => !Enum.IsDefined(pass.Pass) || !double.IsFinite(pass.Milliseconds) || pass.Milliseconds < 0) ||
            sample.TotalMilliseconds <= 0 ||
            sample.PassTimings.Where(pass => pass.Pass != RenderGpuPass.Frame).Sum(pass => pass.Milliseconds) > sample.TotalMilliseconds + 0.001)
            throw new InvalidDataException($"Invalid GPU timestamp sample for frame {sample.FrameNumber}.");
    }

    private static WeatherPerformanceMetric Metric(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        double mean = sorted.Average();
        double median = sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * 0.5 : sorted[sorted.Length / 2];
        return new(sorted.Length, median, sorted[(int)Math.Ceiling(sorted.Length * 0.9) - 1], sorted[0], sorted[^1], mean,
            Math.Sqrt(sorted.Average(value => (value - mean) * (value - mean))));
    }

    private static object Hardware(Dx12RenderBackend backend)
    {
        var adapter = Field("adapter").GetValue(backend) as IDXGIAdapter1
            ?? throw new InvalidOperationException("Unable to record selected native DX12 adapter identity.");
        var description = adapter.Description1;
        if ((description.Flags & AdapterFlags.Software) != 0)
            throw new InvalidOperationException("Performance comparisons require a hardware DX12 adapter.");
        return new { Name = description.Description.Trim(), description.VendorId, description.DeviceId, description.SubsystemId,
            description.Revision, DedicatedVideoMemoryBytes = (ulong)description.DedicatedVideoMemory,
            DedicatedSystemMemoryBytes = (ulong)description.DedicatedSystemMemory, SharedSystemMemoryBytes = (ulong)description.SharedSystemMemory,
            Flags = description.Flags.ToString() };
    }

    private static int[] OtherShowcaseProcesses()
    {
        using var current = Process.GetCurrentProcess();
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("PmWeather.Showcase"))
        {
            using (process) if (process.Id != current.Id) ids.Add(process.Id);
        }
        return ids.Order().ToArray();
    }

    private static WeatherPerformanceCapture InspectCapture(string path, long frameNumber, int frames,
        NativeSurface surface, RenderBackendFrameStatistics statistics)
    {
        if (statistics.FrameNumber != frameNumber || !statistics.RequiredResourcesResident || statistics.OpaqueBatchCount == 0)
            throw new InvalidDataException("Native performance capture lacks resident depth geometry.");
        using var image = Image.Load<Rgba32>(path);
        if (image.Width != surface.Width || image.Height != surface.Height)
            throw new InvalidDataException("Native performance capture dimensions mismatch.");
        byte[] pixels = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixels);
        double sum = 0, sumSquares = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double luminance = (pixels[i] * 0.2126 + pixels[i + 1] * 0.7152 + pixels[i + 2] * 0.0722) / 255;
            sum += luminance;
            sumSquares += luminance * luminance;
        }
        double count = image.Width * image.Height, mean = sum / count, variance = Math.Max(0, sumSquares / count - mean * mean);
        if (mean < 0.005 || variance < 0.0001) throw new InvalidDataException("Native performance capture is blank.");
        return new(path, frameNumber, frames, image.Width, image.Height, Sha256(File.ReadAllBytes(path)), mean, variance, statistics);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal sealed record WeatherPerformanceProbeResult(string ReportPath, long NextFrameNumber, IReadOnlyList<WeatherPerformanceCase> Cases);
internal sealed record WeatherPerformanceMetric(int SampleCount, double MedianMilliseconds, double P90Milliseconds,
    double MinimumMilliseconds, double MaximumMilliseconds, double MeanMilliseconds, double StandardDeviationMilliseconds);
internal sealed record WeatherPerformanceSubmission(RenderBackendFrameStatistics Statistics, double HostRenderMilliseconds);
internal sealed record WeatherPerformanceMeasurement(RenderGpuTimingSample GpuTiming, RenderBackendFrameStatistics Statistics, double HostRenderMilliseconds);
internal sealed record WeatherPerformanceTimingObservations(int MissingSamplePolls, int PrecedingFramePolls, int RepeatedFramePolls);
internal sealed record WeatherPerformanceCapture(string Path, long FrameNumber, int UntimedCaptureFrames, int Width, int Height,
    string Sha256, double MeanLuminance, double LuminanceVariation, RenderBackendFrameStatistics Statistics);
internal sealed record WeatherPerformanceCase(string Name, RenderFrame FixedScene, int WarmupFrames,
    long FirstMeasurementFrame, long LastMeasurementSubmissionFrame, int SubmittedMeasurementFrames, int AcceptedMeasurements,
    ulong TimestampFrequency, WeatherPerformanceTimingObservations TimingObservations, WeatherPerformanceMetric TotalGpu,
    IReadOnlyDictionary<string, WeatherPerformanceMetric> GpuPasses, WeatherPerformanceMetric HostRender,
    IReadOnlyList<WeatherPerformanceMeasurement> Measurements, WeatherPerformanceCapture Capture, IReadOnlyList<string> Notes);
