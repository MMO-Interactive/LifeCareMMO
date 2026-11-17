using Engine.Rendering.Backend;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Platform;
using Shared.Contracts.Rendering;

/// <summary>Keeps renderer history and surface size tied to frames actually consumed by SubmitLatest.</summary>
internal sealed class DemoRenderBackend(IRenderBackend backend, DemoRenderSurface surface,
    Action? initialized = null, Action? disposing = null,
    Action<IRenderBackend>? beforeFrame = null, Action<IRenderBackend, RenderFrame>? afterFrame = null) : IRenderBackend,
    IRenderBackendFrameStatisticsSource, IRenderBackendResolutionSource, IRenderBackendGpuTimingSource
{
    private long renderedFrameNumber;
    private bool isInitialized;

    public RenderBackendInfo Info => backend.Info;
    public RenderBackendFrameStatistics LastFrameStatistics =>
        (backend as IRenderBackendFrameStatisticsSource)?.LastFrameStatistics ?? default;
    public RenderResolution? LastFrameResolution => (backend as IRenderBackendResolutionSource)?.LastFrameResolution;
    public RenderGpuTimingSample? LastGpuTiming => (backend as IRenderBackendGpuTimingSource)?.LastGpuTiming;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        backend.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
        isInitialized = true;
        initialized?.Invoke();
        return Task.CompletedTask;
    }

    public async Task RenderAsync(RenderFrame frame, CancellationToken cancellationToken)
    {
        surface.SetViewport(frame.Viewport);
        // The public host tracks UI submission IDs; temporal histories require a
        // consecutive sequence even when the host supersedes pending snapshots.
        beforeFrame?.Invoke(backend);
        var rendered = frame with { FrameNumber = renderedFrameNumber++ };
        await backend.RenderAsync(rendered, cancellationToken);
        afterFrame?.Invoke(backend, rendered);
    }

    public ValueTask DisposeAsync()
    {
        try { if (isInitialized) disposing?.Invoke(); }
        finally { backend.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Render-thread-owned dimensions from one immutable UI frame snapshot.</summary>
internal sealed class DemoRenderSurface(nint windowHandle, int width, int height) : IRenderSurfaceHost
{
    public nint WindowHandle { get; } = windowHandle;
    // The UI keeps this HWND alive until the dedicated host finishes disposing.
    public bool IsCreated => WindowHandle != nint.Zero;
    public bool IsCloseRequested => false;
    public int Width { get; private set; } = width;
    public int Height { get; private set; } = height;

    public void SetViewport(RenderViewport viewport)
    {
        Width = viewport.Width;
        Height = viewport.Height;
    }
}

internal static class DemoThreadPump
{
    public static void Wait(Task task, NativeSurface surface)
    {
        while (!task.IsCompleted)
        {
            surface.PumpMessages();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
}
