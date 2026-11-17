using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Engine.Rendering.Backend;
using Engine.Rendering.Pipeline;
using Shared.Contracts.Rendering;

/// <summary>Exercises the live-demo queue and native message pump with a deliberately stalled renderer, without a GPU.</summary>
internal static class DemoThreadProbe
{
    public static int Run(NativeSurface surface, string output, Func<long, RenderFrame> createFrame)
    {
        int uiThread = Environment.CurrentManagedThreadId;
        var renderSurface = new DemoRenderSurface(surface.WindowHandle, surface.Width, surface.Height);
        using var backend = new StalledBackend(renderSurface, surface.WindowHandle);
        var host = new DedicatedRenderBackendHost("PMWeather Thread Probe", maximumQueuedFrames: 1);
        double messageMilliseconds = 0;
        RenderFrame? newest = null;
        try
        {
            DemoThreadPump.Wait(Task.Run(() => host.Reconfigure(() => new DemoRenderBackend(backend, renderSurface))), surface);
            host.SubmitLatest(createFrame(100));
            DemoThreadPump.Wait(Task.Run(() => Check(backend.FirstRenderStarted.Wait(5_000), "Renderer did not start.")), surface);
            bool closePreset = surface.CloseCamera;
            var started = Stopwatch.GetTimestamp();
            Check(PostMessageW(surface.WindowHandle, 0x0100, 0x09, 0), "Could not post a native Tab message.");
            surface.PumpMessages();
            messageMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Check(surface.CloseCamera != closePreset, "Native input was not processed while rendering was stalled.");
            var camera = new FlyCamera();
            for (int index = 1; index <= 10; index++)
            {
                camera.Look(10, -2);
                newest = createFrame(100 + index) with
                {
                    Camera = camera.ToCameraState(), Viewport = new(surface.Width + index, surface.Height + index),
                };
                host.SubmitLatest(newest);
            }
            var queued = host.GetProgressSnapshot();
            Check(queued.CompletedFrameCount == 0 && queued.QueuedFrameCount == 1 && queued.SupersededFrameCount == 9,
                "SubmitLatest did not retain exactly one newest pending camera snapshot.");
            backend.ReleaseFirstRender.Set();
            DemoThreadPump.Wait(Task.Run(() => Check(backend.SecondRenderCompleted.Wait(5_000), "Newest frame did not render.")), surface);
            DemoThreadPump.Wait(Task.Run(() =>
            {
                var timeout = Stopwatch.StartNew();
                while (host.GetProgressSnapshot().CompletedFrameCount < 2 && timeout.ElapsedMilliseconds < 5_000) Thread.Sleep(1);
                Check(host.GetProgressSnapshot().CompletedFrameCount == 2, "Completed counters did not track rendered frames.");
            }), surface);
            Check(backend.Frames.Count == 2 && backend.Frames[0].FrameNumber == 0 && backend.Frames[1].FrameNumber == 1,
                "Superseded UI snapshots broke consecutive renderer frame numbers.");
            Check(backend.Frames[1].Camera == newest!.Camera && backend.Frames[1].Viewport == newest.Viewport
                && backend.Frames[1].AnimationTimeSeconds == newest.AnimationTimeSeconds,
                "Newest camera/size/time snapshot was not preserved.");
            Check(host.LastGpuTiming?.TotalMilliseconds == 12 && host.LastFrameResolution?.OutputWidth == newest.Viewport.Width
                && host.LastFrameStatistics.FrameNumber == 1, "Renderer diagnostics were not forwarded.");
            Check(PostMessageW(surface.WindowHandle, 0x0010, 0, 0), "Could not post native WM_CLOSE.");
            surface.PumpMessages();
            Check(surface.IsCloseRequested && IsWindow(surface.WindowHandle), "Close destroyed the window before renderer disposal.");
        }
        finally
        {
            backend.ReleaseFirstRender.Set();
            DemoThreadPump.Wait(Task.Run(host.Dispose), surface);
        }
        host.ThrowIfFaulted();
        Check(backend.DisposeThread == backend.RenderThread && backend.InitializeThread == backend.RenderThread
            && backend.RenderThread != uiThread && backend.WindowAliveDuringDispose,
            "Backend ownership or disposal-before-window-destruction was violated.");
        string path = Path.Combine(output, "demo-thread-smoke.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Passed = true, NativeGpu = false, UiThread = uiThread, backend.RenderThread,
            NativeInputMillisecondsWhileRendererStalled = messageMilliseconds,
            Progress = host.GetProgressSnapshot(), ConsecutiveRenderedFrames = backend.Frames.Select(frame => frame.FrameNumber),
            NewestCamera = newest!.Camera, backend.WindowAliveDuringDispose,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Demo thread smoke passed: native input handled in {messageMilliseconds:0.00} ms while renderer stalled; latest snapshot and consecutive history preserved. {path}");
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StalledBackend(DemoRenderSurface surface, nint window) : IRenderBackend, IDisposable,
        IRenderBackendFrameStatisticsSource, IRenderBackendResolutionSource, IRenderBackendGpuTimingSource
    {
        public ManualResetEventSlim FirstRenderStarted { get; } = new();
        public ManualResetEventSlim ReleaseFirstRender { get; } = new();
        public ManualResetEventSlim SecondRenderCompleted { get; } = new();
        public List<RenderFrame> Frames { get; } = [];
        public int InitializeThread { get; private set; }
        public int RenderThread { get; private set; }
        public int DisposeThread { get; private set; }
        public bool WindowAliveDuringDispose { get; private set; }
        public RenderBackendInfo Info { get; } = new("Stalled CPU Probe", "CPU", new(false, false, false, false));
        public RenderBackendFrameStatistics LastFrameStatistics => new() { FrameNumber = Frames[^1].FrameNumber };
        public RenderResolution? LastFrameResolution => RenderResolution.Create(surface.Width, surface.Height);
        public RenderGpuTimingSample? LastGpuTiming => new(Frames[^1].FrameNumber, 1_000,
            [new(RenderGpuPass.Frame, 12)]);

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeThread = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        }

        public Task RenderAsync(RenderFrame frame, CancellationToken cancellationToken)
        {
            RenderThread = Environment.CurrentManagedThreadId;
            Check(RenderThread == InitializeThread && surface.Width == frame.Viewport.Width && surface.Height == frame.Viewport.Height,
                "Renderer thread or immutable surface size changed.");
            Frames.Add(frame);
            if (Frames.Count == 1)
            {
                FirstRenderStarted.Set();
                Check(ReleaseFirstRender.Wait(10_000), "Stalled renderer was not released.");
            }
            else SecondRenderCompleted.Set();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeThread = Environment.CurrentManagedThreadId;
            WindowAliveDuringDispose = IsWindow(window);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            FirstRenderStarted.Dispose();
            ReleaseFirstRender.Dispose();
            SecondRenderCompleted.Dispose();
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nuint parameter, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
